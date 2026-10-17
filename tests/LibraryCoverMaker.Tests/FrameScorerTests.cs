using System;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using SkiaSharp;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The scorer looks at a copy of the frame that is 160 px wide. A frame built exactly that wide is read as it is,
/// so the numbers worked out by hand in the tests below hold to rounding error; the few tests that need the
/// resize use bigger frames and say so.
/// </summary>
public sealed class FrameScorerTests
{
    private const int W = 160;
    private const int H = 90;

    private static readonly SKColor Black = new(0, 0, 0);
    private static readonly SKColor White = new(255, 255, 255);

    [Fact]
    public void Floor_is_0_35() => Assert.Equal(0.35, FrameScorer.Floor);

    [Fact]
    public void Black_frame_scores_zero()
    {
        using var frame = Solid(W, H, 0, 0, 0);

        Assert.Equal(0, FrameScorer.Score(frame));
    }

    [Fact]
    public void White_frame_scores_zero()
    {
        using var frame = Solid(W, H, 255, 255, 255);

        Assert.Equal(0, FrameScorer.Score(frame));
    }

    [Fact]
    public void A_frame_with_a_mean_luma_under_8_percent_scores_zero()
    {
        // 20/255 is 7.84 percent and 21/255 is 8.24 percent. A flat frame isolates the gate: just above it, the
        // exposure term alone still gives a small positive score.
        using var below = Solid(W, H, 20, 20, 20);
        using var above = Solid(W, H, 21, 21, 21);

        Assert.Equal(0, FrameScorer.Score(below));
        Assert.Equal(Expected(21 / 255.0, 0, 0, 0), FrameScorer.Score(above), 9);
        Assert.True(FrameScorer.Score(above) > 0.05);
    }

    [Fact]
    public void A_frame_with_a_mean_luma_over_95_percent_scores_zero()
    {
        // A flat frame this bright would score zero from its exposure term anyway, so the gate is isolated with
        // stripes, whose contrast and sharpness would give a clearly positive score. The mean luma of the stripes
        // is the average of the two grays: 242/255 is 94.9 percent and 243/255 is 95.3 percent.
        using var below = Stripes(255, 229);
        using var above = Stripes(255, 231);

        Assert.Equal(ExpectedForStripes(255, 229), FrameScorer.Score(below), 9);
        Assert.True(FrameScorer.Score(below) > 0.25);
        Assert.Equal(0, FrameScorer.Score(above));
    }

    [Theory]
    [InlineData(26)]  // 10 percent luma: barely past the dark gate
    [InlineData(51)]  // 20 percent
    [InlineData(115)] // 45 percent, the best exposure
    [InlineData(179)] // 70 percent, as far above the best as 20 percent is below it
    [InlineData(230)] // 90 percent: the exposure term has run out
    public void A_flat_gray_frame_scores_its_exposure_alone(byte gray)
    {
        using var frame = Solid(W, H, gray, gray, gray);

        // No contrast, no color, no edges: only the 0.30 exposure term is left.
        Assert.Equal(Expected(gray / 255.0, 0, 0, 0), FrameScorer.Score(frame), 9);
    }

    [Theory]
    [InlineData(255, 0, 0)]     // pure red: luma 21.3 percent, saturation 1
    [InlineData(0, 255, 0)]     // pure green: luma 71.5 percent
    [InlineData(0, 0, 255)]     // pure blue: luma 7.2 percent in Rec. 709, under the dark gate, so zero
    [InlineData(200, 150, 150)] // HSV saturation 0.25 (HSL would say 0.31): half of what the color term wants
    [InlineData(120, 90, 60)]   // HSV saturation 0.5, and a luma well inside the gates
    public void A_flat_colored_frame_adds_its_saturation_term(byte r, byte g, byte b)
    {
        using var frame = Solid(W, H, r, g, b);

        Assert.Equal(Expected(Luma(r, g, b), 0, Saturation(r, g, b), 0), FrameScorer.Score(frame), 9);
    }

    [Fact]
    public void Mean_saturation_counts_black_pixels_as_unsaturated()
    {
        // The left quarter of the frame is yellow (saturation 1) and the rest black, a vertical split so that no
        // row is black. The mean saturation is taken over ALL the pixels, so it is 0.25 and the color term is
        // half full; taken over the non-black pixels only it would be 1.
        using var frame = Frame(W, H, (x, _) => x < W / 4 ? new SKColor(255, 255, 0) : Black);

        var yellow = Luma(255, 255, 0);

        // A quarter of the pixels at luma `yellow`, the rest at 0: mean yellow / 4 and a deviation of
        // sqrt(1/4 * 3/4) * yellow. The 3x3 Laplacian is non-zero only in the two columns either side of the
        // edge (-yellow on the last yellow column, +yellow on the first black one), on the 88 interior rows: its
        // variance is yellow^2 * (2 * 88) / (158 * 88) = 2 * yellow^2 / 158.
        var expected = Expected(yellow / 4, Math.Sqrt(0.25 * 0.75) * yellow, 0.25, 2 * yellow * yellow / 158);

        Assert.Equal(expected, FrameScorer.Score(frame), 9);
    }

    [Fact]
    public void Contrast_and_sharpness_are_measured_in_their_linear_range()
    {
        // The top half is 77/255 gray and the bottom half 128/255: one horizontal edge with a step of 0.2.
        const byte upper = 77;
        const byte lower = 128;
        using var frame = Frame(W, H, (_, y) => y < H / 2 ? Gray(upper) : Gray(lower));

        var step = (lower - upper) / 255.0;

        // Two equal halves: the standard deviation of luma is half the step. The 3x3 Laplacian (centre -4, the
        // four neighbours +1) is zero everywhere except on the two rows either side of the edge, where it is
        // +step and -step. It is taken on the 158 x 88 pixels that have all four neighbours, so its variance is
        // step^2 * (2 * 158) / (158 * 88) = step^2 / 44. (A Laplacian over all eight neighbours would be three
        // times as strong at the edge and fail here.)
        var expected = Expected((upper + lower) / 2.0 / 255, step / 2, 0, step * step / 44);

        Assert.Equal(expected, FrameScorer.Score(frame), 9);
    }

    [Fact]
    public void A_smooth_gradient_has_contrast_but_no_sharpness()
    {
        // Luma rising evenly from 20 percent on the left to 80 percent on the right, in 8-bit steps: 160 columns
        // equally spaced over a range of 0.6, ends included. The mean is 0.5, and the deviation of n such values
        // is range * sqrt((n + 1) / (12 * (n - 1))). Neighbouring columns differ by less than one level, so the
        // Laplacian is rounding error and the sharpness term is nil.
        using var frame = Frame(W, H, (x, _) => Gray((int)Math.Round(51 + (153.0 * x / (W - 1)))));

        var deviation = 0.6 * Math.Sqrt((W + 1.0) / (12.0 * (W - 1)));

        Assert.Equal(Expected(0.5, deviation, 0, 0), FrameScorer.Score(frame), 3);
    }

    [Fact]
    public void Fine_stripes_reach_full_contrast_and_sharpness()
    {
        using var frame = Stripes(255, 0);

        // Mean luma 0.5 gives exposure 0.889. A luma deviation of 0.5 and a Laplacian of +-2 (variance 4) are far
        // past the limits that count as full contrast and full sharpness, so both clamp to 1.
        Assert.Equal((0.30 * (1 - (0.05 / 0.45))) + 0.25 + 0.25, FrameScorer.Score(frame), 9);
    }

    [Fact]
    public void A_perfect_frame_scores_close_to_one_and_never_above()
    {
        // Red and green columns: mean luma 46.4 percent (exposure 0.97), luma deviation 25.1 percent (full
        // contrast), saturation 1 (full color) and a luma step of 0.5 between columns (full sharpness).
        using var frame = Frame(W, H, (x, _) => x % 2 == 0 ? new SKColor(255, 0, 0) : new SKColor(0, 255, 0));

        var red = Luma(255, 0, 0);
        var green = Luma(0, 255, 0);
        var step = green - red;
        var expected = Expected((red + green) / 2, step / 2, 1, 4 * step * step);

        var score = FrameScorer.Score(frame);

        Assert.Equal(expected, score, 9);
        Assert.InRange(score, 0.98, 1.0);
    }

    [Fact]
    public void Sharp_colorful_frame_beats_its_blurred_copy()
    {
        using var sharp = ColorfulCells(W, H);
        using var blurred = Blurred(sharp, sigma: 3);

        var sharpScore = FrameScorer.Score(sharp);
        var blurredScore = FrameScorer.Score(blurred);

        // Blurring takes away the edges and a little contrast; exposure and color stay about the same.
        Assert.True(sharpScore > blurredScore + 0.15, $"sharp {sharpScore:F3}, blurred {blurredScore:F3}");
    }

    [Fact]
    public void Colorful_frame_beats_its_gray_copy()
    {
        using var colorful = ColorfulCells(W, H);
        using var gray = GrayCopy(colorful);

        var colorfulScore = FrameScorer.Score(colorful);
        var grayScore = FrameScorer.Score(gray);

        // The gray copy has the same luma, so the same exposure, contrast and edges: what it lacks is the color
        // term, worth up to 0.20.
        Assert.True(colorfulScore > grayScore + 0.15, $"colorful {colorfulScore:F3}, gray {grayScore:F3}");
    }

    [Fact]
    public void Letterboxed_frame_scores_below_floor()
    {
        // Mid-gray noise (115/255 is the best exposure) with black bars at the top and the bottom. "40% black rows
        // top and bottom" can mean 40 percent of the rows in all or 40 percent at each end, so both are tried:
        // 18 rows at each end of the 90 (40 percent in all) and 36 at each end (80 percent in all).
        using var content = GrayNoise(W, H, center: 115, spread: 24);
        var contentScore = FrameScorer.Score(content);

        // The content on its own is a good frame; it is the bars that sink it.
        Assert.True(contentScore >= FrameScorer.Floor, $"content alone {contentScore:F3}");

        foreach (var barRows in new[] { 18, 36 })
        {
            using var boxed = Letterboxed(content, barRows);
            var boxedScore = FrameScorer.Score(boxed);

            Assert.True(boxedScore < FrameScorer.Floor, $"{barRows} bar rows at each end: {boxedScore:F3}");

            // Halving is what does it: without it, the same frame would have cleared the floor.
            Assert.True(boxedScore * 2 >= FrameScorer.Floor, $"{barRows} bar rows at each end: {boxedScore:F3}");
        }
    }

    [Theory]
    [InlineData(35, 0, false)]  // exactly 35 percent of the rows is not "more than" 35 percent
    [InlineData(36, 0, true)]
    [InlineData(40, 9, true)]   // rows at 3.5 percent luma still count as black
    [InlineData(40, 11, false)] // rows at 4.3 percent luma do not
    public void More_than_35_percent_of_rows_under_4_percent_luma_halves_the_score(
        int barRows,
        byte barGray,
        bool halved)
    {
        // 100 rows, so a row is a percent. A bar at the top, flat mid-gray under it.
        const int height = 100;
        const byte content = 128;
        using var frame = Frame(W, height, (_, y) => y < barRows ? Gray(barGray) : Gray(content));

        var bar = barGray / 255.0;
        var body = content / 255.0;
        var share = barRows / (double)height;
        var step = body - bar;

        // Two flat regions: a two-point distribution for the mean and deviation. The Laplacian is non-zero only
        // on the two rows either side of the bar's edge (+-step), out of the 98 interior rows.
        var unpenalized = Expected(
            (share * bar) + ((1 - share) * body),
            Math.Sqrt(share * (1 - share)) * step,
            0,
            step * step / 49);

        Assert.Equal(halved ? unpenalized / 2 : unpenalized, FrameScorer.Score(frame), 9);
    }

    [Fact]
    public void A_black_row_is_decided_by_its_mean_luma_not_by_its_brightest_pixel()
    {
        // Burned-in subtitles sit in the bars, so a bar row is seldom all black. The mid-gray noise frame has 18
        // bar rows at each end (40 percent in all), and every bar row has a few white columns at its right end.
        // With 6 of the 160 the row's mean luma is 3.75 percent, under the 4 percent of a black row: the frame is
        // letterboxed and its score is halved. With 7 it is 4.4 percent: no black row, no halving. The two frames
        // are otherwise almost the same, so their scores come in the ratio of one to two. (A rule that looked at
        // the brightest pixel of a row would find no black row in either, and the ratio would be 1.)
        using var content = GrayNoise(W, H, center: 115, spread: 24);
        using var halved = Letterboxed(content, barRows: 18, whiteColumns: 6);
        using var whole = Letterboxed(content, barRows: 18, whiteColumns: 7);

        var ratio = FrameScorer.Score(halved) / FrameScorer.Score(whole);

        Assert.InRange(ratio, 0.45, 0.55);
    }

    [Fact]
    public void A_frame_wider_than_160_px_scores_like_its_160_px_version()
    {
        using var small = ColorfulCells(W, H);
        using var large = Enlarged(small, 4);

        Assert.Equal(640, large.Width);
        Assert.Equal(FrameScorer.Score(small), FrameScorer.Score(large), 3);
    }

    [Fact]
    public void A_very_wide_and_short_frame_is_scored_on_a_one_row_copy()
    {
        // 4000 x 10 at 160 px wide would be 0.4 of a row: the copy keeps one row instead of none. A flat gray has
        // no contrast, color or edges at any size, so it scores its exposure alone.
        using var frame = Solid(4000, 10, 115, 115, 115);

        Assert.Equal(Expected(115 / 255.0, 0, 0, 0), FrameScorer.Score(frame), 9);
    }

    [Theory]
    [InlineData(480, 270)] // 3 times as wide as the copy
    [InlineData(800, 450)] // 5 times
    public void Detail_finer_than_the_analysis_size_does_not_count_as_sharpness(int width, int height)
    {
        // A one-pixel checkerboard is the sharpest picture there is at this size, but at 160 px wide it is a flat
        // mid-gray, and that is the picture that is scored: exposure only, about 0.267. The scales are odd on
        // purpose. At an even scale a bilinear tap falls between two source pixels and averages them by luck;
        // at an odd one it lands on a single pixel, and only an averaging downscale gets rid of the detail.
        using var frame = Frame(width, height, (x, y) => (x + y) % 2 == 0 ? White : Black);

        Assert.InRange(FrameScorer.Score(frame), 0.25, 0.28);
    }

    [Fact]
    public void The_pixel_layout_of_the_frame_does_not_change_the_score()
    {
        using var rgba = ColorfulCells(W, H, SKColorType.Rgba8888);
        using var bgra = ColorfulCells(W, H, SKColorType.Bgra8888);

        Assert.Equal(FrameScorer.Score(rgba), FrameScorer.Score(bgra), 12);
    }

    [Fact]
    public void Transparent_pixels_count_as_black()
    {
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var clear = new SKBitmap(info);
        clear.Erase(SKColors.Transparent);
        Assert.Equal(0, FrameScorer.Score(clear));

        // Half-transparent white over black is a mid-gray: the same score as an opaque gray 128.
        using var half = new SKBitmap(info);
        half.Erase(new SKColor(255, 255, 255, 128));
        using var gray = Solid(W, H, 128, 128, 128);
        Assert.Equal(FrameScorer.Score(gray), FrameScorer.Score(half), 2);
    }

    [Fact]
    public void Scoring_leaves_the_frame_as_it_was()
    {
        // Bigger than 160 px, so the copy that gets resized is a real one.
        using var frame = ColorfulCells(320, 180);
        var before = frame.Bytes;

        var first = FrameScorer.Score(frame);

        Assert.Equal(before, frame.Bytes);
        Assert.Equal(first, FrameScorer.Score(frame));
    }

    [Fact]
    public void A_missing_frame_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => FrameScorer.Score(null!));
    }

    [Fact]
    public void A_frame_without_pixels_scores_zero()
    {
        using var empty = new SKBitmap();

        Assert.Equal(0, FrameScorer.Score(empty));
    }

    // The scoring formula, written out once more, on its own. The tests work out a frame's four statistics by hand
    // from how they built it, so the scorer's measuring is held against the construction and its arithmetic
    // against this. The letterbox rule is left out; the tests that need it halve the result themselves.
    private static double Expected(double meanLuma, double lumaStdDev, double meanSaturation, double laplacianVariance)
    {
        if (meanLuma < 0.08 || meanLuma > 0.95)
        {
            return 0;
        }

        return (0.30 * Clamp01(1 - (Math.Abs(meanLuma - 0.45) / 0.45)))
            + (0.25 * Clamp01(lumaStdDev / 0.25))
            + (0.20 * Clamp01(meanSaturation / 0.5))
            + (0.25 * Clamp01(laplacianVariance / 0.01));
    }

    // Stripes of two grays, one pixel wide: the luma mean is their average, the deviation half their gap, and the
    // Laplacian is +-2 gaps everywhere inside, so its variance is (2 * gap)^2.
    private static double ExpectedForStripes(byte even, byte odd)
    {
        var gap = Math.Abs(even - odd) / 255.0;
        return Expected((even + odd) / 2.0 / 255, gap / 2, 0, 4 * gap * gap);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    // Rec. 709 luma on 0..1 values, from 0..255 channels.
    private static double Luma(int r, int g, int b) => ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;

    // HSV saturation: (max - min) / max, and 0 for black.
    private static double Saturation(int r, int g, int b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    private static SKColor Gray(int value) => new((byte)value, (byte)value, (byte)value);

    private static SKBitmap Frame(
        int width,
        int height,
        Func<int, int, SKColor> pixel,
        SKColorType colorType = SKColorType.Rgba8888)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, colorType, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, pixel(x, y));
            }
        }

        return bitmap;
    }

    private static SKBitmap Solid(int width, int height, byte r, byte g, byte b)
    {
        var color = new SKColor(r, g, b);
        return Frame(width, height, (_, _) => color);
    }

    // One-pixel-wide vertical stripes, W x H, alternating between two grays.
    private static SKBitmap Stripes(byte even, byte odd) =>
        Frame(W, H, (x, _) => x % 2 == 0 ? Gray(even) : Gray(odd));

    // Cells of 10 x 10 px in saturated, fairly bright colors: hard edges everywhere, and a mean luma and
    // saturation that pass every gate.
    private static SKBitmap ColorfulCells(int width, int height, SKColorType colorType = SKColorType.Rgba8888)
    {
        const int cell = 10;
        var sequence = new Sequence(11);
        var columns = (width + cell - 1) / cell;
        var rows = (height + cell - 1) / cell;

        var colors = new SKColor[columns * rows];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = SKColor.FromHsv(sequence.Next(0, 359), sequence.Next(75, 100), sequence.Next(60, 100));
        }

        return Frame(width, height, (x, y) => colors[((y / cell) * columns) + (x / cell)], colorType);
    }

    // The blurred copy is drawn through a canvas with a blur filter; clamping the edges keeps the borders from
    // fading to transparent.
    private static SKBitmap Blurred(SKBitmap source, float sigma)
    {
        var blurred = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(blurred);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
        canvas.DrawBitmap(source, 0, 0, paint);
        return blurred;
    }

    // The same picture with every pixel's gray taken from its Rec. 709 luma.
    private static SKBitmap GrayCopy(SKBitmap source) =>
        Frame(
            source.Width,
            source.Height,
            (x, y) =>
            {
                var color = source.GetPixel(x, y);
                return Gray((int)Math.Round(Luma(color.Red, color.Green, color.Blue) * 255));
            });

    // Nearest-neighbour enlargement by a whole factor, so no new colors appear.
    private static SKBitmap Enlarged(SKBitmap source, int factor) =>
        Frame(
            source.Width * factor,
            source.Height * factor,
            (x, y) => source.GetPixel(x / factor, y / factor));

    // The same frame with its first and last rows turned black. The last `whiteColumns` columns of those rows stay
    // white, like a subtitle burned into the bars.
    private static SKBitmap Letterboxed(SKBitmap source, int barRows, int whiteColumns = 0) =>
        Frame(
            source.Width,
            source.Height,
            (x, y) => y < barRows || y >= source.Height - barRows
                ? (x >= source.Width - whiteColumns ? White : Black)
                : source.GetPixel(x, y));

    // Gray noise, every pixel an independent draw from center - spread to center + spread.
    private static SKBitmap GrayNoise(int width, int height, int center, int spread)
    {
        var sequence = new Sequence(7);
        return Frame(width, height, (_, _) => Gray(sequence.Next(center - spread, center + spread)));
    }

    // A small fixed pseudo-random sequence (xorshift32), so the noise and the colors are the same on every
    // machine and runtime.
    private sealed class Sequence(uint seed)
    {
        private uint _state = seed;

        public int Next(int minInclusive, int maxInclusive)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return minInclusive + (int)(_state % (uint)(maxInclusive - minInclusive + 1));
        }
    }
}
