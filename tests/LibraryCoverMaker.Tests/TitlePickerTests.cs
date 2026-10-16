using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Xunit;

namespace LibraryCoverMaker.Tests;

public sealed class TitlePickerTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Id(int n) => new($"{n:D8}-0000-0000-0000-000000000000");

    // A title that suits every mode unless the arguments say otherwise. The more days old, the earlier it was added.
    private static TitleCandidate Title(
        int n,
        int plays = 0,
        float? rating = null,
        int daysOld = 0,
        bool backdrop = true,
        bool video = true) =>
        new(Id(n), plays, rating, Now.AddDays(-daysOld), backdrop, video);

    // Titles 1 to count that differ in every way the picker ranks them: plays, rating (every fifth has none) and age.
    private static List<TitleCandidate> Library(int count) =>
        Enumerable.Range(1, count)
            .Select(n => Title(n, plays: n * 7 % 11, rating: n % 5 == 0 ? null : n * 3 % 10 / 2f, daysOld: n * 5 % 13))
            .ToList();

    // Titles 1 to 5 are the most watched, best rated and newest all at once, so they head every pool; the others trail.
    private static List<TitleCandidate> FiveThatHeadEveryPool(int others) =>
        Enumerable.Range(1, 5 + others)
            .Select(n => n <= 5
                ? Title(n, plays: 100 - n, rating: 10f - n / 10f, daysOld: n)
                : Title(n, daysOld: 30 + n))
            .ToList();

    // Titles 1 to 5 are the most watched, 6 to 10 the best rated and 11 to 15 the newest: in each ranking one group
    // heads the list, so the three top lists of five have nothing in common.
    private static List<TitleCandidate> ThreeDisjointTopLists() =>
        Enumerable.Range(1, 15)
            .Select(n => n switch
            {
                <= 5 => Title(n, plays: 100 - n, daysOld: 100),
                <= 10 => Title(n, rating: 9f - (n - 6) / 10f, daysOld: 100),
                _ => Title(n, daysOld: n - 11),
            })
            .ToList();

    // Which of the three groups of ThreeDisjointTopLists a title is in; -1 for a title outside the library.
    private static int GroupOf(Guid id)
    {
        for (var n = 1; n <= 15; n++)
        {
            if (Id(n) == id)
            {
                return (n - 1) / 5;
            }
        }

        return -1;
    }

    private static IReadOnlyList<Guid> Pick(
        IReadOnlyList<TitleCandidate> allowed,
        CoverMode mode = CoverMode.Art,
        CoverStyle? style = null,
        IReadOnlyList<Guid>? locked = null,
        IReadOnlyCollection<Guid>? previous = null,
        Random? random = null) =>
        TitlePicker.Pick(
            allowed,
            mode,
            style ?? new CoverStyle(),
            locked ?? Array.Empty<Guid>(),
            previous ?? Array.Empty<Guid>(),
            random ?? new Random(42));

    private static CoverStyle OnlyPool(bool mostWatched = false, bool topRated = false, bool recent = false) =>
        new() { UseMostWatched = mostWatched, UseTopRated = topRated, UseRecent = recent };

    private static IEnumerable<Guid> Ids(params int[] numbers) => numbers.Select(Id);

    [Fact]
    public void Returns_at_most_slot_count_distinct_titles()
    {
        var library = Library(20);

        var picked = Pick(library);

        Assert.Equal(5, picked.Count);
        Assert.Equal(5, picked.Distinct().Count());
        Assert.All(picked, id => Assert.Contains(library, title => title.Id == id));

        var three = Pick(library, style: new CoverStyle { SlotCount = 3 });
        Assert.Equal(3, three.Count);
        Assert.Equal(3, three.Distinct().Count());
    }

    [Fact]
    public void Never_returns_a_title_twice_whatever_the_draw()
    {
        var library = Library(12);
        var locked = new[] { Id(4) };
        var previous = Ids(1, 2, 3, 4, 5, 6).ToArray();
        var random = new Random(42);

        for (var round = 0; round < 300; round++)
        {
            var picked = Pick(library, locked: locked, previous: previous, random: random);

            Assert.Equal(picked.Count, picked.Distinct().Count());
            Assert.InRange(picked.Count, 1, 5);
            Assert.Equal(Id(4), picked[0]);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(99, 5)] // a cover has 5 slots, whatever a hand-edited file says
    [InlineData(0, 0)]
    [InlineData(-2, 0)]
    public void Slot_count_sets_how_many_titles_are_picked(int slotCount, int expected)
    {
        var picked = Pick(Library(20), style: new CoverStyle { SlotCount = slotCount });

        Assert.Equal(expected, picked.Count);
        Assert.Equal(expected, picked.Distinct().Count());
    }

    [Fact]
    public void Locked_titles_come_first()
    {
        // Title 1 heads every pool, so it would be drawn for a free slot too if the lock did not keep it out.
        var library = FiveThatHeadEveryPool(others: 15);
        var locked = new[] { Id(7), Id(1) }; // in slot order, which is not id order
        var random = new Random(42);

        for (var round = 0; round < 200; round++)
        {
            var picked = Pick(library, locked: locked, random: random);

            Assert.Equal(5, picked.Count);
            Assert.Equal(locked, picked.Take(2));
            Assert.Equal(5, picked.Distinct().Count());
        }
    }

    [Fact]
    public void Locked_titles_the_library_no_longer_offers_are_still_returned_first()
    {
        // Whether a lock is still allowed is for the caller to decide; the picker keeps the admin's choice in its slot.
        var gone = Id(900);

        var picked = Pick(Library(20), locked: new[] { gone });

        Assert.Equal(5, picked.Count);
        Assert.Equal(gone, picked[0]);
        Assert.Equal(5, picked.Distinct().Count());
    }

    [Fact]
    public void Locked_titles_are_kept_even_when_the_library_has_nothing_to_offer()
    {
        var locked = new[] { Id(900), Id(901) };

        Assert.Equal(locked, Pick(new List<TitleCandidate>(), locked: locked));
    }

    [Fact]
    public void Slots_that_are_all_locked_need_no_other_title()
    {
        var locked = Ids(9, 8, 7, 6, 5).ToArray();

        Assert.Equal(locked, Pick(Library(20), locked: locked));
    }

    [Fact]
    public void Locked_titles_beyond_the_slot_count_are_cut()
    {
        var locked = Ids(9, 8, 7, 6).ToArray();

        var picked = Pick(Library(20), style: new CoverStyle { SlotCount = 3 }, locked: locked);

        Assert.Equal(locked.Take(3), picked);
    }

    [Fact]
    public void A_title_locked_in_two_slots_is_picked_once()
    {
        // Title 7 is locked in the first and in the third slot. It keeps the first; the third slot is free again, so
        // the cover still has five different titles and the repeat is refilled with a draw.
        var library = Library(20);
        var locked = new[] { Id(7), Id(3), Id(7), Id(9) };
        var random = new Random(42);

        for (var round = 0; round < 100; round++)
        {
            var picked = Pick(library, locked: locked, random: random);

            Assert.Equal(Ids(7, 3, 9), picked.Take(3));
            Assert.Equal(5, picked.Count);
            Assert.Equal(5, picked.Distinct().Count());
        }
    }

    [Fact]
    public void A_title_locked_in_every_slot_leaves_the_other_slots_free()
    {
        var picked = Pick(Library(20), locked: new[] { Id(4), Id(4), Id(4), Id(4), Id(4) });

        Assert.Equal(Id(4), picked[0]);
        Assert.Equal(5, picked.Count);
        Assert.Equal(5, picked.Distinct().Count());
    }

    [Fact]
    public void Locks_beyond_the_slot_count_are_cut_before_repeats_are_removed()
    {
        // Two slots, locked to title 5 both. The third lock is for a slot the cover does not use, so it is not a way
        // to fill the slot that the repeat freed.
        var gone = Id(900);

        var picked = Pick(Library(20), style: new CoverStyle { SlotCount = 2 }, locked: new[] { Id(5), Id(5), gone });

        Assert.Equal(2, picked.Count);
        Assert.Equal(2, picked.Distinct().Count());
        Assert.Equal(Id(5), picked[0]);
        Assert.DoesNotContain(gone, picked);
    }

    [Fact]
    public void Art_mode_needs_backdrops()
    {
        var library = new List<TitleCandidate>
        {
            Title(1, backdrop: true, video: false),
            Title(2, backdrop: true, video: true),
            Title(3, backdrop: false, video: true),
            Title(4, backdrop: false, video: true),
            Title(5, backdrop: true, video: false),
            Title(6, backdrop: false, video: false),
            Title(7, backdrop: true, video: true),
        };

        var picked = Pick(library, CoverMode.Art);

        // Fewer titles have a backdrop than there are slots, so exactly those come back, with or without a video.
        Assert.Equal(Ids(1, 2, 5, 7).Order(), picked.Order());
    }

    [Fact]
    public void Scenes_mode_needs_video()
    {
        var library = new List<TitleCandidate>
        {
            Title(1, backdrop: true, video: false),
            Title(2, backdrop: true, video: true),
            Title(3, backdrop: false, video: true),
            Title(4, backdrop: false, video: true),
            Title(5, backdrop: true, video: false),
            Title(6, backdrop: false, video: false),
            Title(7, backdrop: true, video: true),
        };

        var picked = Pick(library, CoverMode.Scenes);

        Assert.Equal(Ids(2, 3, 4, 7).Order(), picked.Order());
    }

    [Fact]
    public void Manual_mode_takes_a_title_with_either_a_backdrop_or_a_video()
    {
        var library = new List<TitleCandidate>
        {
            Title(1, backdrop: true, video: false),
            Title(2, backdrop: false, video: true),
            Title(3, backdrop: false, video: false),
        };

        Assert.Equal(Ids(1, 2).Order(), Pick(library, CoverMode.Manual).Order());
    }

    [Fact]
    public void Unknown_mode_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Pick(Library(5), (CoverMode)99));

    [Fact]
    public void Avoids_previous_titles_when_enough_remain()
    {
        // Without the list, titles 1 to 5 are the whole cover. Ten other titles are there to take their place.
        var library = FiveThatHeadEveryPool(others: 10);
        var previous = Ids(1, 2, 3, 4, 5).ToArray();
        Assert.Equal(previous, Pick(library).Order());

        var picked = Pick(library, previous: previous);
        Assert.Equal(5, picked.Count);
        Assert.Empty(picked.Intersect(previous));

        // Part of a pool having been shown is enough to skip those titles: the others come first, then the rest.
        var partly = Pick(library, previous: Ids(1, 2).ToArray());
        Assert.Equal(5, partly.Count);
        Assert.Empty(partly.Intersect(Ids(1, 2)));
        Assert.All(Ids(3, 4, 5), id => Assert.Contains(id, partly));
    }

    [Fact]
    public void Avoids_previous_titles_when_exactly_enough_remain()
    {
        // Five titles head every pool and were shown; the other five are exactly what the cover needs.
        var library = FiveThatHeadEveryPool(others: 5);

        var picked = Pick(library, previous: Ids(1, 2, 3, 4, 5).ToArray());

        Assert.Equal(Ids(6, 7, 8, 9, 10).Order(), picked.Order());
    }

    [Fact]
    public void Reuses_previous_titles_in_a_small_library()
    {
        // Four titles in all, and the last cover showed every one of them: there is nothing else to show.
        var library = Library(4);
        var previous = library.Select(title => title.Id).ToArray();

        var picked = Pick(library, previous: previous);

        Assert.Equal(previous.Order(), picked.Order());
    }

    [Fact]
    public void Prefers_unused_titles_when_it_has_to_reuse_previous_ones()
    {
        // Seven titles for five slots, five of them shown last time: the two others are in, and three old ones fill up.
        var library = Library(7);
        var previous = Ids(1, 2, 3, 4, 5).ToArray();
        var random = new Random(42);

        for (var round = 0; round < 100; round++)
        {
            var picked = Pick(library, previous: previous, random: random);

            Assert.Equal(5, picked.Count);
            Assert.Equal(5, picked.Distinct().Count());
            Assert.All(Ids(6, 7), id => Assert.Contains(id, picked));
        }
    }

    [Fact]
    public void Keeps_to_its_pools_when_the_previous_cover_showed_every_title()
    {
        var library = ThreeDisjointTopLists();
        var previous = library.Select(title => title.Id).ToArray();

        var picked = Pick(library, previous: previous);

        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, picked.Select(GroupOf));
    }

    [Fact]
    public void Fewer_eligible_titles_than_slots_returns_fewer()
    {
        var library = Library(3);

        var picked = Pick(library);

        Assert.Equal(3, picked.Count);
        Assert.Equal(library.Select(title => title.Id).Order(), picked.Order());
    }

    [Fact]
    public void No_eligible_titles_returns_empty()
    {
        Assert.Empty(Pick(new List<TitleCandidate>()));

        // Titles without a backdrop do not count in art mode, nor titles without a video in scenes mode.
        var noBackdrops = new List<TitleCandidate> { Title(1, backdrop: false), Title(2, backdrop: false) };
        Assert.Empty(Pick(noBackdrops, CoverMode.Art));
        Assert.NotEmpty(Pick(noBackdrops, CoverMode.Scenes));

        var noVideo = new List<TitleCandidate> { Title(1, video: false), Title(2, video: false) };
        Assert.Empty(Pick(noVideo, CoverMode.Scenes));
        Assert.NotEmpty(Pick(noVideo, CoverMode.Art));
    }

    [Fact]
    public void Draws_from_each_enabled_pool()
    {
        var library = ThreeDisjointTopLists();

        // Three slots, three pools: one title from each of the three top lists, in the order of the pools.
        var three = Pick(library, style: new CoverStyle { SlotCount = 3 });
        Assert.Equal(new[] { 0, 1, 2 }, three.Select(GroupOf));

        // Free slot i draws from pool i mod 3.
        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, Pick(library).Select(GroupOf));
    }

    [Fact]
    public void Draws_only_from_the_pools_that_are_enabled()
    {
        var library = ThreeDisjointTopLists();

        var withoutTopRated = new CoverStyle { UseTopRated = false };
        Assert.Equal(new[] { 0, 2, 0, 2, 0 }, Pick(library, style: withoutTopRated).Select(GroupOf));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, Pick(library, style: OnlyPool(topRated: true)).Select(GroupOf));
    }

    [Fact]
    public void All_pools_are_used_when_none_is_enabled()
    {
        var library = ThreeDisjointTopLists();

        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, Pick(library, style: OnlyPool()).Select(GroupOf));
    }

    [Fact]
    public void Free_slots_are_counted_after_the_locked_ones()
    {
        var library = ThreeDisjointTopLists();

        var picked = Pick(library, locked: new[] { Id(900) });

        // The locked slot is outside the pools, so the first free slot is the first of the pools again.
        Assert.Equal(new[] { -1, 0, 1, 2, 0 }, picked.Select(GroupOf));
    }

    [Fact]
    public void Draws_uniformly_within_a_pool()
    {
        var library = ThreeDisjointTopLists();
        var style = new CoverStyle { SlotCount = 1 };
        var random = new Random(42);
        var drawn = new Dictionary<Guid, int>();

        for (var round = 0; round < 3000; round++)
        {
            var id = Assert.Single(Pick(library, style: style, random: random));
            drawn[id] = drawn.GetValueOrDefault(id) + 1;
        }

        // The most watched pool holds titles 1 to 5, so each should come up about 600 times in 3000.
        Assert.Equal(Ids(1, 2, 3, 4, 5).Order(), drawn.Keys.Order());
        Assert.All(drawn.Values, count => Assert.InRange(count, 450, 750));
    }

    [Fact]
    public void Most_watched_pool_takes_the_highest_play_counts()
    {
        int[] plays = { 5, 0, 9, 2, 7, 1, 8, 3 };
        var library = plays.Select((count, index) => Title(index + 1, plays: count)).ToList();

        // Eight titles make pools of 5, and five slots take a pool whole.
        var picked = Pick(library, style: OnlyPool(mostWatched: true));

        Assert.Equal(Ids(1, 3, 5, 7, 8).Order(), picked.Order());
    }

    [Fact]
    public void Top_rated_pool_takes_the_highest_ratings_and_ranks_unrated_titles_last()
    {
        // Five titles have a rating, one of them 0, which is still a rating. Three have none.
        float?[] ratings = { 8f, null, 9.5f, null, 7f, 0f, 9f, null };
        var library = ratings.Select((rating, index) => Title(index + 1, rating: rating)).ToList();

        var picked = Pick(library, style: OnlyPool(topRated: true));

        Assert.Equal(Ids(1, 3, 5, 6, 7).Order(), picked.Order());
    }

    [Fact]
    public void Recent_pool_takes_the_newest_titles()
    {
        int[] daysOld = { 40, 3, 25, 0, 12, 60, 1, 90 };
        var library = daysOld.Select((age, index) => Title(index + 1, daysOld: age)).ToList();

        var picked = Pick(library, style: OnlyPool(recent: true));

        Assert.Equal(Ids(2, 3, 4, 5, 7).Order(), picked.Order());
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Ties_go_to_the_newer_title_then_to_the_lower_id(bool mostWatched, bool topRated, bool recent)
    {
        // Every title has the same plays and rating. Title 6 is the newest, then 5, 4 and 3; titles 1 and 2 are equally
        // old. The ranking is 6, 5, 4, 3, 1, 2, so the pool of 5 leaves out title 2. Listed in a different order on
        // purpose.
        var library = new List<TitleCandidate>
        {
            Title(2, plays: 3, rating: 7.5f, daysOld: 9),
            Title(1, plays: 3, rating: 7.5f, daysOld: 9),
            Title(3, plays: 3, rating: 7.5f, daysOld: 4),
            Title(6, plays: 3, rating: 7.5f, daysOld: 1),
            Title(4, plays: 3, rating: 7.5f, daysOld: 3),
            Title(5, plays: 3, rating: 7.5f, daysOld: 2),
        };

        var picked = Pick(library, style: OnlyPool(mostWatched, topRated, recent));

        Assert.Equal(Ids(1, 3, 4, 5, 6).Order(), picked.Order());
    }

    [Theory]
    [InlineData(8, 5)] // the pool is never smaller than 5 titles
    [InlineData(16, 5)] // 30% of 16 is 4.8, still under that
    [InlineData(17, 6)] // 30% of 17 is 5.1, which rounds up
    [InlineData(40, 12)]
    [InlineData(100, 30)]
    public void Each_pool_is_cut_to_the_top_30_percent_but_never_below_five(int titles, int poolSize)
    {
        // Title 1 is the most watched, title 2 the next, and so on. Only that pool is on, so every title any cover
        // shows is in the pool, and over many covers all of them are.
        var library = Enumerable.Range(1, titles).Select(n => Title(n, plays: titles - n)).ToList();
        var random = new Random(42);
        var shown = new HashSet<Guid>();

        for (var round = 0; round < 2000; round++)
        {
            shown.UnionWith(Pick(library, style: OnlyPool(mostWatched: true), random: random));
        }

        Assert.Equal(Enumerable.Range(1, poolSize).Select(Id).Order(), shown.Order());
    }

    [Fact]
    public void Pools_are_sized_by_the_titles_that_can_be_used()
    {
        // 100 titles, but only the last ten have a backdrop, so art mode has ten to choose from and the pool is the
        // best five of them (30% of ten is three, and a pool is never below five), not the best thirty.
        var library = Enumerable.Range(1, 100).Select(n => Title(n, plays: n, backdrop: n > 90)).ToList();
        var random = new Random(42);
        var shown = new HashSet<Guid>();

        for (var round = 0; round < 500; round++)
        {
            shown.UnionWith(Pick(library, style: OnlyPool(mostWatched: true), random: random));
        }

        Assert.Equal(Enumerable.Range(96, 5).Select(Id).Order(), shown.Order());
    }

    [Theory]
    [InlineData(17)] // 30% is 5.1: pools of 6, where without the locked title they would be 5
    [InlineData(18)] // 30% is 5.4: pools of 6 either way, but without the locked title they would start at title 2
    public void Locked_titles_still_count_when_the_pools_are_sized_and_ranked(int titles)
    {
        // Title 1 is the most watched and is locked: it is never drawn again, but it keeps its place among the eligible
        // titles, so the pool is titles 1 to 6 and the free slots draw from titles 2 to 6.
        var library = Enumerable.Range(1, titles).Select(n => Title(n, plays: 100 - n)).ToList();
        var style = OnlyPool(mostWatched: true);
        var random = new Random(42);
        var shown = new HashSet<Guid>();

        for (var round = 0; round < 500; round++)
        {
            var picked = Pick(library, style: style, locked: new[] { Id(1) }, random: random);
            shown.UnionWith(picked.Skip(1)); // the locked title is first
        }

        Assert.Equal(Ids(2, 3, 4, 5, 6).Order(), shown.Order());
    }

    [Fact]
    public void The_same_random_gives_the_same_titles()
    {
        var library = Library(40);

        var first = Pick(library, random: new Random(42));
        var second = Pick(library, random: new Random(42));

        Assert.Equal(first, second);
        Assert.Contains(Enumerable.Range(0, 20), seed => !Pick(library, random: new Random(seed)).SequenceEqual(first));
    }

    [Fact]
    public void Result_does_not_depend_on_the_order_of_the_candidates()
    {
        var library = Library(40);
        var locked = new[] { Id(11) };
        var previous = Ids(1, 2, 3, 4, 5, 6, 7, 8).ToArray();
        var reversed = Enumerable.Reverse(library).ToList();
        var rotated = library.Skip(13).Concat(library.Take(13)).ToList();

        for (var seed = 0; seed < 30; seed++)
        {
            var expected = Pick(library, locked: locked, previous: previous, random: new Random(seed));

            Assert.Equal(5, expected.Count);
            Assert.Equal(expected, Pick(reversed, locked: locked, previous: previous, random: new Random(seed)));
            Assert.Equal(expected, Pick(rotated, locked: locked, previous: previous, random: new Random(seed)));
        }
    }

    [Fact]
    public void Does_not_change_what_it_is_given()
    {
        var library = Library(20);
        var libraryBefore = library.ToList();
        var locked = new List<Guid> { Id(3), Id(2) };
        var previous = new HashSet<Guid>(Ids(1, 4, 5));

        var picked = Pick(library, locked: locked, previous: previous);

        Assert.Equal(5, picked.Count);
        Assert.Equal(libraryBefore, library);
        Assert.Equal(Ids(3, 2), locked);
        Assert.Equal(Ids(1, 4, 5).Order(), previous.Order());
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        var titles = Library(5);
        var style = new CoverStyle();
        var none = Array.Empty<Guid>();
        var dice = new Random(42);

        // The refusal names the argument that is missing.
        static string? Blamed(Action call) => Assert.Throws<ArgumentNullException>(call).ParamName;

        Assert.Equal("allowed", Blamed(() => TitlePicker.Pick(null!, CoverMode.Art, style, none, none, dice)));
        Assert.Equal("style", Blamed(() => TitlePicker.Pick(titles, CoverMode.Art, null!, none, none, dice)));
        Assert.Equal("locked", Blamed(() => TitlePicker.Pick(titles, CoverMode.Art, style, null!, none, dice)));
        Assert.Equal("previous", Blamed(() => TitlePicker.Pick(titles, CoverMode.Art, style, none, null!, dice)));
        Assert.Equal("random", Blamed(() => TitlePicker.Pick(titles, CoverMode.Art, style, none, none, null!)));
    }
}
