using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// Each piece <c>Allowed</c> returns is (Start, End), where End is the LATEST SECOND A CLIP MAY START AT, not the
/// second where the piece stops: a clip of <c>clip</c> seconds started anywhere in [Start, End] ends inside the
/// allowed stretch. The expectations below spell that out: "60 to 530" for a 10 s clip means starts from 60 to 530,
/// and the last such clip ends at 540.
/// </summary>
public sealed class SceneWindowTests
{
    private const double Clip = 10;

    /// <summary>One line for the whole result ("60 to 90; 200 to 530"), so a failure reads as a diff.</summary>
    private static string Show(IEnumerable<(double Start, double End)> pieces) =>
        string.Join(
            "; ",
            pieces.Select(p => string.Create(
                CultureInfo.InvariantCulture,
                $"{Math.Round(p.Start, 6)} to {Math.Round(p.End, 6)}")));

    [Fact]
    public void Excludes_head_and_tail_10_percent()
    {
        var allowed = SceneWindow.Allowed(600, [], Clip);

        // The first 60 s and the last 60 s are out. The window is 60 to 540, so a 10 s clip may start from 60 up
        // to 530, and the clip that starts at 530 ends at 540, where the tail begins.
        Assert.Equal("60 to 530", Show(allowed));
        Assert.Equal(540, Assert.Single(allowed).End + Clip);
    }

    [Fact]
    public void Removes_excluded_segments()
    {
        // An intro at the start and a stretch marked as a commercial in the middle.
        var allowed = SceneWindow.Allowed(600, [(0, 90), (300, 360)], Clip);

        // 60 to 90 is inside the intro, so the first piece starts where the intro ends. The middle segment splits
        // what is left: a clip must end by 300 (start by 290) or start from 360 on.
        Assert.Equal("90 to 290; 360 to 530", Show(allowed));
    }

    [Fact]
    public void Drops_pieces_shorter_than_the_clip()
    {
        // Leaves 60 to 75 (15 s) and 500 to 540 (40 s).
        var tooShort = SceneWindow.Allowed(600, [(75, 500)], 20);
        Assert.Equal("500 to 520", Show(tooShort));

        // The same 15 s piece is kept for a clip it can hold.
        var fits = SceneWindow.Allowed(600, [(75, 500)], 15);
        Assert.Equal("60 to 60; 500 to 525", Show(fits));
    }

    [Fact]
    public void Keeps_a_piece_exactly_as_long_as_the_clip()
    {
        // 60 to 70 holds one 10 s clip, and only one place to start it.
        var allowed = SceneWindow.Allowed(600, [(70, 540)], Clip);

        Assert.Equal("60 to 60", Show(allowed));
    }

    [Fact]
    public void Merges_overlapping_touching_and_nested_exclusions_given_in_any_order()
    {
        // 150 to 250 and 200 to 300 overlap, 300 to 340 touches the end of the merged range, and 380 to 400 lies
        // inside 360 to 420. The entries are not in order.
        var allowed = SceneWindow.Allowed(600, [(380, 400), (300, 340), (200, 300), (360, 420), (150, 250)], Clip);

        // The exclusions merge into two stretches, 150 to 340 and 360 to 420 (the nested one adds nothing). That
        // leaves 60 to 150, 340 to 360 and 420 to 540; the middle one is 20 s long, so a 10 s clip there may
        // start anywhere from 340 to 350.
        Assert.Equal("60 to 140; 340 to 350; 420 to 530", Show(allowed));
    }

    [Fact]
    public void Clips_exclusions_that_reach_outside_the_runtime()
    {
        // Wholly outside the video: nothing to exclude.
        Assert.Equal("60 to 530", Show(SceneWindow.Allowed(600, [(-30, -10)], Clip)));
        Assert.Equal("60 to 530", Show(SceneWindow.Allowed(600, [(900, 1000)], Clip)));

        // Ending before the window starts, and starting before the video does but ending inside the window.
        Assert.Equal("60 to 530", Show(SceneWindow.Allowed(600, [(-50, 20)], Clip)));
        Assert.Equal("100 to 530", Show(SceneWindow.Allowed(600, [(-50, 100)], Clip)));

        // Running past the end of the video: only the part inside the window counts. It removes 500 to 540, so
        // the last start is 490.
        Assert.Equal("60 to 490", Show(SceneWindow.Allowed(600, [(500, 700)], Clip)));
    }

    [Fact]
    public void Ignores_empty_and_reversed_exclusions()
    {
        var allowed = SceneWindow.Allowed(
            600,
            [(300, 200), (400, 400), (double.NaN, 100), (100, double.NaN)],
            Clip);

        Assert.Equal("60 to 530", Show(allowed));
    }

    [Fact]
    public void Nothing_is_allowed_when_everything_is_excluded()
    {
        Assert.Empty(SceneWindow.Allowed(600, [(-1, 601)], Clip));
        Assert.Empty(SceneWindow.Allowed(600, [(0, 100), (90, 700)], Clip));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nothing_is_allowed_without_a_usable_runtime(double runtimeSeconds)
    {
        Assert.Empty(SceneWindow.Allowed(runtimeSeconds, [], Clip));

        // A head and tail share over a half turns the window of a negative runtime around, into a stretch with a
        // start and an end that a short clip would fit in, if the runtime itself were not refused first.
        Assert.Empty(SceneWindow.Allowed(runtimeSeconds, [], 1, headTail: 0.8));
    }

    [Fact]
    public void A_clip_of_30_seconds_needs_a_title_of_more_than_37_seconds()
    {
        // The longest cover is 30 s, and one slot may take all of it. 120 s leaves 12 to 108, starts from 12 to 78;
        // 36 s leaves 3.6 to 32.4, 28.8 s, too short; 37.5 s leaves exactly 30 s.
        Assert.Equal("12 to 78", Show(SceneWindow.Allowed(120, [], 30)));
        Assert.Empty(SceneWindow.Allowed(36, [], 30));
        Assert.Equal("3.75 to 3.75", Show(SceneWindow.Allowed(37.5, [], 30)));
    }

    [Fact]
    public void Nothing_is_allowed_when_the_clip_is_longer_than_the_window()
    {
        // 100 s of video leaves 10 to 90, 80 s, for a clip of 81 s.
        Assert.Empty(SceneWindow.Allowed(100, [], 81));
        Assert.Equal("10 to 10", Show(SceneWindow.Allowed(100, [], 80)));
    }

    [Fact]
    public void Head_and_tail_share_can_be_changed()
    {
        Assert.Equal("0 to 590", Show(SceneWindow.Allowed(600, [], Clip, headTail: 0)));
        Assert.Equal("150 to 440", Show(SceneWindow.Allowed(600, [], Clip, headTail: 0.25)));

        // Half skipped at each end leaves nothing.
        Assert.Empty(SceneWindow.Allowed(600, [], Clip, headTail: 0.5));
        Assert.Empty(SceneWindow.Allowed(600, [], Clip, headTail: 0.8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_a_clip_that_is_not_a_positive_length(double clipSeconds)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => SceneWindow.Allowed(600, [], clipSeconds));
        Assert.Equal("clipSeconds", error.ParamName);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_a_head_and_tail_share_that_is_not_a_fraction_from_zero_up(double headTail)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => SceneWindow.Allowed(600, [], Clip, headTail));
        Assert.Equal("headTail", error.ParamName);
    }

    [Fact]
    public void Rejects_a_missing_exclusion_list()
    {
        var error = Assert.Throws<ArgumentNullException>(() => SceneWindow.Allowed(600, null!, Clip));
        Assert.Equal("excluded", error.ParamName);

        // Refused whatever the runtime, not only once the list is first read.
        Assert.Throws<ArgumentNullException>(() => SceneWindow.Allowed(0, null!, Clip));
    }
}
