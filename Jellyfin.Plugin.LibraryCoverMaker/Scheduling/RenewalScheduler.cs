using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// Looks every 15 minutes for libraries with their own renewal timetable (<c>RenewEveryDays</c>) whose time has
/// come and puts them in the render queue. The queue never holds a library twice, so a long build does not pile up
/// requests.
/// </summary>
public sealed class RenewalScheduler : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // How long a library whose renewal failed is left alone.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    private readonly Func<IEnumerable<Guid>> _libraries;
    private readonly Func<CoverMode> _defaultMode;
    private readonly CoverStore _store;
    private readonly RenderQueue _queue;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<RenewalScheduler> _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _failedAt = new();
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    public RenewalScheduler(
        ILibraryManager libraryManager,
        CoverStore store,
        RenderQueue queue,
        ILogger<RenewalScheduler> log)
        : this(
            () => RefreshCoversTask.EnabledLibraries(libraryManager),
            RefreshCoversTask.DefaultModeFromPlugin,
            store,
            queue,
            () => DateTimeOffset.UtcNow,
            log)
    {
    }

    /// <summary>For the tests: the libraries, the default mode and the clock come from the caller.</summary>
    internal RenewalScheduler(
        Func<IEnumerable<Guid>> libraries,
        Func<CoverMode> defaultMode,
        CoverStore store,
        RenderQueue queue,
        Func<DateTimeOffset> now,
        ILogger<RenewalScheduler> log)
    {
        _libraries = libraries;
        _defaultMode = defaultMode;
        _store = store;
        _queue = queue;
        _now = now;
        _log = log;
        _queue.BuildFinished += OnBuildFinished;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host stopped waiting; the loop was told to stop and ends on its own.
        }
    }

    public void Dispose()
    {
        _queue.BuildFinished -= OnBuildFinished;
        _stopping?.Dispose();
    }

    /// <summary>
    /// Queues every library whose renewal time has passed. A library whose last renewal failed is left alone for
    /// <see cref="RetryAfterFailure"/>, so that a build that keeps failing is not tried again on every check. One
    /// library that cannot be read is logged and does not stop the others. Never throws.
    /// </summary>
    internal void CheckNow()
    {
        IReadOnlyList<Guid> libraries;
        CoverMode mode;
        DateTimeOffset now;
        try
        {
            libraries = _libraries().ToList();
            mode = _defaultMode();
            now = _now();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Library Cover Maker: listing the libraries due for renewal failed");
            return;
        }

        // A library that no longer exists is forgotten, failure included.
        lock (_lock)
        {
            var existing = libraries.ToHashSet();
            foreach (var gone in _failedAt.Keys.Where(id => !existing.Contains(id)).ToList())
            {
                _failedAt.Remove(gone);
            }
        }

        foreach (var id in libraries)
        {
            try
            {
                var state = _store.Load(id, mode);
                if (RefreshCoversTask.InRotation(state)
                    && RefreshCoversTask.HasOwnTimetable(state)
                    && IsDue(state, now)
                    && !RecentlyFailed(id, now))
                {
                    _queue.Enqueue(id, BuildReason.Scheduled);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Library Cover Maker: checking the renewal of library {LibraryId} failed", id);
            }
        }
    }

    // Compares the elapsed time, not a date plus days, which can overflow for a large setting.
    private static bool IsDue(LibraryCoverState state, DateTimeOffset now) =>
        state.LastRenderedAt is not { } last || (now - last).TotalDays >= state.RenewEveryDays;

    private bool RecentlyFailed(Guid id, DateTimeOffset now)
    {
        lock (_lock)
        {
            return _failedAt.TryGetValue(id, out var failed) && now - failed < RetryAfterFailure;
        }
    }

    // A failed renewal starts the pause; a build that went through (or had nothing to do) ends it. An admin's own
    // request neither starts nor ends it.
    private void OnBuildFinished(Guid id, BuildReason reason, BuildResult result)
    {
        if (reason == BuildReason.Manual)
        {
            return;
        }

        lock (_lock)
        {
            if (result is { Applied: false, Error: not null })
            {
                _failedAt[id] = _now();
            }
            else
            {
                _failedAt.Remove(id);
            }
        }
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            CheckNow();
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                CheckNow();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
