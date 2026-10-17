using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Covers;

/// <summary>
/// Makes a cover the library's Primary image, through Jellyfin's own <c>SaveImage</c>, and keeps the image the library
/// had before the plugin first replaced it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here changes a library's state, so it runs inside <see cref="CoverStore.UpdateAsync"/>. A change that
/// applies a cover goes through an <see cref="ApplyRun"/> (<see cref="StartApply"/>): it marks the library as applying
/// from the apply until the state that records the new image is saved, and deletes the files of covers that drop out
/// of the history only once that save went through. <see cref="SaveUploadAsync"/> and <see cref="ReapplyAllAsync"/>
/// use one themselves; the builder and the admin API do the same around their own changes.
/// </para>
/// <para>
/// Saving a Primary image makes Jellyfin raise <c>ItemUpdated</c> for the library, which is also what an admin's
/// upload in the Edit images dialog raises. The state records where Jellyfin put the image
/// (<see cref="LibraryCoverState.AppliedPrimaryPath"/>) as soon as <c>SaveImage</c> returns, before the item is
/// written to the database, which is what raises the event.
/// </para>
/// <para>
/// For the watcher of <c>ItemUpdated</c> (Task 10): the event is raised synchronously from the database write, on the
/// thread of the apply, while that library's gate in the store is held. So the watcher checks
/// <see cref="IsApplying"/> first, ignores the event when it is true, and never calls <see cref="CoverStore.Update"/>
/// or <see cref="CoverStore.UpdateAsync"/> from inside the event handler before that check: the gate would never be
/// free. When the watcher records an Edit images upload as an Uploaded cover, it also sets
/// <see cref="LibraryCoverState.AppliedPrimaryPath"/> and <see cref="LibraryCoverState.AppliedPrimaryModified"/> to
/// that image; otherwise the next apply in a library without an original would keep the upload as the original.
/// </para>
/// <para>
/// Files: a generated cover is its animated WebP and its still JPEG (<see cref="CoverStore.AnimatedPath"/>,
/// <see cref="CoverStore.StillPath"/>). An uploaded cover and the original are one file each, named by the cover id
/// with the extension of the image (<c>.webp</c>, <c>.jpg</c>, <c>.png</c>, <c>.gif</c> and, for an original, whatever
/// Jellyfin had); <see cref="CoverFile"/> finds it.
/// </para>
/// </remarks>
public sealed class CoverApplier
{
    // The buffer of the file streams that read and write cover files.
    private const int BufferSize = 81920;

    // The image types an admin may upload, and the extension each is stored under.
    private static readonly Dictionary<string, string> UploadTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/webp"] = ".webp",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
    };

    private readonly ILibraryImages _images;
    private readonly CoverStore _store;
    private readonly ILogger _log;
    private readonly Func<PluginConfiguration> _config;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _applying = new();

    public CoverApplier(
        IProviderManager providers,
        ILibraryManager library,
        CoverStore store,
        ILogger<CoverApplier> log)
        : this(new JellyfinLibraryImages(providers, library), store, log, Settings)
    {
    }

    // For the tests: Jellyfin's side and the configuration are replaced.
    internal CoverApplier(ILibraryImages images, CoverStore store, ILogger log, Func<PluginConfiguration> config)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(config);

        _images = images;
        _store = store;
        _log = log;
        _config = config;
    }

    /// <summary>
    /// Whether the plugin is changing this library's Primary image right now, or has changed it and not yet saved the
    /// state that says so, so that the <c>ItemUpdated</c> event it causes is not taken for an admin's upload.
    /// </summary>
    public bool IsApplying(Guid libraryId)
    {
        lock (_lock)
        {
            return _applying.ContainsKey(libraryId);
        }
    }

    /// <summary>
    /// The file of a cover that would become the Primary image, with its MIME type, or null when it is missing. A
    /// generated cover gives the version <paramref name="format"/> asks for; an uploaded cover and the original have
    /// one file, whatever the format.
    /// </summary>
    public static (string Path, string MimeType)? CoverFile(
        CoverStore store,
        Guid libraryId,
        CoverRecord record,
        PrimaryFormat format)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);

        if (record.Origin == CoverOrigin.Generated)
        {
            var (path, mimeType) = format == PrimaryFormat.Still
                ? (store.StillPath(libraryId, record.Id), "image/jpeg")
                : (store.AnimatedPath(libraryId, record.Id), "image/webp");
            return File.Exists(path) ? (path, mimeType) : null;
        }

        var single = SingleFiles(store, libraryId, record.Id).FirstOrDefault();
        return single is null ? null : (single, MimeTypes.GetMimeType(single));
    }

    /// <summary>
    /// Starts one change of a library's state that applies a cover. Use it around
    /// <see cref="CoverStore.UpdateAsync"/>: call <see cref="ApplyRun.ApplyAsync"/> and
    /// <see cref="ApplyRun.AddToHistory"/> inside the change, <see cref="ApplyRun.Saved"/> after UpdateAsync returned,
    /// <see cref="ApplyRun.Unsaved"/> when it threw, and dispose the run last.
    /// </summary>
    public ApplyRun StartApply(Guid libraryId) => new(this, libraryId);

    /// <summary>
    /// Makes <paramref name="record"/> the library's Primary image and its current cover, with the library marked as
    /// applying for the length of this call only. Called from inside <see cref="CoverStore.UpdateAsync"/> with the
    /// state it loaded; the caller saves it. A change that should keep the mark until its state is saved uses
    /// <see cref="StartApply"/> instead.
    /// </summary>
    /// <remarks>
    /// The first time, the image the library has is copied into the store as its
    /// <see cref="LibraryCoverState.Original"/>, unless it is the plugin's own (the path the state recorded when the
    /// plugin last applied a cover, or a file identical to one of the store's covers) or not a file on this server.
    /// Cancellation is honored until <c>SaveImage</c> starts; from there the apply runs to its end, so that the
    /// library never shows a cover the state does not know about. The library's item is looked up once, and that one
    /// instance is saved, read back and written to the database, as Jellyfin's own upload does.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The cover's file is missing. Nothing was changed.</exception>
    /// <exception cref="InvalidOperationException">The library no longer exists. Nothing was changed.</exception>
    /// <exception cref="OperationCanceledException">
    /// Cancelled before the image was touched. Nothing was changed.
    /// </exception>
    public async Task ApplyAsync(
        LibraryCoverState state,
        CoverRecord record,
        PrimaryFormat format,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);

        var libraryId = state.LibraryId;
        var file = CoverFile(_store, libraryId, record, format)
            ?? throw new FileNotFoundException($"The file of cover {record.Id:N} is missing.");
        ct.ThrowIfCancellationRequested();
        var library = _images.Find(libraryId)
            ?? throw new InvalidOperationException($"Library {libraryId:N} no longer exists.");

        Mark(libraryId);
        try
        {
            var original = KeepOriginal(state, library);
            try
            {
                ct.ThrowIfCancellationRequested();
                var stream = new FileStream(
                    file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    await library.SaveAsync(stream, file.MimeType, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // The library keeps its image, so it is not the original yet: the copy goes, and the next apply
                // makes it again.
                if (original is not null)
                {
                    state.Original = null;
                    DeleteSingleFiles(libraryId, original.Id);
                }

                throw;
            }

            // Recorded before the item is written to the database, which is what raises ItemUpdated.
            var applied = library.Primary();
            state.AppliedPrimaryPath = applied?.Path;
            state.AppliedPrimaryModified = applied?.Modified;
            state.CurrentCoverId = record.Id;

            try
            {
                await library.PersistAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The image file is in place and Jellyfin's item already points at it; only the database copy lags
                // until the next refresh of the library. The state must still say what the library shows.
                _log.LogError(
                    ex,
                    "Library Cover Maker: the new Primary image of library {LibraryId} is in place but could not be "
                    + "written to the database",
                    libraryId);
            }
        }
        finally
        {
            Unmark(libraryId);
        }
    }

    /// <summary>
    /// Puts <paramref name="record"/> at the front of the library's history and drops the oldest covers beyond the
    /// configured history size, like <see cref="CoverStore.AddToHistory"/>, but deletes no file: it returns the dropped
    /// covers that are no longer in use, whose files <see cref="DeleteCovers"/> removes once the state was saved. So a
    /// save that fails never leaves a state that lists covers whose files are gone. Changes only the given state.
    /// </summary>
    public IReadOnlyList<Guid> AddToHistory(LibraryCoverState state, CoverRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);

        state.History.RemoveAll(listed => listed.Id == record.Id);
        state.History.Insert(0, record);

        var keep = Math.Max(_config().HistorySize, 1);
        if (state.History.Count <= keep)
        {
            return [];
        }

        var dropped = state.History.GetRange(keep, state.History.Count - keep);
        state.History.RemoveRange(keep, dropped.Count);
        return dropped.Select(cover => cover.Id).Where(id => !IsInUse(state, id)).Distinct().ToList();
    }

    /// <summary>
    /// Deletes every file of the given covers of a library (the WebP and JPEG of a generated cover, the one file of an
    /// upload). For covers <see cref="AddToHistory"/> dropped, after the state was saved. Best effort: a file that
    /// cannot be deleted is logged and left.
    /// </summary>
    public void DeleteCovers(Guid libraryId, IEnumerable<Guid> coverIds)
    {
        ArgumentNullException.ThrowIfNull(coverIds);

        foreach (var id in coverIds)
        {
            TryDelete(_store.AnimatedPath(libraryId, id));
            TryDelete(_store.StillPath(libraryId, id));
            DeleteSingleFiles(libraryId, id);
        }
    }

    /// <summary>
    /// Makes an uploaded image the library's Primary image: it is stored as an <see cref="CoverOrigin.Uploaded"/>
    /// cover, applied, added to the history, and the library is pinned, all in one change of its state, so no
    /// render can replace it in between.
    /// </summary>
    /// <remarks>
    /// Only the MIME type is checked here. The caller (the admin API's upload endpoint) must check that the file's
    /// signature matches the type and that its size is within the limit before it calls this.
    /// </remarks>
    /// <param name="libraryId">The library.</param>
    /// <param name="image">The image, read to its end.</param>
    /// <param name="mimeType"><c>image/webp</c>, <c>image/jpeg</c>, <c>image/png</c> or <c>image/gif</c>.</param>
    /// <param name="ct">Cancels the upload until the library's image starts to change.</param>
    /// <exception cref="ArgumentException">The type is not one of those. Nothing was stored.</exception>
    public async Task SaveUploadAsync(Guid libraryId, Stream image, string mimeType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (mimeType is null || !UploadTypes.TryGetValue(mimeType, out var extension))
        {
            throw new ArgumentException(
                $"An uploaded cover must be WebP, JPEG, PNG or GIF, not '{mimeType}'.", nameof(mimeType));
        }

        var config = _config();
        var record = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = CoverOrigin.Uploaded,
        };
        var path = Path.ChangeExtension(_store.AnimatedPath(libraryId, record.Id), extension);

        using var run = StartApply(libraryId);
        try
        {
            await StoreAsync(image, path, extension, ct).ConfigureAwait(false);
            await _store.UpdateAsync(
                libraryId,
                async (state, token) =>
                {
                    record.Mode = state.Mode;
                    await run.ApplyAsync(state, record, _config().PrimaryFormat, token).ConfigureAwait(false);
                    state.Pinned = true;
                    run.AddToHistory(state, record);
                },
                config.DefaultMode,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            run.Unsaved(ex);

            // Once applied, the file is what the library shows, so it stays even if the state could not be saved.
            if (!run.Applied)
            {
                TryDelete(path);
            }

            throw;
        }

        run.Saved();
    }

    /// <summary>
    /// Applies the <paramref name="format"/> version of every library's current cover that the plugin composed,
    /// without rendering: what the global switch between the animated and the still Primary needs. Uploaded covers
    /// and originals have one version and are left as they are, and so are libraries Jellyfin no longer has. A library
    /// that fails is logged and the others go on.
    /// </summary>
    public async Task ReapplyAllAsync(PrimaryFormat format, CancellationToken ct)
    {
        var config = _config();
        foreach (var libraryId in _store.KnownLibraries())
        {
            ct.ThrowIfCancellationRequested();
            if (_images.Find(libraryId) is null)
            {
                _log.LogDebug(
                    "Library Cover Maker: library {LibraryId} no longer exists, nothing to reapply",
                    libraryId);
                continue;
            }

            using var run = StartApply(libraryId);
            try
            {
                await _store.UpdateAsync(
                    libraryId,
                    async (state, token) =>
                    {
                        var current = state.History.Find(cover => cover.Id == state.CurrentCoverId);
                        if (current is { Origin: CoverOrigin.Generated })
                        {
                            await run.ApplyAsync(state, current, format, token).ConfigureAwait(false);
                        }
                    },
                    config.DefaultMode,
                    ct).ConfigureAwait(false);
                run.Saved();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Unsaved(ex);
                _log.LogWarning(
                    ex,
                    "Library Cover Maker: could not apply the {Format} version of the cover of library {LibraryId}",
                    format,
                    libraryId);
            }
        }
    }

    /// <summary>The plugin's configuration, or the defaults while the plugin is not loaded (the tests).</summary>
    internal static PluginConfiguration Settings() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Whether <paramref name="path"/> is a file inside <paramref name="folder"/>. Jellyfin redraws a library's
    /// automatic image only while it is under the library's internal metadata folder, so that is how an automatic
    /// image is told from one an admin set.
    /// </summary>
    internal static bool IsUnder(string folder, string path)
    {
        if (string.IsNullOrEmpty(folder)
            || string.IsNullOrEmpty(path)
            || !Path.IsPathRooted(folder)
            || !Path.IsPathRooted(path))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }

    private static bool IsInUse(LibraryCoverState state, Guid coverId) =>
        state.CurrentCoverId == coverId
        || state.Original?.Id == coverId
        || state.History.Exists(cover => cover.Id == coverId);

    // The files named by the cover id with any extension: the one file of an upload or an original.
    private static IEnumerable<string> SingleFiles(CoverStore store, Guid libraryId, Guid coverId)
    {
        var folder = Path.GetDirectoryName(store.AnimatedPath(libraryId, coverId))!;
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, coverId.ToString("N") + ".*").Order(StringComparer.Ordinal)
            : [];
    }

    // Whether a file has the same bytes as one of the covers the store keeps for the library: then it is a cover the
    // plugin applied, whatever its path says (the state that recorded the path may have been lost).
    private bool MatchesAStoredCover(Guid libraryId, string path)
    {
        var folder = Path.GetDirectoryName(_store.AnimatedPath(libraryId, Guid.Empty))!;
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var length = new FileInfo(path).Length;
        byte[]? bytes = null;
        foreach (var stored in Directory.EnumerateFiles(folder))
        {
            if (new FileInfo(stored).Length != length)
            {
                continue;
            }

            bytes ??= File.ReadAllBytes(path);
            if (File.ReadAllBytes(stored).AsSpan().SequenceEqual(bytes))
            {
                return true;
            }
        }

        return false;
    }

    // The first time: copies the library's image into the store as the original, and returns its record. Null when
    // there is nothing to keep.
    private CoverRecord? KeepOriginal(LibraryCoverState state, ILibraryImage library)
    {
        if (state.Original is not null)
        {
            return null;
        }

        var current = library.Primary();
        if (current is null || string.Equals(current.Path, state.AppliedPrimaryPath, StringComparison.Ordinal))
        {
            return null; // no image, or the plugin's own cover
        }

        if (!current.IsLocalFile || !File.Exists(current.Path))
        {
            _log.LogWarning(
                "Library Cover Maker: the Primary image of library {LibraryId} ({Path}) is not a file on this server, "
                + "so it cannot be kept as the original",
                state.LibraryId,
                current.Path);
            return null;
        }

        if (MatchesAStoredCover(state.LibraryId, current.Path))
        {
            _log.LogWarning(
                "Library Cover Maker: the Primary image of library {LibraryId} ({Path}) is one of the plugin's own "
                + "covers, so it is not kept as the original",
                state.LibraryId,
                current.Path);
            return null;
        }

        var record = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = CoverOrigin.Original,
            Mode = state.Mode,
            Automatic = current.Automatic,
        };
        var extension = Path.GetExtension(current.Path).ToLowerInvariant();
        var target = Path.ChangeExtension(
            _store.AnimatedPath(state.LibraryId, record.Id),
            extension.Length > 1 ? extension : ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Copy(current.Path, target);
        }
        catch
        {
            // Without its copy the image would be lost to SaveImage, so the apply stops here, leaving no half copy.
            TryDelete(target);
            throw;
        }

        state.Original = record;

        // SaveImage deletes an automatic image, so from here this copy is the only one.
        _log.LogInformation(
            "Library Cover Maker: kept the {Kind} Primary image of library {LibraryId} as its original ({Path})",
            current.Automatic ? "automatic" : "admin-set",
            state.LibraryId,
            current.Path);
        return record;
    }

    // Writes the upload into the store by way of the temporary folder, so a half written file never has a cover's name.
    private async Task StoreAsync(Stream image, string path, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(_store.TempFolder);
        var temp = Path.Combine(_store.TempFolder, "upload-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var target = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await image.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void DeleteSingleFiles(Guid libraryId, Guid coverId)
    {
        foreach (var path in SingleFiles(_store, libraryId, coverId).ToList())
        {
            TryDelete(path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not delete {Path}", path);
        }
    }

    // A count rather than a flag: a run marks the library until its state is saved, and its apply marks it again.
    private void Mark(Guid libraryId)
    {
        lock (_lock)
        {
            _applying[libraryId] = _applying.GetValueOrDefault(libraryId) + 1;
        }
    }

    private void Unmark(Guid libraryId)
    {
        lock (_lock)
        {
            if (_applying.GetValueOrDefault(libraryId) <= 1)
            {
                _applying.Remove(libraryId);
            }
            else
            {
                _applying[libraryId]--;
            }
        }
    }

    /// <summary>
    /// One change of a library's state that applies a cover. The library is marked as applying from the first apply,
    /// which happens inside the store's gate, until the run is disposed, after the state was saved; files of covers
    /// dropped from the history are deleted only by <see cref="Saved"/>.
    /// </summary>
    public sealed class ApplyRun : IDisposable
    {
        private readonly CoverApplier _applier;
        private readonly Guid _libraryId;
        private readonly List<Guid> _dropped = new();
        private bool _marked;
        private CoverRecord? _keptOriginal;

        internal ApplyRun(CoverApplier applier, Guid libraryId)
        {
            _applier = applier;
            _libraryId = libraryId;
        }

        /// <summary>Whether a cover of this run reached the library's Primary image.</summary>
        public bool Applied { get; private set; }

        /// <summary>
        /// <see cref="CoverApplier.ApplyAsync"/>, with the mark held until the run ends. Inside the change only.
        /// </summary>
        public async Task ApplyAsync(
            LibraryCoverState state,
            CoverRecord record,
            PrimaryFormat format,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(state);
            ct.ThrowIfCancellationRequested();
            if (!_marked)
            {
                _applier.Mark(_libraryId);
                _marked = true;
            }

            var before = state.Original;
            await _applier.ApplyAsync(state, record, format, ct).ConfigureAwait(false);
            Applied = true;
            if (before is null && state.Original is { } kept)
            {
                _keptOriginal = kept;
            }
        }

        /// <summary>
        /// <see cref="CoverApplier.AddToHistory"/>; the dropped covers' files wait for <see cref="Saved"/>.
        /// </summary>
        public void AddToHistory(LibraryCoverState state, CoverRecord record) =>
            _dropped.AddRange(_applier.AddToHistory(state, record));

        /// <summary>The state was saved: the files of the covers dropped from the history go.</summary>
        public void Saved()
        {
            _applier.DeleteCovers(_libraryId, _dropped);
            _dropped.Clear();
        }

        /// <summary>
        /// The change or its save threw. When the library's image had already changed, that is logged as an error,
        /// naming the copy of the original that the unsaved state would have listed: it is the only copy left.
        /// </summary>
        public void Unsaved(Exception error)
        {
            _dropped.Clear();
            if (!Applied)
            {
                return;
            }

            var copy = _keptOriginal is null
                ? null
                : CoverFile(_applier._store, _libraryId, _keptOriginal, PrimaryFormat.Animated)?.Path;
            _applier._log.LogError(
                error,
                "Library Cover Maker: library {LibraryId} shows a new cover but its state could not be saved; the "
                + "copy of its original image, if one was made, is {Copy}",
                _libraryId,
                copy ?? "(none)");
        }

        /// <summary>Ends the mark.</summary>
        public void Dispose()
        {
            if (_marked)
            {
                _marked = false;
                _applier.Unmark(_libraryId);
            }
        }
    }
}

/// <summary>A library's Primary image as Jellyfin records it.</summary>
/// <param name="Path">Where the image is: a file, or an address when it is not on this server.</param>
/// <param name="Modified">When the file was last modified, as Jellyfin recorded it.</param>
/// <param name="IsLocalFile">Whether <paramref name="Path"/> is a file on this server.</param>
/// <param name="Automatic">
/// Whether it is the image Jellyfin draws by itself for a library (a collage of its titles), which lives under the
/// library's internal metadata folder, rather than one an admin set.
/// </param>
internal sealed record PrimaryImage(string Path, DateTime Modified, bool IsLocalFile, bool Automatic);

/// <summary>What the applier asks of Jellyfin about libraries. The tests replace it.</summary>
internal interface ILibraryImages
{
    /// <summary>The library's item, for one apply, or null when Jellyfin no longer has the library.</summary>
    ILibraryImage? Find(Guid libraryId);
}

/// <summary>One library item, looked up once for an apply, and its Primary image.</summary>
internal interface ILibraryImage
{
    /// <summary>The library's Primary image now, or null when it has none.</summary>
    PrimaryImage? Primary();

    /// <summary>Replaces the library's Primary image with <paramref name="image"/>, through <c>SaveImage</c>.</summary>
    Task SaveAsync(Stream image, string mimeType, CancellationToken ct);

    /// <summary>Writes the library's item to Jellyfin's database, as Jellyfin's own upload does after saving.</summary>
    Task PersistAsync();
}

/// <summary>Libraries' Primary images through Jellyfin's library and provider managers.</summary>
internal sealed class JellyfinLibraryImages(IProviderManager providers, ILibraryManager library) : ILibraryImages
{
    public ILibraryImage? Find(Guid libraryId) =>
        library.GetItemById(libraryId) is CollectionFolder folder ? new Item(providers, folder) : null;

    // The one instance is saved, read back and written to the database, as ImageController.SetItemImage does: a
    // second lookup could get a fresh copy from the database without the new image.
    private sealed class Item(IProviderManager providers, CollectionFolder folder) : ILibraryImage
    {
        public PrimaryImage? Primary()
        {
            var image = folder.GetImageInfo(ImageType.Primary, 0);
            if (image is null || string.IsNullOrEmpty(image.Path))
            {
                return null;
            }

            var automatic = image.IsLocalFile && CoverApplier.IsUnder(folder.GetInternalMetadataPath(), image.Path);
            return new PrimaryImage(image.Path, image.DateModified, image.IsLocalFile, automatic);
        }

        public Task SaveAsync(Stream image, string mimeType, CancellationToken ct) =>
            providers.SaveImage(folder, image, mimeType, ImageType.Primary, null, ct);

        // This raises ItemUpdated, synchronously, from LibraryManager.UpdateItemsAsync.
        public Task PersistAsync() =>
            folder.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
    }
}
