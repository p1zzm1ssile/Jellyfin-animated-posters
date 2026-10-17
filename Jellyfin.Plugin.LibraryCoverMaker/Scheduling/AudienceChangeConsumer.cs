using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Scheduling;

/// <summary>
/// Checks covers again at once when a user is created or their library access or parental controls change
/// (decision 10, spec 7.6), instead of waiting for the next renewal.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin publishes <see cref="UserCreatedEventArgs"/> when a user is created and
/// <see cref="UserUpdatedEventArgs"/> when a user's policy (access, parental controls) or name is saved, and awaits
/// every consumer before it answers the admin, so the check is kept cheap: for each library the user can see now, the
/// titles of its current cover and of its locked slots are asked about one by one. A library whose cover the plugin
/// composed and that shows a title some viewer may not see is queued for a build
/// (<see cref="BuildReason.AudienceChanged"/>), pinned or not; locked titles that break the rule are unlocked. Both
/// leave a notice on the card naming the user. An uploaded cover and the original are left alone (decision 9).
/// </para>
/// <para>
/// Only the libraries the user sees are checked: one they lost access to has a smaller audience, which can only allow
/// more titles. A change that lands while the library renders is caught by the build's own check when it commits; the
/// library is queued again all the same. A library whose check fails is queued too: the build checks its cover and its
/// locks again itself.
/// </para>
/// <para>
/// A change saved through <c>IUserManager.UpdateUserAsync</c> publishes no event, as some third-party
/// authentication plugins do, so it is not seen here; nor is a title whose rating or tags a metadata refresh changed.
/// The audit of <see cref="RenewalScheduler"/> is the backstop for both: every 15 minutes it queues each library whose
/// cover breaks the rule, pinned and manual ones included, and the check every build makes when it commits never
/// applies a title some viewer may not see.
/// </para>
/// </remarks>
public sealed class AudienceChangeConsumer : IEventConsumer<UserUpdatedEventArgs>, IEventConsumer<UserCreatedEventArgs>
{
    private readonly IViewerAccess _access;
    private readonly CoverStore _store;
    private readonly Action<Guid, BuildReason> _enqueue;
    private readonly ILogger _log;
    private readonly Func<PluginConfiguration> _config;

    public AudienceChangeConsumer(
        LibraryAudience audience,
        ILibraryManager libraries,
        CoverStore store,
        RenderQueue queue,
        ILogger<AudienceChangeConsumer> log)
        : this(new JellyfinViewerAccess(audience, libraries), store, queue.Enqueue, log, CoverApplier.Settings)
    {
    }

    /// <summary>For the tests: Jellyfin's side, the queue and the configuration are replaced.</summary>
    internal AudienceChangeConsumer(
        IViewerAccess access,
        CoverStore store,
        Action<Guid, BuildReason> enqueue,
        ILogger log,
        Func<PluginConfiguration> config)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(enqueue);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(config);

        _access = access;
        _store = store;
        _enqueue = enqueue;
        _log = log;
        _config = config;
    }

    /// <inheritdoc />
    public Task OnEvent(UserUpdatedEventArgs eventArgs) => CheckAsync(eventArgs?.Argument, "changed");

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs) => CheckAsync(eventArgs?.Argument, "created");

    // Never throws: Jellyfin would log it as an error of the plugin, and the admin's change is saved already.
    private async Task CheckAsync(User? user, string what)
    {
        if (user is null)
        {
            return;
        }

        IReadOnlyList<Guid> libraries;
        try
        {
            libraries = _store.KnownLibraries();
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Library Cover Maker: could not list the libraries to check again after user {User} was {What}",
                user.Username,
                what);
            return;
        }

        // One library that fails must not keep the others from being checked.
        foreach (var libraryId in libraries)
        {
            try
            {
                await CheckLibraryAsync(libraryId, user).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // In doubt the library is built: the build checks its cover and its locks again and leaves a cover
                // that keeps the rule alone.
                _log.LogWarning(
                    ex,
                    "Library Cover Maker: could not check the cover of library {LibraryId} again after user {User} "
                    + "was {What}, so it is queued for a build that checks it",
                    libraryId,
                    user.Username,
                    what);
                Enqueue(libraryId);
            }
        }
    }

    // Guarded like the check: an exception from here would reach Jellyfin.
    private void Enqueue(Guid libraryId)
    {
        try
        {
            _enqueue(libraryId, BuildReason.AudienceChanged);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not queue library {LibraryId}", libraryId);
        }
    }

    private async Task CheckLibraryAsync(Guid libraryId, User user)
    {
        if (!_access.LibraryExists(libraryId))
        {
            return;
        }

        var viewers = _access.ViewersOf(libraryId);
        if (!viewers.Any(viewer => viewer.Id == user.Id))
        {
            return;
        }

        bool Allowed(Guid id) => _access.MaySee(id, viewers);

        // A cheap read without the library's gate, which an apply or a commit holds; the change below looks again.
        var config = _config();
        if (!CoverRules.BreaksRule(_store.Load(libraryId, config.DefaultMode), Allowed))
        {
            return;
        }

        var queue = false;
        await _store.UpdateAsync(
            libraryId,
            (state, token) =>
            {
                token.ThrowIfCancellationRequested();
                var current = CoverRules.Current(state);
                var shown = CoverRules.MustReplace(current, Allowed)
                    ? current!.TitleIds.Where(id => !Allowed(id)).Distinct().ToList()
                    : [];
                var unlocked = CoverRules.DisallowedLocks(state.Slots, Allowed);
                if (shown.Count == 0 && unlocked.Count == 0)
                {
                    return Task.CompletedTask;
                }

                CoverRules.Unlock(state, unlocked);
                var parts = new List<string>();
                if (shown.Count > 0)
                {
                    parts.Add(CoverRules.QueuedNotice(Reasons(shown, user)));
                }

                if (unlocked.Count > 0)
                {
                    var (blamed, others, gone) = Sort(unlocked, user);
                    parts.Add(CoverRules.UnlockNotice(user.Username, Quoted(blamed), Quoted(others), gone.Count));
                }

                state.Notice = string.Join(" ", parts);
                state.NoticeAt = DateTimeOffset.UtcNow;
                queue = true;
                return Task.CompletedTask;
            },
            config.DefaultMode,
            CancellationToken.None).ConfigureAwait(false);

        if (queue)
        {
            _log.LogInformation(
                "Library Cover Maker: the cover of library {LibraryId} breaks the parental rule since user {User} "
                + "changed, so it is queued for a new one",
                libraryId,
                user.Username);
            Enqueue(libraryId);
        }
    }

    // Why each of these titles breaks the rule, as "kid may not see "A"", "some viewers of this library may not see
    // "B"" and "a title is no longer in the library", joined with "and".
    private string Reasons(IReadOnlyList<Guid> titles, User user)
    {
        var (blamed, others, gone) = Sort(titles, user);
        var reasons = new List<string>();
        if (blamed.Count > 0)
        {
            reasons.Add($"{user.Username} may not see {Names(blamed)}");
        }

        if (others.Count > 0)
        {
            reasons.Add($"some viewers of this library may not see {Names(others)}");
        }

        if (gone.Count > 0)
        {
            reasons.Add(gone.Count == 1
                ? "a title is no longer in the library"
                : $"{gone.Count} titles are no longer in the library");
        }

        return string.Join(" and ", reasons);
    }

    // The titles the user may not see, those only other viewers may not see, and those no longer in Jellyfin.
    private (List<Guid> Blamed, List<Guid> Others, List<Guid> Gone) Sort(IReadOnlyList<Guid> titles, User user)
    {
        var gone = titles.Where(id => _access.TitleName(id) is null).ToList();
        var blamed = titles.Except(gone).Where(id => !_access.MaySee(id, [user])).ToList();
        var others = titles.Except(gone).Except(blamed).ToList();
        return (blamed, others, gone);
    }

    private string Names(IEnumerable<Guid> titles) => string.Join(", ", Quoted(titles));

    private List<string> Quoted(IEnumerable<Guid> titles) =>
        titles.Select(id => "\"" + _access.TitleName(id) + "\"").ToList();
}

/// <summary>What the check asks of Jellyfin about libraries, viewers and titles. The tests replace it.</summary>
internal interface IViewerAccess
{
    /// <summary>Whether Jellyfin still has the library.</summary>
    bool LibraryExists(Guid libraryId);

    /// <summary>The users who can see the library now.</summary>
    IReadOnlyList<User> ViewersOf(Guid libraryId);

    /// <summary>
    /// Whether every one of <paramref name="users"/> may see the item. False for an item that is gone.
    /// </summary>
    bool MaySee(Guid itemId, IReadOnlyList<User> users);

    /// <summary>The item's name, or null when Jellyfin no longer has it.</summary>
    string? TitleName(Guid itemId);
}

/// <summary>Libraries, viewers and titles through Jellyfin and <see cref="LibraryAudience"/>.</summary>
internal sealed class JellyfinViewerAccess(LibraryAudience audience, ILibraryManager libraries) : IViewerAccess
{
    public bool LibraryExists(Guid libraryId) => libraries.GetItemById(libraryId) is CollectionFolder;

    public IReadOnlyList<User> ViewersOf(Guid libraryId) => audience.UsersWhoSee(libraryId);

    public bool MaySee(Guid itemId, IReadOnlyList<User> users) =>
        libraries.GetItemById(itemId) is { } item && audience.AllowedForAll(item, users);

    public string? TitleName(Guid itemId) => libraries.GetItemById(itemId)?.Name;
}
