using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Storage;

/// <summary>
/// Keeps each library's cover state and cover files under the server's data folder, not the plugin's own
/// folder, so they survive plugin upgrades. Layout under <c>library-cover-maker</c>:
/// <c>{library}/state.json</c>, <c>{library}/covers/{cover}.webp</c> and <c>.jpg</c>, and <c>tmp</c> for renders
/// in progress. Folder and file names write the ids as 32 hex digits.
/// </summary>
/// <remarks>
/// <para>
/// Every change to a library's state goes through <see cref="Update"/> or <see cref="UpdateAsync"/>. Each holds
/// that library's own semaphore from the load to the save, so two changes to one library never overwrite each
/// other, while changes to different libraries do not wait for each other. <see cref="Save"/> is internal, for the
/// store itself and for the tests; it does not take the semaphore, so nothing else may call it to change a state.
/// A library's first state is written by an Update too.
/// </para>
/// <para>
/// The semaphores are not reentrant: never call Update or UpdateAsync from inside the change they run. Other
/// changes to the same library wait until that change is done, so long work such as rendering belongs before the
/// call, not inside it.
/// </para>
/// <para>
/// Apart from the semaphores, one short lock covers the files: <see cref="Load"/>, <see cref="Save"/> and
/// <see cref="Forget"/> hold it while they read, write or delete, and never across an await. Forget does not wait
/// for an update in flight. <see cref="KnownLibraries"/> and the path members take no lock.
/// </para>
/// </remarks>
public class CoverStore
{
    private const string DataFolderName = "library-cover-maker";
    private const string StateFileName = "state.json";

    private const string UnreadableStateNotice =
        "Settings for this library could not be read and were reset; the current cover is kept until you choose.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // A JSON null where the model says non-null (a hand-edited or damaged file) is an error here, not a null
        // reference later. It applies the same way when a state is written.
        RespectNullableAnnotations = true,

        // Names, never numbers: adding an enum member later must not change what a stored value means.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly string _root;
    private readonly ILogger<CoverStore> _log;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public CoverStore(IApplicationPaths paths, ILogger<CoverStore> log)
    {
        _root = Path.Combine(paths.DataPath, DataFolderName);
        _log = log;
    }

    /// <summary>Where renders in progress are written. Whoever renders into it creates it.</summary>
    public string TempFolder => Path.Combine(_root, "tmp");

    /// <summary>Where a cover's animated WebP is stored. Whoever writes the file creates the folder.</summary>
    public string AnimatedPath(Guid libraryId, Guid coverId) => CoverPath(libraryId, coverId, ".webp");

    /// <summary>Where a cover's still JPEG is stored. Whoever writes the file creates the folder.</summary>
    public string StillPath(Guid libraryId, Guid coverId) => CoverPath(libraryId, coverId, ".jpg");

    /// <summary>
    /// The library's saved state. A library with no state file gets the defaults, starting in
    /// <paramref name="defaultMode"/>; a saved state keeps its own mode, and always gets the requested library id,
    /// whatever the file says. A state file that cannot be read as a whole state (invalid JSON, a JSON null where a
    /// value is required, other than exactly five slots, a gap in a list) is copied to <c>state.json.bad</c>,
    /// replacing an earlier copy. The library then gets the defaults, pinned and with a notice, saved at once: no
    /// schedule replaces its cover because a file was unreadable, and the next load does not repeat any of this.
    /// </summary>
    public LibraryCoverState Load(Guid libraryId, CoverMode defaultMode = CoverMode.Scenes)
    {
        var path = StatePath(libraryId);
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return new LibraryCoverState { LibraryId = libraryId, Mode = defaultMode };
            }

            try
            {
                var state = JsonSerializer.Deserialize<LibraryCoverState>(File.ReadAllText(path), JsonOptions)
                    ?? throw new JsonException("The file holds no state.");
                if (!IsComplete(state))
                {
                    throw new JsonException(
                        $"The file does not hold exactly {LibraryCoverState.MaxSlots} slots and lists without gaps.");
                }

                // The folder says which library this is, whatever the file says.
                state.LibraryId = libraryId;
                return state;
            }
            catch (JsonException ex)
            {
                // Copy before returning the defaults. If the copy fails the exception reaches the caller,
                // rather than a later Save replacing the only copy of the old state.
                var copy = path + ".bad";
                File.Copy(path, copy, overwrite: true);
                _log.LogWarning(
                    ex,
                    "Jellyfin Animated Posters: could not read {Path}, so a copy was kept as {Copy} and library "
                    + "{LibraryId} starts from the defaults, pinned so that its cover is kept",
                    path,
                    copy,
                    libraryId);

                // The settings are lost, but the cover on the library is not: pinned, it is left alone until an
                // admin chooses, and the notice tells them why.
                var pinned = new LibraryCoverState
                {
                    LibraryId = libraryId,
                    Pinned = true,
                    Notice = UnreadableStateNotice,
                    NoticeAt = DateTimeOffset.UtcNow,
                };
                Save(pinned);
                return pinned;
            }
        }
    }

    /// <summary>
    /// Saves the state in one step: it is written to a temporary file next to the real one and then moved over
    /// it, so a crash cannot leave a half written state.json, and a failure leaves no temporary file behind. A
    /// state that <see cref="Load"/> would set aside as unreadable is refused, so that saving never costs the
    /// state at the next load. Internal, for the store itself and for the tests: it does not take the library's
    /// semaphore, so a state is changed through <see cref="Update"/> or <see cref="UpdateAsync"/>.
    /// </summary>
    internal void Save(LibraryCoverState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!IsComplete(state))
        {
            throw new ArgumentException(
                $"A state needs exactly {LibraryCoverState.MaxSlots} slots and lists without gaps.",
                nameof(state));
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var path = StatePath(state.LibraryId);
        var temp = path + ".tmp";
        lock (_lock)
        {
            Directory.CreateDirectory(LibraryFolder(state.LibraryId));
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    /// <summary>
    /// Changes a library's state in one step that no other change to the same library can interleave with: loads
    /// the state (<paramref name="defaultMode"/> as in <see cref="Load"/>), runs <paramref name="change"/> on it,
    /// saves it and returns it. If <paramref name="change"/> throws, nothing of the change is saved and the
    /// exception reaches the caller; the only thing saved before the change runs is the replacement of an unreadable
    /// state file by its pinned defaults, which <see cref="Load"/> does. For short changes made from synchronous
    /// code: it blocks the thread while it waits for its turn, so asynchronous code should use
    /// <see cref="UpdateAsync"/>. See the class remarks.
    /// </summary>
    public LibraryCoverState Update(
        Guid libraryId,
        Action<LibraryCoverState> change,
        CoverMode defaultMode = CoverMode.Scenes)
    {
        ArgumentNullException.ThrowIfNull(change);

        var gate = GateOf(libraryId);
        gate.Wait();
        try
        {
            var state = Load(libraryId, defaultMode);
            change(state);
            state.LibraryId = libraryId; // the state saved is always the one of the library asked for
            Save(state);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The same as <see cref="Update"/>, for a change that awaits, such as applying an image through Jellyfin, and
    /// with the same rule when the change throws: nothing of the change is saved, though an unreadable state file
    /// has by then been replaced by its pinned defaults, inside <see cref="Load"/>. The change receives
    /// <paramref name="cancellationToken"/>; the token also ends the wait for a turn, and then the change does not
    /// run. Once the change has finished, a cancellation does not stop the save. See the class remarks.
    /// </summary>
    public async Task<LibraryCoverState> UpdateAsync(
        Guid libraryId,
        Func<LibraryCoverState, CancellationToken, Task> change,
        CoverMode defaultMode = CoverMode.Scenes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        var gate = GateOf(libraryId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = Load(libraryId, defaultMode);
            await change(state, cancellationToken).ConfigureAwait(false);
            state.LibraryId = libraryId; // the state saved is always the one of the library asked for
            Save(state);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The libraries that have a folder in the store, whether or not they still exist in Jellyfin.</summary>
    public IReadOnlyList<Guid> KnownLibraries()
    {
        var known = new List<Guid>();
        if (Directory.Exists(_root))
        {
            foreach (var folder in Directory.EnumerateDirectories(_root))
            {
                if (Guid.TryParseExact(Path.GetFileName(folder), "N", out var id))
                {
                    known.Add(id);
                }
            }
        }

        return known;
    }

    /// <summary>
    /// Removes everything stored for a library, such as one that no longer exists. Does nothing when there is nothing.
    /// </summary>
    public void Forget(Guid libraryId)
    {
        var folder = LibraryFolder(libraryId);
        lock (_lock)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    ex,
                    "Jellyfin Animated Posters: could not remove the stored covers of library {LibraryId}",
                    libraryId);
            }
        }
    }

    // A state is complete when it has all its slots and no gap in its lists. A JSON null in a property that the
    // model declares non-null is already refused by the serializer (see JsonOptions), so only the lists remain.
    private static bool IsComplete(LibraryCoverState state) =>
        state.Slots is { Length: LibraryCoverState.MaxSlots } slots
        && Array.TrueForAll(slots, slot => slot is not null)
        && state.History is { } history
        && history.TrueForAll(cover => cover is not null);

    private SemaphoreSlim GateOf(Guid libraryId) => _gates.GetOrAdd(libraryId, static _ => new SemaphoreSlim(1, 1));

    private string LibraryFolder(Guid libraryId) => Path.Combine(_root, libraryId.ToString("N"));

    /// <summary>Where a library's state is saved. Every save writes a new file and moves it over this one.</summary>
    internal string StatePath(Guid libraryId) => Path.Combine(LibraryFolder(libraryId), StateFileName);

    private string CoverPath(Guid libraryId, Guid coverId, string extension) =>
        Path.Combine(LibraryFolder(libraryId), "covers", coverId.ToString("N") + extension);

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
            _log.LogWarning(ex, "Jellyfin Animated Posters: could not delete {Path}", path);
        }
    }
}
