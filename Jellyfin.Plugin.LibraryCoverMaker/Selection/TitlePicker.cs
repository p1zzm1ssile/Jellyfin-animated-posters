using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;

namespace Jellyfin.Plugin.LibraryCoverMaker.Selection;

/// <summary>A title that every viewer of a library may see, with what the picker needs to rank and filter it.</summary>
/// <param name="Id">The title's item id. For a series, the series.</param>
/// <param name="PlayCount">Plays summed over every user who can see the library.</param>
/// <param name="CommunityRating">The community rating, or null when the title has none.</param>
/// <param name="DateCreated">When the title was added to the library.</param>
/// <param name="HasBackdrop">Whether the title has a backdrop image, which art mode needs.</param>
/// <param name="HasVideo">Whether the title has a video to take clips from, which scenes mode needs.</param>
public sealed record TitleCandidate(
    Guid Id,
    int PlayCount,
    float? CommunityRating,
    DateTime DateCreated,
    bool HasBackdrop,
    bool HasVideo);

/// <summary>Chooses the titles a cover shows.</summary>
public static class TitlePicker
{
    // A pool is never cut below this many titles, so that a small library still has something to draw from.
    private const int MinimumPoolSize = 5;

    /// <summary>
    /// Picks the titles of one cover: the locked titles first, then one draw for each free slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The locked titles come back first, in slot order, even ones the library no longer offers: whether a lock is
    /// still allowed is for the caller to decide. Pass only the locks of the slots the cover uses; a cover has no more
    /// titles than <see cref="CoverStyle.SlotCount"/>, so locks beyond that are left out. A title is never in a cover
    /// twice: when the same title is locked in several slots it keeps the first of them, and each later one counts
    /// as a free slot. So the locked part of the result is the distinct ids among the first SlotCount entries of
    /// <paramref name="locked"/>, in the order of their first appearance, and it is followed by one draw for every
    /// other slot.
    /// </para>
    /// <para>
    /// The eligible titles are the allowed ones that have what the mode needs: a backdrop in art mode, a video in
    /// scenes mode, either in manual mode. Free slot i (counted among the free slots only) draws, uniformly, from
    /// pool i mod the number of pools, and never takes a title that is in the cover already. The pools are the ones
    /// the style turns on (all three when it turns on none): the most watched titles (plays, then the newest), the
    /// top rated (rating with unrated titles last, then the newest) and the recently added. A pool holds the top 30%
    /// of the eligible titles, rounded up, but at least five; locked titles are among the eligible ones when it comes
    /// to ranking and counting, though they are never drawn again. Ties go to the newer title, then to the lower id,
    /// so the pools do not depend on the order of the input.
    /// </para>
    /// <para>
    /// Titles of the previous cover are used only when nothing else is left: a free slot takes a title of its pool
    /// that the previous cover did not show, else any other eligible title that it did not show, and only then,
    /// in a library too small to vary, a title it did show. A title is never picked twice, so the result is
    /// shorter than the slot count when the eligible titles run out.
    /// </para>
    /// <para>
    /// The result depends only on the arguments and on the sequence of numbers the <paramref name="random"/>
    /// gives; nothing that is passed in is changed.
    /// </para>
    /// </remarks>
    /// <param name="allowed">The library's titles that every viewer may see.</param>
    /// <param name="mode">What the cover is made of, which decides which titles can be used.</param>
    /// <param name="style">The cover's number of slots and which pools to draw from.</param>
    /// <param name="locked">The titles an admin locked, in slot order.</param>
    /// <param name="previous">The titles of the cover this one replaces.</param>
    /// <param name="random">The source of the draws; a seeded one gives the same titles every time.</param>
    /// <returns>
    /// The title for each slot, locked ones first: at most <see cref="CoverStyle.SlotCount"/> of them.
    /// </returns>
    public static IReadOnlyList<Guid> Pick(
        IReadOnlyList<TitleCandidate> allowed,
        CoverMode mode,
        CoverStyle style,
        IReadOnlyList<Guid> locked,
        IReadOnlyCollection<Guid> previous,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(random);

        var suitsMode = Suits(mode);
        var slots = Math.Clamp(style.SlotCount, 0, LibraryCoverState.MaxSlots);

        // The locks of the slots in use, each title once. A title locked in two slots keeps the first, and the later
        // slot is free again, so that no title is in the cover twice. A title in the cover already, locked or drawn,
        // is never drawn again.
        var used = new HashSet<Guid>();
        var picked = new List<Guid>();
        foreach (var id in locked.Take(slots))
        {
            if (used.Add(id))
            {
                picked.Add(id);
            }
        }

        var free = slots - picked.Count;
        if (free == 0)
        {
            return picked;
        }

        // Newest first, then by id, always: with that order fixed, neither the order of the input nor a tie can change
        // which titles are in a pool or what a draw picks.
        var eligible = allowed
            .Where(suitsMode)
            .OrderByDescending(title => title.DateCreated)
            .ThenBy(title => title.Id)
            .DistinctBy(title => title.Id)
            .ToList();
        var everyone = eligible.Select(title => title.Id).ToList();

        // 30% of the titles, rounded up. Whole numbers, so that no floating point rounding can move the cut.
        var poolSize = Math.Max(MinimumPoolSize, (int)((3L * eligible.Count + 9) / 10));
        var pools = PoolsOf(eligible, style, poolSize);

        var previousCover = new HashSet<Guid>(previous);
        var noTitles = new HashSet<Guid>();

        for (var slot = 0; slot < free; slot++)
        {
            var pool = pools[slot % pools.Count];
            var title = Draw(pool, previousCover, used, random)
                ?? Draw(everyone, previousCover, used, random)
                ?? Draw(pool, noTitles, used, random)
                ?? Draw(everyone, noTitles, used, random);
            if (title is null)
            {
                break; // every eligible title is in the cover already
            }

            used.Add(title.Value);
            picked.Add(title.Value);
        }

        return picked;
    }

    private static Func<TitleCandidate, bool> Suits(CoverMode mode) => mode switch
    {
        CoverMode.Art => title => title.HasBackdrop,
        CoverMode.Scenes => title => title.HasVideo,
        CoverMode.Manual => title => title.HasBackdrop || title.HasVideo,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cover mode."),
    };

    // The pools the style turns on, in the fixed order of most watched, top rated, recent; the top titles of each.
    private static List<List<Guid>> PoolsOf(List<TitleCandidate> newestFirst, CoverStyle style, int size)
    {
        var all = !style.UseMostWatched && !style.UseTopRated && !style.UseRecent;
        var pools = new List<List<Guid>>();

        if (style.UseMostWatched || all)
        {
            pools.Add(TopOf(
                newestFirst
                    .OrderByDescending(title => title.PlayCount)
                    .ThenByDescending(title => title.DateCreated)
                    .ThenBy(title => title.Id),
                size));
        }

        if (style.UseTopRated || all)
        {
            // A missing rating is lower than any rating, so the unrated titles come last.
            pools.Add(TopOf(
                newestFirst
                    .OrderByDescending(title => title.CommunityRating)
                    .ThenByDescending(title => title.DateCreated)
                    .ThenBy(title => title.Id),
                size));
        }

        if (style.UseRecent || all)
        {
            pools.Add(TopOf(newestFirst, size));
        }

        return pools;
    }

    private static List<Guid> TopOf(IEnumerable<TitleCandidate> ranked, int size) =>
        ranked.Take(size).Select(title => title.Id).ToList();

    // One title chosen uniformly from those of the source that are neither skipped nor used; null when there are none.
    private static Guid? Draw(List<Guid> source, HashSet<Guid> skipped, HashSet<Guid> used, Random random)
    {
        var open = source.Where(id => !used.Contains(id) && !skipped.Contains(id)).ToList();
        return open.Count == 0 ? null : open[random.Next(open.Count)];
    }
}
