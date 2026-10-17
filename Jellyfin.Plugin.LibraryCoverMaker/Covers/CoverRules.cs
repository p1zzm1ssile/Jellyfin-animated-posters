using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;

namespace Jellyfin.Plugin.LibraryCoverMaker.Covers;

/// <summary>
/// Whether a library's cover is built, what the visibility rule asks of its current cover and of its locks, and what
/// the library's card says when the rule changed something.
/// </summary>
public static class CoverRules
{
    // How a notice about a cover that breaks the rule starts, so that the commit can tell what it is answering.
    private const string QueuedStart = "Queued for a new cover because ";
    private const string NotReplacedStart =
        "Could not replace the cover yet, so it is tried again later. It was queued because ";
    private const string ReplacedStart = "Replaced the cover because ";
    private const string AnyViewer =
        "a title on it may not be seen by some viewers of this library, or is no longer in it.";
    private const string Settled =
        "No new cover was needed: the cover no longer shows a title some viewers of this library may not see.";

    // The video library types the plugin renders covers for (spec 7.1), as Jellyfin names them. A mixed library has
    // no type at all. Music, books, photos and the rest only get upload and pin.
    private static readonly string[] BuiltTypes = ["movies", "tvshows", "musicvideos", "homevideos"];

    /// <summary>
    /// The titles locked into <paramref name="slots"/> that <paramref name="allowed"/> refuses: those some viewer of
    /// the library may not see. In slot order, each title once.
    /// </summary>
    public static IReadOnlyList<Guid> DisallowedLocks(CoverSlot[] slots, Func<Guid, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(allowed);

        return slots
            .Where(slot => slot?.ItemId is not null)
            .Select(slot => slot.ItemId!.Value)
            .Where(id => !allowed(id))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="current"/> must be replaced whatever the library's pin or mode: a cover the plugin
    /// composed that shows a title some viewer may not see (decision 9). An uploaded cover and the library's original
    /// image are never replaced, since the plugin cannot tell what they show.
    /// </summary>
    public static bool MustReplace(CoverRecord? current, Func<Guid, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        return current is { Origin: CoverOrigin.Generated } && current.TitleIds.Exists(id => !allowed(id));
    }

    /// <summary>
    /// Whether a build for <paramref name="reason"/> may replace the library's cover. Only video libraries are built
    /// (<paramref name="collectionType"/> movies, tvshows, musicvideos, homevideos, or null for a mixed library). A
    /// pinned library is built only when its cover must be replaced after an audience change; a library in manual
    /// mode only when an admin asks (<see cref="BuildReason.Manual"/>, <see cref="BuildReason.Slots"/>) or, like a
    /// pinned one, when its cover must be replaced. Every other library is built for any reason.
    /// </summary>
    public static bool ShouldBuild(
        LibraryCoverState state,
        string? collectionType,
        BuildReason reason,
        bool mustReplace)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!IsVideoLibrary(collectionType))
        {
            return false;
        }

        var replacing = reason == BuildReason.AudienceChanged && mustReplace;
        if (state.Pinned)
        {
            return replacing;
        }

        if (state.Mode == CoverMode.Manual)
        {
            return reason is BuildReason.Manual or BuildReason.Slots || replacing;
        }

        return true;
    }

    /// <summary>
    /// Whether the library's current cover must be replaced or one of its locked titles unlocked: what a user change
    /// and the renewal audit look for.
    /// </summary>
    public static bool BreaksRule(LibraryCoverState state, Func<Guid, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(allowed);

        return MustReplace(Current(state), allowed) || DisallowedLocks(state.Slots, allowed).Count > 0;
    }

    /// <summary>
    /// The cover the library shows now: an entry of its history, or its original. Null when it has none.
    /// </summary>
    public static CoverRecord? Current(LibraryCoverState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.History.Find(cover => cover.Id == state.CurrentCoverId)
            ?? (state.Original is { } original && original.Id == state.CurrentCoverId ? original : null);
    }

    /// <summary>Frees the slots locked to <paramref name="titles"/>: they get new picks on the next render.</summary>
    public static void Unlock(LibraryCoverState state, IReadOnlyCollection<Guid> titles)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(titles);

        foreach (var slot in state.Slots.Where(slot => slot.ItemId is { } id && titles.Contains(id)))
        {
            slot.ItemId = null;
            slot.MomentSeconds = null;
            slot.BackdropIndex = null;
        }
    }

    /// <summary>
    /// What the card says about unlocked titles: those <paramref name="user"/> may not see (by name), those only other
    /// viewers may not see (by name), and how many left the library. Empty when nothing was unlocked.
    /// </summary>
    /// <param name="user">The user whose change unlocked them; null when no user is known.</param>
    /// <param name="blamed">The quoted names of the titles <paramref name="user"/> may not see.</param>
    /// <param name="others">The quoted names of the titles some other viewer may not see.</param>
    /// <param name="gone">How many titles are no longer in the library.</param>
    public static string UnlockNotice(
        string? user,
        IReadOnlyList<string> blamed,
        IReadOnlyList<string> others,
        int gone)
    {
        ArgumentNullException.ThrowIfNull(blamed);
        ArgumentNullException.ThrowIfNull(others);

        var notices = new List<string>();
        if (blamed.Count > 0)
        {
            notices.Add($"Unlocked {string.Join(", ", blamed)} because {user} may not see {Them(blamed)}.");
        }

        if (others.Count > 0)
        {
            var names = string.Join(", ", others);
            notices.Add($"Unlocked {names} because some viewers of this library may not see {Them(others)}.");
        }

        if (gone > 0)
        {
            notices.Add(gone == 1
                ? "Unlocked a title that is no longer in this library."
                : $"Unlocked {gone} titles that are no longer in this library.");
        }

        return string.Join(" ", notices);
    }

    /// <summary>What the card says when a user change queued the library for a new cover.</summary>
    /// <param name="reasons">Why, as in <c>kid may not see "A"</c>, without the final period.</param>
    public static string QueuedNotice(string reasons) => QueuedStart + reasons + ".";

    /// <summary>
    /// What the card says once a cover that broke the rule was replaced. The reason the library was queued for, with
    /// the user it names, is kept; without one, the card says what the rule found. <paramref name="unlocked"/>, what
    /// the build said about the locks it freed, is kept too.
    /// </summary>
    public static string ReplacedNotice(string? previous, string? unlocked = null) =>
        WithUnlocked(ReplacedStart + (QueuedReason(previous) ?? AnyViewer), unlocked);

    /// <summary>
    /// What the card says when a cover that breaks the rule could not be replaced: the failure itself is the card's
    /// last error, and the reason the library was queued for and <paramref name="unlocked"/> are kept.
    /// </summary>
    public static string NotReplacedNotice(string? previous, string? unlocked = null) =>
        WithUnlocked(NotReplacedStart + (QueuedReason(previous) ?? AnyViewer), unlocked);

    /// <summary>
    /// What the card says when a build that was queued to replace a cover found that it no longer had to, with
    /// <paramref name="unlocked"/> kept.
    /// </summary>
    public static string SettledNotice(string? unlocked = null) => WithUnlocked(Settled, unlocked);

    /// <summary>
    /// Whether <paramref name="notice"/> says that the cover is waiting to be replaced (queued, or not replaced yet),
    /// so that the build that ends the wait has to say how it ended.
    /// </summary>
    public static bool IsPending(string? notice) => QueuedReason(notice) is not null;

    /// <summary>
    /// The notice of a pending replacement with <paramref name="unlocked"/> added, or <paramref name="unlocked"/>
    /// alone when nothing is pending.
    /// </summary>
    public static string AddUnlocked(string? notice, string unlocked) =>
        IsPending(notice) ? WithUnlocked(notice!, unlocked) : unlocked;

    /// <summary>Whether the plugin renders covers for a library of this type. Null is a mixed library.</summary>
    public static bool IsVideoLibrary(string? collectionType) =>
        collectionType is null || BuiltTypes.Contains(collectionType, StringComparer.OrdinalIgnoreCase);

    // The reason of a notice this class wrote for a cover that breaks the rule and is still waiting for its
    // replacement (queued, or not replaced yet), or null. A notice of a replacement that is done says nothing about
    // the next one.
    private static string? QueuedReason(string? notice)
    {
        foreach (var start in new[] { QueuedStart, NotReplacedStart })
        {
            if (notice is not null && notice.StartsWith(start, StringComparison.Ordinal))
            {
                return notice[start.Length..];
            }
        }

        return null;
    }

    private static string Them(IReadOnlyCollection<string> titles) => titles.Count == 1 ? "it" : "them";

    // The text once: a pending notice the build already added it to carries it already.
    private static string WithUnlocked(string text, string? unlocked) =>
        string.IsNullOrEmpty(unlocked) || text.Contains(unlocked, StringComparison.Ordinal)
            ? text
            : text + " " + unlocked;
}
