using System;
using SkiaSharp;

namespace Jellyfin.Plugin.LibraryCoverMaker.Sources;

/// <summary>
/// Scores how good a video frame would look on a cover, from 0 to 1. The frame is resized to a copy 160 px wide
/// (keeping its aspect) and measured on that copy. Luma is Rec. 709, <c>0.2126 R + 0.7152 G + 0.0722 B</c> on
/// 0..1 values; <c>b</c> is the mean luma. The score is 0 when <c>b</c> is under 0.08 or over 0.95; otherwise it is
/// <c>0.30 * exposure + 0.25 * contrast + 0.20 * color + 0.25 * sharpness</c>, each term clamped to [0, 1]:
/// <list type="bullet">
/// <item><description>exposure: <c>1 - |b - 0.45| / 0.45</c></description></item>
/// <item><description>contrast: the standard deviation of luma over the whole copy, divided by 0.25</description></item>
/// <item><description>color: the mean HSV saturation, <c>(max - min) / max</c> per pixel and 0 for a black one,
/// divided by 0.5</description></item>
/// <item><description>sharpness: the variance of the 3x3 Laplacian of luma (centre -4, the four neighbours +1,
/// taken at every pixel that has all four), divided by 0.01</description></item>
/// </list>
/// When more than 35% of the rows have a mean luma under 0.04 (black bars), the score is halved. The weights add
/// up to 1, so the score never leaves [0, 1].
/// </summary>
/// <remarks>
/// Static and without state, so it may be called from any thread. A frame is only read: it is not changed and not
/// disposed. Transparent pixels count as black.
/// </remarks>
public static class FrameScorer
{
    /// <summary>
    /// The lowest score worth using for a cover: the scene finder drops frames that score below it.
    /// </summary>
    public const double Floor = 0.35;

    // Every frame is measured at this width, whatever its own.
    private const int AnalysisWidth = 160;

    // A mean luma outside these limits scores 0: nothing to see, or nothing but glare.
    private const double DarkestMeanLuma = 0.08;
    private const double BrightestMeanLuma = 0.95;

    // The four terms and their weights, which add up to 1.
    private const double ExposureWeight = 0.30;
    private const double ContrastWeight = 0.25;
    private const double ColorWeight = 0.20;
    private const double SharpnessWeight = 0.25;

    // The mean luma with the best exposure, and what earns the full mark on each of the other terms.
    private const double BestMeanLuma = 0.45;
    private const double FullContrastDeviation = 0.25;
    private const double FullColorSaturation = 0.5;
    private const double FullSharpnessVariance = 0.01;

    // Black bars: a row is black under this mean luma, and the score is halved when more than this percent of
    // the rows are. The percent is an integer so that exactly 35% of the rows is not "more than" 35%.
    private const double BlackRowLuma = 0.04;
    private const int LetterboxPercent = 35;
    private const double LetterboxFactor = 0.5;

    // Rec. 709 luma coefficients.
    private const double RedCoefficient = 0.2126;
    private const double GreenCoefficient = 0.7152;
    private const double BlueCoefficient = 0.0722;

    // A big shrink has to average the source pixels. A bilinear tap alone reads only four of them for each pixel
    // it makes, so fine detail would alias into false sharpness; the mipmaps make the downscale an average.
    private static readonly SKSamplingOptions Downscale = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Scores a frame; see the class description for how.</summary>
    /// <param name="frame">The frame, in any size and pixel layout. A frame with no pixels scores 0.</param>
    /// <returns>A score from 0 (useless as a cover) to 1 (the best a frame can be).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The frame has a size but its pixels cannot be read, so no copy of it can be made.
    /// </exception>
    public static double Score(SKBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return 0;
        }

        using var copy = AnalysisCopy(frame);
        var measured = Measure(copy);

        if (measured.MeanLuma < DarkestMeanLuma || measured.MeanLuma > BrightestMeanLuma)
        {
            return 0;
        }

        var exposure = Clamp01(1 - (Math.Abs(measured.MeanLuma - BestMeanLuma) / BestMeanLuma));
        var contrast = Clamp01(measured.LumaDeviation / FullContrastDeviation);
        var color = Clamp01(measured.MeanSaturation / FullColorSaturation);
        var sharpness = Clamp01(measured.LaplacianVariance / FullSharpnessVariance);

        var score = (ExposureWeight * exposure)
            + (ContrastWeight * contrast)
            + (ColorWeight * color)
            + (SharpnessWeight * sharpness);

        var letterboxed = measured.BlackRows * 100 > measured.Rows * LetterboxPercent;
        return letterboxed ? score * LetterboxFactor : score;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    /// <summary>
    /// The frame at <see cref="AnalysisWidth"/> px wide, as 8-bit RGBA with premultiplied alpha, which is also
    /// what turns a transparent pixel into a black one. The caller disposes it.
    /// </summary>
    private static SKBitmap AnalysisCopy(SKBitmap frame)
    {
        var height = Math.Max(1, (int)Math.Round((double)frame.Height * AnalysisWidth / frame.Width));
        var info = new SKImageInfo(AnalysisWidth, height, SKColorType.Rgba8888, SKAlphaType.Premul);

        return frame.Resize(info, Downscale)
            ?? throw new InvalidOperationException("The frame could not be resized to be scored.");
    }

    /// <summary>
    /// Measures a copy made by <see cref="AnalysisCopy"/> in one pass over its rows. The Laplacian of a row needs
    /// the rows above and below it, so the luma of the last three rows is kept in a ring and nothing else is.
    /// </summary>
    private static Measurements Measure(SKBitmap copy)
    {
        var height = copy.Height;
        var stride = copy.RowBytes;
        ReadOnlySpan<byte> pixels = copy.GetPixelSpan();
        Span<double> ring = stackalloc double[3 * AnalysisWidth];

        var luma = default(RunningVariance);
        var laplacian = default(RunningVariance);
        var saturationSum = 0.0;
        var blackRows = 0;

        for (var y = 0; y < height; y++)
        {
            var row = ring.Slice((y % 3) * AnalysisWidth, AnalysisWidth);
            var rowSum = 0.0;
            var offset = y * stride;

            for (var x = 0; x < AnalysisWidth; x++, offset += 4)
            {
                int red = pixels[offset];
                int green = pixels[offset + 1];
                int blue = pixels[offset + 2];

                var value = ((RedCoefficient * red) + (GreenCoefficient * green) + (BlueCoefficient * blue)) / 255.0;
                row[x] = value;
                rowSum += value;
                luma.Add(value);

                var max = Math.Max(red, Math.Max(green, blue));
                if (max > 0)
                {
                    saturationSum += (max - Math.Min(red, Math.Min(green, blue))) / (double)max;
                }
            }

            if (rowSum / AnalysisWidth < BlackRowLuma)
            {
                blackRows++;
            }

            if (y >= 2)
            {
                // Rows y - 2, y - 1 and y are in the ring: this is the Laplacian of the one in the middle.
                var upper = ring.Slice(((y - 2) % 3) * AnalysisWidth, AnalysisWidth);
                var middle = ring.Slice(((y - 1) % 3) * AnalysisWidth, AnalysisWidth);
                for (var x = 1; x < AnalysisWidth - 1; x++)
                {
                    laplacian.Add(upper[x] + row[x] + middle[x - 1] + middle[x + 1] - (4 * middle[x]));
                }
            }
        }

        return new Measurements(
            luma.Mean,
            Math.Sqrt(luma.Variance),
            saturationSum / ((double)AnalysisWidth * height),
            laplacian.Variance,
            blackRows,
            height);
    }

    private readonly record struct Measurements(
        double MeanLuma,
        double LumaDeviation,
        double MeanSaturation,
        double LaplacianVariance,
        int BlackRows,
        int Rows);

    /// <summary>
    /// Mean and variance over a stream of values, by Welford's method. Unlike a sum and a sum of squares, it
    /// gives a variance of exactly 0 for values that are all equal, such as a flat frame.
    /// </summary>
    private struct RunningVariance
    {
        private long _count;
        private double _mean;
        private double _squaredDistance;

        public readonly double Mean => _mean;

        /// <summary>The variance of the values seen, not corrected for a sample (0 for none).</summary>
        public readonly double Variance => _count == 0 ? 0 : _squaredDistance / _count;

        public void Add(double value)
        {
            _count++;
            var delta = value - _mean;
            _mean += delta / _count;
            _squaredDistance += delta * (value - _mean);
        }
    }
}
