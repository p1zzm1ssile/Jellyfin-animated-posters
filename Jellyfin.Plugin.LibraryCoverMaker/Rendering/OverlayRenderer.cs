using System;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>
/// Draws the part of a cover that stays still while the pictures move: a transparent PNG with the darkening, the
/// rounded white frame and the title. The render lays it over every frame of the animation. The title is shaped with
/// HarfBuzz, so it is kerned and laid out like the page's browser laid it out.
/// </summary>
/// <remarks>
/// The geometry is the cover page's design, a canvas of 960 by 540 pixels, scaled by <c>width / 960</c>. The page's
/// shadows are canvas shadows, where a blur of B is a Gaussian with a standard deviation of B / 2; the constants below
/// carry that standard deviation.
/// </remarks>
public sealed class OverlayRenderer
{
    // Lengths in the page's 960 by 540 canvas; the renderer multiplies each by the output scale.
    private const float DesignWidth = 960f;
    private const float FrameLeft = 24f;
    private const float FrameTop = 22f;
    private const float FrameStroke = 4f;
    private const float FrameRadius = 22f;
    private const float FrameShadowSigma = 3f; // the page's blur of 6
    private const float TitleMargin = 120f; // the title is kept this much narrower than the canvas
    private const float TitleShadowSigma = 7f; // the page's blur of 14
    private const float TitleShadowOffsetY = 3f;

    // The middle of the title sits this fraction of the text size below the canvas center.
    private const float TitleDrop = 0.04f;

    // The documented range of CoverStyle.Darken, in percent, is 0 to 70. A value beyond it is clamped to it.
    private const int MaxDarken = 70;

    private static readonly SKColor FrameShadowColor = new(0, 0, 0, 89); // rgba(0, 0, 0, .35)
    private static readonly SKColor TitleShadowColor = new(0, 0, 0, 153); // rgba(0, 0, 0, .6)

    /// <summary>
    /// Draws the overlay for a cover. The title is the style's, or the library's name when the style has none, in
    /// uppercase and shrunk to fit. A darkening outside 0 to 70 percent is clamped, and a text size of 0 or less
    /// draws no title.
    /// </summary>
    /// <param name="style">How the cover looks.</param>
    /// <param name="libraryName">The text to use when the style has no title of its own.</param>
    /// <param name="width">Width of the cover in pixels. The page's design is scaled by this over 960.</param>
    /// <param name="height">Height of the cover in pixels.</param>
    /// <returns>The overlay as PNG bytes, with an alpha channel.</returns>
    public byte[] RenderPng(CoverStyle style, string libraryName, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(libraryName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var scale = width / DesignWidth;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException($"SkiaSharp could not create a {width} by {height} surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        DrawDarkening(canvas, style.Darken, width, height);
        if (style.Frame)
        {
            DrawFrame(canvas, scale, width, height);
        }

        DrawTitle(canvas, style, libraryName, scale, width, height);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode the overlay as PNG.");
        return png.ToArray();
    }

    private static void DrawDarkening(SKCanvas canvas, int darken, int width, int height)
    {
        var percent = Math.Clamp(darken, 0, MaxDarken);
        if (percent == 0)
        {
            return;
        }

        // Whole-number arithmetic rounds half up without floating point noise: 15 percent is round(38.25) = 38.
        var alpha = (byte)((percent * 255 + 50) / 100);
        using var paint = new SKPaint { Color = new SKColor(0, 0, 0, alpha) };
        canvas.DrawRect(0, 0, width, height, paint);
    }

    private static void DrawFrame(SKCanvas canvas, float scale, int width, int height)
    {
        var frame = new SKRect(FrameLeft * scale, FrameTop * scale, width - FrameLeft * scale, height - FrameTop * scale);

        // The drop shadow filter draws the shadow under the stroke itself, like a canvas shadow does.
        using var shadow = SKImageFilter.CreateDropShadow(0, 0, FrameShadowSigma * scale, FrameShadowSigma * scale, FrameShadowColor);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = FrameStroke * scale,
            Color = SKColors.White,
            ImageFilter = shadow,
        };
        canvas.DrawRoundRect(frame, FrameRadius * scale, FrameRadius * scale, paint);
    }

    private static void DrawTitle(SKCanvas canvas, CoverStyle style, string libraryName, float scale, int width, int height)
    {
        var text = (string.IsNullOrWhiteSpace(style.Title) ? libraryName : style.Title).Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return;
        }

        // Gray antialiasing, never LCD: the overlay is laid over moving pictures, so colored fringes would show.
        // Linear metrics and no hinting keep the layout exactly proportional to the size.
        using var font = new SKFont(FontCatalog.ForText(style.Font, text), style.TextSize * scale)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
            LinearMetrics = true,
            Hinting = SKFontHinting.None,
        };

        // The text is shaped, as the page's browser laid it out, so pairs are kerned and right-to-left scripts come out
        // joined and in visual order. A title wider than the canvas allows shrinks to the largest whole pixel size that
        // fits, by its shaped width.
        var shaped = FontCatalog.Shape(font, text);
        var maxWidth = width - TitleMargin * scale;
        if (shaped.Width > maxWidth)
        {
            font.Size = MathF.Floor(font.Size * maxWidth / shaped.Width);
            shaped = FontCatalog.Shape(font, text);
        }

        var middle = height / 2f + font.Size * TitleDrop;

        // The line comes from the chosen family's metrics even when a fallback font draws the glyphs, as it did on the
        // page, so the title stays where the chosen family puts it whatever script it is in.
        var baseline = middle + font.Size * FontCatalog.BaselineBelowMiddle(FontCatalog.Get(style.Font));

        using var shadow = style.Shadow
            ? SKImageFilter.CreateDropShadow(0, TitleShadowOffsetY * scale, TitleShadowSigma * scale, TitleShadowSigma * scale, TitleShadowColor)
            : null;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, ImageFilter = shadow };
        DrawGlyphs(canvas, shaped, font, (width - shaped.Width) / 2f, baseline, paint);
    }

    // Draws shaped glyphs as one run, the line starting at x on the given baseline.
    private static void DrawGlyphs(SKCanvas canvas, SKShaper.Result shaped, SKFont font, float x, float baseline, SKPaint paint)
    {
        if (shaped.Codepoints.Length == 0)
        {
            return;
        }

        var glyphs = new ushort[shaped.Codepoints.Length];
        var positions = new SKPoint[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = (ushort)shaped.Codepoints[i];
            positions[i] = new SKPoint(x + shaped.Points[i].X, baseline + shaped.Points[i].Y);
        }

        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphs, font, positions);
        using var blob = builder.Build();
        canvas.DrawText(blob, 0, 0, paint);
    }
}
