using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>
/// Draws the part of a cover that stays still while the pictures move: a transparent PNG with the darkening, the
/// rounded white frame and the title. The render lays it over every frame of the animation. The title is shaped with
/// HarfBuzz, so it is kerned and laid out like the page's browser or Jellyfin laid it out.
/// </summary>
/// <remarks>
/// <para>
/// The geometry is the cover page's design, a canvas of 960 by 540 pixels, scaled by <c>width / 960</c>. The page's
/// shadows are canvas shadows, where a blur of B is a Gaussian with a standard deviation of B / 2; the constants below
/// carry that standard deviation.
/// </para>
/// <para>
/// A title in the Jellyfin family is written the way Jellyfin 12.2 writes a library's name on the library images it
/// draws itself (<c>StripCollageBuilder.BuildThumbCollageBitmap</c>, src/Jellyfin.Drawing.Skia, tag v12.2, on images
/// of 960 by 540): in the name's own case, white, measured without kerning by <c>SKFont.MeasureText</c> with SKFont's
/// default settings, shrunk to 90 percent of the width when it is wider than 95 percent, centered by that measured
/// width, on a baseline half the font's x-height below the middle, and shaped from the line's left end, or its right
/// end for a right-to-left script. Jellyfin draws it at 112 pixels, the default text size here. What Jellyfin adds
/// besides the name is a black layer at alpha 0x78 (47 percent) over the whole image and no shadow; here those are the
/// style's darkening and shadow. One difference is left: where the name has letters its typeface lacks, Jellyfin draws
/// each run of them in a font of its own, and this renderer draws the whole name in one (see
/// <see cref="FontCatalog.ForText"/>).
/// </para>
/// </remarks>
public sealed partial class OverlayRenderer
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

    // Jellyfin's rule for a long name: wider than this fraction of the image, it shrinks to fill the second.
    private const double JellyfinMaxWidth = 0.95;
    private const float JellyfinShrunkWidth = 0.9f;

    // The documented range of CoverStyle.Darken, in percent, is 0 to 70. A value beyond it is clamped to it.
    private const int MaxDarken = 70;

    private static readonly SKColor FrameShadowColor = new(0, 0, 0, 89); // rgba(0, 0, 0, .35)
    private static readonly SKColor TitleShadowColor = new(0, 0, 0, 153); // rgba(0, 0, 0, .6)

    /// <summary>
    /// Draws the overlay for a cover. The title is the style's, or the library's name when the style has none, trimmed
    /// and shrunk to fit; in uppercase, except in the Jellyfin family, which keeps its case as Jellyfin does. A
    /// darkening outside 0 to 70 percent is clamped, and a text size of 0 or less draws no title.
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
        var frame = new SKRect(
            FrameLeft * scale,
            FrameTop * scale,
            width - FrameLeft * scale,
            height - FrameTop * scale);

        // The drop shadow filter draws the shadow under the stroke itself, like a canvas shadow does.
        using var shadow = SKImageFilter.CreateDropShadow(
            0,
            0,
            FrameShadowSigma * scale,
            FrameShadowSigma * scale,
            FrameShadowColor);
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

    private static void DrawTitle(
        SKCanvas canvas,
        CoverStyle style,
        string libraryName,
        float scale,
        int width,
        int height)
    {
        var jellyfin = FontCatalog.IsJellyfin(style.Font);
        var text = (string.IsNullOrWhiteSpace(style.Title) ? libraryName : style.Title).Trim();
        if (!jellyfin)
        {
            text = text.ToUpperInvariant();
        }

        if (text.Length == 0)
        {
            return;
        }

        using var font = jellyfin
            ? new SKFont(FontCatalog.ForText(style.Font, text), style.TextSize * scale)
            : PageFont(FontCatalog.ForText(style.Font, text), style.TextSize * scale);
        var (shaped, x, baseline) = jellyfin
            ? LayOutLikeJellyfin(font, text, width, height)
            : LayOutLikeThePage(font, style.Font, text, scale, width, height);

        using var shadow = style.Shadow
            ? SKImageFilter.CreateDropShadow(
                0,
                TitleShadowOffsetY * scale,
                TitleShadowSigma * scale,
                TitleShadowSigma * scale,
                TitleShadowColor)
            : null;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, ImageFilter = shadow };
        DrawGlyphs(canvas, shaped, font, x, baseline, paint);
    }

    // Gray antialiasing, never LCD: the overlay is laid over moving pictures, so colored fringes would show. Linear
    // metrics and no hinting keep the layout exactly proportional to the size.
    private static SKFont PageFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = true,
        LinearMetrics = true,
        Hinting = SKFontHinting.None,
    };

    // The page's layout. The text is shaped, as the page's browser laid it out, so pairs are kerned and right-to-left
    // scripts come out joined and in visual order. A title wider than the canvas allows shrinks to the largest whole
    // pixel size that fits, by its shaped width, and the shaped line is centered.
    private static (SKShaper.Result Shaped, float X, float Baseline) LayOutLikeThePage(
        SKFont font,
        string? family,
        string text,
        float scale,
        int width,
        int height)
    {
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
        var baseline = middle + font.Size * FontCatalog.BaselineBelowMiddle(FontCatalog.Get(family));
        return (shaped, (width - shaped.Width) / 2f, baseline);
    }

    // Jellyfin's layout, step for step (see the class remarks). The font has SKFont's default settings, as Jellyfin's
    // has: hinted, with glyphs on whole pixels. The measure is SKFont.MeasureText, which adds advances without
    // kerning, and Jellyfin measures again after a shrink and shrinks a second time if the line is still too wide.
    private static (SKShaper.Result Shaped, float X, float Baseline) LayOutLikeJellyfin(
        SKFont font,
        string text,
        int width,
        int height)
    {
        var measured = font.MeasureText(text);
        for (var pass = 0; pass < 2 && measured > width * JellyfinMaxWidth; pass++)
        {
            font.Size = JellyfinShrunkWidth * width * font.Size / measured;
            measured = font.MeasureText(text);
        }

        // Jellyfin takes the x-height from its own typeface even when a fallback draws the letters, so the line stays
        // where its typeface puts it whatever script the name is in.
        using var lineFont = new SKFont(FontCatalog.Get(FontCatalog.JellyfinFamily), font.Size);
        var baseline = height / 2f + lineFont.Metrics.XHeight / 2;

        var shaped = FontCatalog.Shape(font, text);
        var padding = (width - measured) / 2;
        var x = JellyfinRightToLeft().IsMatch(text) ? width - padding - shaped.Width : padding;
        return (shaped, x, baseline);
    }

    // Jellyfin's own test for a name it aligns to the right, Armenian included.
    [GeneratedRegex(@"\p{IsArabic}|\p{IsArmenian}|\p{IsHebrew}|\p{IsSyriac}|\p{IsThaana}")]
    private static partial Regex JellyfinRightToLeft();

    // Draws shaped glyphs as one run, the line starting at x on the given baseline.
    private static void DrawGlyphs(
        SKCanvas canvas,
        SKShaper.Result shaped,
        SKFont font,
        float x,
        float baseline,
        SKPaint paint)
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
