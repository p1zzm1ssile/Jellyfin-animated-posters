using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;

namespace Jellyfin.Plugin.LibraryCoverMaker.Covers;

/// <summary>
/// Whether a library's cover is built, and what the visibility rule asks of its current cover and of its locks.
/// </summary>
public static class CoverRules
{
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

    /// <summary>Whether the plugin renders covers for a library of this type. Null is a mixed library.</summary>
    public static bool IsVideoLibrary(string? collectionType) =>
        collectionType is null || BuiltTypes.Contains(collectionType, StringComparer.OrdinalIgnoreCase);
}
