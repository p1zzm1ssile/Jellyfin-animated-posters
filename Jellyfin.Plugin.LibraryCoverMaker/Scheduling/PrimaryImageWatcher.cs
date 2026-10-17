using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// Notices a library's Primary image being changed outside the plugin, in Jellyfin's Edit images dialog, and keeps it
/// as an uploaded cover, which pins the library (spec 7.3): an admin who picks an image there wants that image.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin raises <c>ItemUpdated</c> for a library after an upload in the dialog and after the plugin's own apply.
/// The plugin's apply raises it synchronously, on the thread of the apply, while that library's gate in the store is
/// held, so the handler asks <see cref="CoverApplier.IsApplying"/> first and leaves at once when it is true. Nothing in
/// the handler waits for the store: every other event is checked later, one at a time, on the thread pool.
/// </para>
/// <para>
/// The image is an outside change when it is not the one the plugin applied: another path, or another modification
/// time. A library the plugin never applied a cover to has nothing to compare with, so there only an image written in
/// the last minutes counts: a refresh of the library, or an upload of its Logo in the same dialog, also raises the
/// event, with an image an admin set long ago. The image Jellyfin draws by itself (a collage of the titles) is never an
/// upload.
/// </para>
/// </remarks>
public sealed class PrimaryImageWatcher : IHostedService, IDisposable
{
    /// <summary>What the card of a library pinned this way says.</summary>
    internal const string EditImagesNotice =
        "Pinned: the Primary image was changed in Jellyfin's Edit images dialog, so it is kept as an uploaded cover.";

    // How recent an image must be to count as an upload in a library the plugin never applied a cover to.
    private static readonly TimeSpan JustWritten = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraries;
    private readonly CoverApplier _applier;
    private readonly ILogger<PrimaryImageWatcher> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _pending = Task.CompletedTask;

    public PrimaryImageWatcher(ILibraryManager libraries, CoverApplier applier, ILogger<PrimaryImageWatcher> log)
        : this(libraries, applier, log, () => DateTime.UtcNow)
    {
    }

    /// <summary>For the tests: the clock comes from the caller.</summary>
    internal PrimaryImageWatcher(
        ILibraryManager libraries,
        CoverApplier applier,
        ILogger<PrimaryImageWatcher> log,
        Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(utcNow);

        _libraries = libraries;
        _applier = applier;
        _log = log;
        _utcNow = utcNow;
    }

    /// <summary>The checks asked for so far, done when they are. For the tests.</summary>
    internal Task Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraries.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraries.ItemUpdated -= OnItemUpdated;

        Task pending;
        lock (_lock)
        {
            _stopping.Cancel();
            pending = _pending;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host stopped waiting; the check was told to stop and ends on its own.
        }
    }

    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Whether <paramref name="image"/>, the library's Primary image now, was set outside the plugin, given the
    /// library's <paramref name="state"/>.
    /// </summary>
    internal static bool IsOutsideChange(LibraryCoverState state, PrimaryImage? image, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (image is null || image.Automatic || !image.IsLocalFile)
        {
            return false;
        }

        if (state.AppliedPrimaryPath is null)
        {
            return (utcNow - image.Modified).Duration() <= JustWritten;
        }

        return !string.Equals(image.Path, state.AppliedPrimaryPath, StringComparison.Ordinal)
            || state.AppliedPrimaryModified is not { } applied
            || (image.Modified - applied).Duration() > CoverApplier.AppliedTimeTolerance;
    }

    /// <summary>
    /// <c>ILibraryManager.ItemUpdated</c>. Raised on the thread that wrote the item, also from inside the plugin's own
    /// apply, so it never waits: a check is queued behind the ones before it. Never throws, since Jellyfin would log
    /// that as an error.
    /// </summary>
    internal void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is not CollectionFolder library || (e.UpdateReason & ItemUpdateType.ImageUpdate) == 0)
            {
                return;
            }

            // The plugin's own apply: its gate in the store is held on this very thread.
            var libraryId = library.Id;
            if (_applier.IsApplying(libraryId))
            {
                return;
            }

            // When the event came, not when its check runs: a check waiting behind others must not age the image.
            var seenAt = _utcNow();
            lock (_lock)
            {
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                var token = _stopping.Token;
                _pending = _pending.ContinueWith(
                    _ => CheckAsync(libraryId, seenAt, token),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jellyfin Animated Posters: could not look at an update of a library's images");
        }
    }

    private async Task CheckAsync(Guid libraryId, DateTime seenAt, CancellationToken ct)
    {
        try
        {
            var kept = await _applier.KeepOutsideUploadAsync(
                libraryId,
                (state, image) => IsOutsideChange(state, image, seenAt),
                EditImagesNotice,
                ct).ConfigureAwait(false);
            if (kept)
            {
                _log.LogInformation(
                    "Jellyfin Animated Posters: the Primary image of library {LibraryId} was changed outside the "
                    + "plugin, so it is kept as an uploaded cover and the library is pinned",
                    libraryId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Jellyfin Animated Posters: could not keep the new Primary image of library {LibraryId} as an uploaded "
                + "cover",
                libraryId);
        }
    }
}
