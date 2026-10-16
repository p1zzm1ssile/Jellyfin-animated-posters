using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// The weekly "Refresh library covers" task: builds a new cover for every library that follows the schedule. A
/// library is left out when it is pinned, manual, renews on its own timetable (<see cref="RenewalScheduler"/>), or
/// is visible to nobody.
/// </summary>
public sealed class RefreshCoversTask : IScheduledTask
{
    private readonly Func<IEnumerable<Guid>> _libraries;
    private readonly Func<CoverMode> _defaultMode;
    private readonly CoverStore _store;
    private readonly RenderQueue _queue;
    private readonly ILogger<RefreshCoversTask> _log;

    public RefreshCoversTask(
        ILibraryManager libraryManager,
        CoverStore store,
        RenderQueue queue,
        ILogger<RefreshCoversTask> log)
        : this(() => EnabledLibraries(libraryManager), DefaultModeFromPlugin, store, queue, log)
    {
    }

    /// <summary>For the tests: the libraries and the default mode come from the caller.</summary>
    internal RefreshCoversTask(
        Func<IEnumerable<Guid>> libraries,
        Func<CoverMode> defaultMode,
        CoverStore store,
        RenderQueue queue,
        ILogger<RefreshCoversTask> log)
    {
        _libraries = libraries;
        _defaultMode = defaultMode;
        _store = store;
        _queue = queue;
        _log = log;
    }

    public string Name => "Refresh library covers";

    public string Key => "LibraryCoverMakerRefresh";

    public string Description => "Builds a new cover for every library that follows the schedule.";

    public string Category => "Library Cover Maker";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var mode = _defaultMode();
        var libraries = _libraries().ToList();

        for (var i = 0; i < libraries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / libraries.Count);
            try
            {
                if (!FollowsSchedule(_store.Load(libraries[i], mode)))
                {
                    continue;
                }

                var result = await _queue.RunNowAsync(libraries[i], BuildReason.Scheduled, cancellationToken)
                    .ConfigureAwait(false);
                _log.LogInformation(
                    "Library Cover Maker: refreshed library {LibraryId}, applied {Applied}",
                    libraries[i],
                    result.Applied);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One library that cannot be read does not stop the others.
                _log.LogError(ex, "Library Cover Maker: refreshing library {LibraryId} failed", libraries[i]);
            }
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        },
    ];

    /// <summary>Whether the schedule may replace a library's cover: not pinned and not manual.</summary>
    internal static bool InRotation(LibraryCoverState state) =>
        !state.Pinned && state.Mode != CoverMode.Manual;

    /// <summary>
    /// Whether a library renews on a timetable of its own. A value of zero or less counts as no timetable, so that
    /// a mistaken setting does not make the library due on every check.
    /// </summary>
    internal static bool HasOwnTimetable(LibraryCoverState state) => state.RenewEveryDays is > 0;

    /// <summary>Whether the weekly task rebuilds a library: in rotation and no timetable of its own.</summary>
    internal static bool FollowsSchedule(LibraryCoverState state) => InRotation(state) && !HasOwnTimetable(state);

    /// <summary>The libraries visible to somebody; one switched off in its options is visible to nobody.</summary>
    internal static IEnumerable<Guid> EnabledLibraries(ILibraryManager libraryManager) =>
        libraryManager.GetVirtualFolders()
            .Where(folder => folder.LibraryOptions?.Enabled != false)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty);

    internal static CoverMode DefaultModeFromPlugin() =>
        Plugin.Instance?.Configuration.DefaultMode ?? CoverMode.Art;
}
