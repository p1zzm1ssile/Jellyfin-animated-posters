using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// Builds covers one at a time, in the order they were asked for. Rendering is heavy, so a single worker takes the
/// jobs from the schedule, from the renewals and from an admin pressing the button, and it waits while someone is
/// watching something. A job asked for by an admin (<see cref="BuildReason.Manual"/>) goes before every other job and
/// does not wait for playback, not even when the worker is already waiting for another job. A failed build is logged
/// and the next job goes on.
/// </summary>
public sealed class RenderQueue : IHostedService
{
    private static readonly TimeSpan DefaultPlaybackPoll = TimeSpan.FromSeconds(30);

    private readonly Func<Guid, BuildReason, CancellationToken, Task<BuildResult>> _build;
    private readonly Func<bool> _someoneIsPlaying;
    private readonly Func<Guid, bool> _libraryExists;
    private readonly CoverStore _store;
    private readonly ILogger<RenderQueue> _log;
    private readonly TimeSpan _playbackPoll;

    private readonly Lock _lock = new();
    private readonly List<Job> _normal = new();
    private readonly Queue<Job> _manual = new();
    private readonly HashSet<Guid> _queued = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _manualWake = new(0);
    private CancellationTokenSource? _stopping;
    private Task _worker = Task.CompletedTask;
    private bool _stopped;

    /// <param name="build">Builds one library's cover.</param>
    /// <param name="someoneIsPlaying">Whether a session is playing something right now.</param>
    /// <param name="libraryExists">Whether Jellyfin still has the library.</param>
    /// <param name="store">Where the covers live.</param>
    /// <param name="log">The log.</param>
    /// <param name="playbackPoll">How often to look again while someone is playing; 30 seconds when null.</param>
    public RenderQueue(
        Func<Guid, BuildReason, CancellationToken, Task<BuildResult>> build,
        Func<bool> someoneIsPlaying,
        Func<Guid, bool> libraryExists,
        CoverStore store,
        ILogger<RenderQueue> log,
        TimeSpan? playbackPoll = null)
    {
        _build = build;
        _someoneIsPlaying = someoneIsPlaying;
        _libraryExists = libraryExists;
        _store = store;
        _log = log;
        _playbackPoll = playbackPoll ?? DefaultPlaybackPoll;
    }

    /// <summary>
    /// Raised on the worker after each job that ran or was dropped, with its result. A job replaced by a manual
    /// request for the same library raises nothing: the manual job's own event covers the library.
    /// </summary>
    public event Action<Guid, BuildReason, BuildResult>? BuildFinished;

    /// <summary>
    /// Asks for a library's cover to be built when its turn comes. A library that is already waiting is not added a
    /// second time, and it counts as waiting until its build actually starts, also while the worker holds it back for
    /// playback. A <see cref="BuildReason.Manual"/> request is the exception: it replaces the waiting job and goes to
    /// the front. A library being built right now is added, because the build may have started before the reason
    /// arose.
    /// </summary>
    public void Enqueue(Guid libraryId, BuildReason reason)
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            if (!_queued.Add(libraryId))
            {
                if (reason != BuildReason.Manual
                    || _manual.Any(waiting => waiting.LibraryId == libraryId && waiting.Done is null))
                {
                    return;
                }

                // Promoted: the waiting job is dropped. One the worker holds back for playback is dropped when it
                // sees this manual job (see YieldToManual).
                _normal.RemoveAll(waiting => waiting.LibraryId == libraryId && waiting.Done is null);
            }

            Add(new Job(libraryId, reason, null, CancellationToken.None));
        }
    }

    /// <summary>
    /// Builds a library's cover through the same worker and returns the result. A build that throws gives a result
    /// with the error, not the exception. Cancelling <paramref name="cancellationToken"/> cancels the returned task at
    /// once; a job that is still queued is skipped when its turn comes and a running one is stopped. After
    /// <see cref="StopAsync"/> the task is cancelled from the start.
    /// </summary>
    public Task<BuildResult> RunNowAsync(Guid libraryId, BuildReason reason, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job(libraryId, reason, done, cancellationToken);
        lock (_lock)
        {
            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                done.TrySetCanceled(cancellationToken);
                return done.Task;
            }

            // A job started here is not a duplicate of a waiting Enqueue: the caller wants this one's result.
            job.Registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
            Add(job);
        }

        return done.Task;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Best effort, and before the worker exists: nothing renders yet, so a cover file that no state refers to
        // was left by a build that never finished, and no render can be writing one that is about to be saved.
        CleanUp();

        lock (_lock)
        {
            _stopped = false;
        }

        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _worker = Task.Run(() => WorkAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _stopped = true;
        }

        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host stopped waiting; the worker was told to stop and ends on its own.
            }

            _stopping.Dispose();
            _stopping = null;
        }

        lock (_lock)
        {
            foreach (var job in _normal.Concat(_manual))
            {
                job.Registration.Dispose();
                job.Done?.TrySetCanceled();
            }

            _normal.Clear();
            _manual.Clear();
            _queued.Clear();
        }
    }

    // Caller holds the lock.
    private void Add(Job job)
    {
        if (job.Reason == BuildReason.Manual)
        {
            _manual.Enqueue(job);
            _manualWake.Release();
        }
        else
        {
            _normal.Add(job);
        }

        _wake.Release();
    }

    private Job? Next()
    {
        lock (_lock)
        {
            if (_manual.TryDequeue(out var manual))
            {
                return manual;
            }

            if (_normal.Count == 0)
            {
                return null;
            }

            var next = _normal[0];
            _normal.RemoveAt(0);
            return next;
        }
    }

    private async Task WorkAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(stopping).ConfigureAwait(false);
                if (Next() is { } job)
                {
                    await RunJobAsync(job, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken stopping)
    {
        if (job.Caller.IsCancellationRequested)
        {
            // Cancelled while it waited its turn: the caller already has its answer.
            Leave(job);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, job.Caller);
        var token = linked.Token;
        var requeued = false;
        try
        {
            // An admin who pressed the button wants the cover now; everything else gives way to playback, and to
            // such a request that arrives while it waits.
            while (job.Reason != BuildReason.Manual && _someoneIsPlaying())
            {
                if (YieldToManual(job))
                {
                    requeued = true;
                    return;
                }

                await _manualWake.WaitAsync(_playbackPoll, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            // Dropped before Forget, which does not wait for an update in flight: nothing runs for this library.
            if (!_libraryExists(job.LibraryId))
            {
                _log.LogInformation(
                    "Library Cover Maker: library {LibraryId} no longer exists, dropping its covers",
                    job.LibraryId);
                Unqueue(job);
                _store.Forget(job.LibraryId);
                Finish(job, new BuildResult(false, "The library no longer exists.", 0));
                return;
            }

            Unqueue(job);
            Finish(job, await _build(job.LibraryId, job.Reason, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Done?.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Library Cover Maker: building the cover of library {LibraryId} failed", job.LibraryId);
            Finish(job, new BuildResult(false, ex.Message, 0));
        }
        finally
        {
            if (!requeued)
            {
                Leave(job);
            }
        }
    }

    // A manual job that arrived while this one waited goes first: this one returns to the head of its queue, or is
    // dropped when the manual job is for the same library (that build covers it).
    private bool YieldToManual(Job job)
    {
        lock (_lock)
        {
            if (_manual.Count == 0)
            {
                return false;
            }

            // A manual request for the same library replaces this job, which is then dropped, not put back.
            var replaced = job.Done is null
                && _manual.Any(manual => manual.LibraryId == job.LibraryId && manual.Done is null);
            if (!replaced)
            {
                _normal.Insert(0, job);
                _wake.Release();
            }

            return true;
        }
    }

    private void Unqueue(Job job)
    {
        if (job.Done is null)
        {
            lock (_lock)
            {
                _queued.Remove(job.LibraryId);
            }
        }
    }

    private void Leave(Job job)
    {
        Unqueue(job);
        job.Registration.Dispose();
    }

    private void Finish(Job job, BuildResult result)
    {
        if (!result.Applied && result.Error is not null)
        {
            _log.LogWarning(
                "Library Cover Maker: no new cover for library {LibraryId}: {Error}",
                job.LibraryId,
                result.Error);
        }

        try
        {
            BuildFinished?.Invoke(job.LibraryId, job.Reason, result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Library Cover Maker: a listener of the build of {LibraryId} failed", job.LibraryId);
        }

        job.Done?.TrySetResult(result);
    }

    // Cleaning is best effort: whatever goes wrong here is logged and must never keep Jellyfin from starting.
    private void CleanUp()
    {
        try
        {
            if (Directory.Exists(_store.TempFolder))
            {
                Directory.Delete(_store.TempFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not clean {Folder}", _store.TempFolder);
        }

        try
        {
            foreach (var id in _store.KnownLibraries())
            {
                RemoveOrphanCovers(id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not list the libraries to clean");
        }
    }

    // Cover files that no state refers to any more: a render whose state was never saved, or a history entry dropped
    // by a crash. A library with no cover, original or history at all is left alone, since an unreadable state is
    // replaced by empty defaults and its files may still be the only copy.
    private void RemoveOrphanCovers(Guid id)
    {
        try
        {
            var state = _store.Load(id);
            if (state.CurrentCoverId is null && state.Original is null && state.History.Count == 0)
            {
                return;
            }

            var folder = Path.GetDirectoryName(_store.AnimatedPath(id, Guid.Empty))!;
            if (!Directory.Exists(folder))
            {
                return;
            }

            var used = state.History.Select(cover => cover.Id).ToHashSet();
            used.Add(state.CurrentCoverId ?? Guid.Empty);
            used.Add(state.Original?.Id ?? Guid.Empty);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                if ((extension is ".webp" or ".jpg")
                    && Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var cover)
                    && !used.Contains(cover))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not clean the covers of library {LibraryId}", id);
        }
    }

    private sealed class Job(
        Guid libraryId,
        BuildReason reason,
        TaskCompletionSource<BuildResult>? done,
        CancellationToken caller)
    {
        public Guid LibraryId { get; } = libraryId;

        public BuildReason Reason { get; } = reason;

        public TaskCompletionSource<BuildResult>? Done { get; } = done;

        public CancellationToken Caller { get; } = caller;

        public CancellationTokenRegistration Registration { get; set; }
    }
}
