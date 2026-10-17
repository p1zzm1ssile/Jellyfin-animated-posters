using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// Looks every 15 minutes, starting a little after Jellyfin starts, for libraries with their own renewal timetable
/// (<c>RenewEveryDays</c>) whose time has come and puts them in the render queue. The queue never holds a library
/// twice, so a long build does not pile up requests.
/// </summary>
/// <remarks>
/// Each check is also an audit of the visibility rule (decision 9): a library whose current cover the plugin composed
/// shows a title some viewer may not see, or that has such a title locked, is queued for an
/// <see cref="BuildReason.AudienceChanged"/> build, pinned and manual ones included. It catches what no user event
/// announces (a policy saved without an event, a rating changed by a metadata refresh) and a replacement that did not
/// go through. A library the audit queued is queued again by it at most once an hour while its cover still breaks the
/// rule, whatever the renewals' pause after a failure says.
/// </remarks>
public sealed class RenewalScheduler : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // Jellyfin is still starting right after the plugin's services start: the first check waits a little.
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(90);

    // How long a library whose renewal failed is left alone.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    // How long the audit waits before it queues a library again whose cover still breaks the rule.
    private static readonly TimeSpan AuditRetry = TimeSpan.FromHours(1);

    private readonly Func<IEnumerable<Guid>> _libraries;
    private readonly Func<CoverMode> _defaultMode;
    private readonly CoverStore _store;
    private readonly RenderQueue _queue;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Guid, LibraryCoverState, bool> _breaksRule;
    private readonly ILogger<RenewalScheduler> _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _failedAt = new();
    private readonly Dictionary<Guid, DateTimeOffset> _auditedAt = new();
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    public RenewalScheduler(
        ILibraryManager libraryManager,
        LibraryAudience audience,
        CoverStore store,
        RenderQueue queue,
        ILogger<RenewalScheduler> log)
        : this(
            () => RefreshCoversTask.EnabledLibraries(libraryManager),
            RefreshCoversTask.DefaultModeFromPlugin,
            store,
            queue,
            () => DateTimeOffset.UtcNow,
            log,
            (id, state) => BreaksRuleNow(libraryManager, audience, id, state))
    {
    }

    /// <summary>
    /// For the tests: the libraries, the default mode, the clock and the audit's check come from the caller. Without
    /// a check, no cover breaks the rule.
    /// </summary>
    internal RenewalScheduler(
        Func<IEnumerable<Guid>> libraries,
        Func<CoverMode> defaultMode,
        CoverStore store,
        RenderQueue queue,
        Func<DateTimeOffset> now,
        ILogger<RenewalScheduler> log,
        Func<Guid, LibraryCoverState, bool>? breaksRule = null)
    {
        _libraries = libraries;
        _defaultMode = defaultMode;
        _store = store;
        _queue = queue;
        _now = now;
        _log = log;
        _breaksRule = breaksRule ?? ((_, _) => false);
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
    /// Queues every library whose renewal time has passed, and audits every library against the visibility rule. A
    /// library whose last renewal failed is left alone for <see cref="RetryAfterFailure"/>, so that a build that keeps
    /// failing is not tried again on every check; the audit has its own <see cref="AuditRetry"/>. One library that
    /// cannot be read is logged and does not stop the others. Never throws.
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
            _log.LogError(ex, "Jellyfin Animated Posters: listing the libraries due for renewal failed");
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

            foreach (var gone in _auditedAt.Keys.Where(id => !existing.Contains(id)).ToList())
            {
                _auditedAt.Remove(gone);
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

                // After the renewal: a Scheduled job queued just now becomes an audience change (RenderQueue.Enqueue).
                Audit(id, state, now);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Jellyfin Animated Posters: checking the renewal of library {LibraryId} failed", id);
            }
        }
    }

    // Jellyfin's answer for the audit: the library's viewers now, and each title of its current cover and its locks
    // asked about the way a build asks (in the library, and every viewer may see it). A library with nothing to check
    // asks Jellyfin nothing, and one the plugin does not build is never queued: a library of another type, or one
    // switched off in its options, which the build leaves alone (it would be queued again every hour).
    internal static bool BreaksRuleNow(
        ILibraryManager libraries,
        LibraryAudience audience,
        Guid libraryId,
        LibraryCoverState state)
    {
        if (CoverRules.Current(state) is not { Origin: CoverOrigin.Generated } && !state.Slots.Any(slot => slot.Locked))
        {
            return false;
        }

        if (libraries.GetItemById(libraryId) is not CollectionFolder folder
            || !CoverRules.IsVideoLibrary(folder.CollectionType?.ToString())
            || !libraries.GetLibraryOptions(folder).Enabled)
        {
            return false;
        }

        var viewers = audience.UsersWhoSee(libraryId);
        return CoverRules.BreaksRule(
            state,
            id => JellyfinCoverSource.AccessOf(libraries, audience, libraryId, id, viewers) == TitleAccess.Allowed);
    }

    private void Audit(Guid id, LibraryCoverState state, DateTimeOffset now)
    {
        if (!_breaksRule(id, state))
        {
            lock (_lock)
            {
                _auditedAt.Remove(id);
            }

            return;
        }

        lock (_lock)
        {
            if (_auditedAt.TryGetValue(id, out var audited) && now - audited < AuditRetry)
            {
                return;
            }

            _auditedAt[id] = now;
        }

        _log.LogInformation(
            "Jellyfin Animated Posters: the cover of library {LibraryId} breaks the parental rule, so it is queued for "
            + "a new one",
            id);
        _queue.Enqueue(id, BuildReason.AudienceChanged);
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
            await Task.Delay(FirstCheck, stopping).ConfigureAwait(false);
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
