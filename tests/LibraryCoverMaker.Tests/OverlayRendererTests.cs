using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using SkiaSharp;
using Xunit;

namespace LibraryCoverMaker.Tests;

public sealed class FontCatalogTests
{
    public static IEnumerable<object[]> Families => FontCatalog.Families.Select(family => new object[] { family });

    [Fact]
    public void Families_lists_the_five_bundled_families() =>
        Assert.Equal(new[] { "Bodoni Moda", "Cover Serif", "Cinzel", "Bebas Neue", "Oswald" }, FontCatalog.Families);

    [Theory]
    [InlineData("Bodoni Moda", 700)]
    [InlineData("Cover Serif", 800)]
    [InlineData("Cinzel", 800)]
    [InlineData("Bebas Neue", 400)]
    [InlineData("Oswald", 700)]
    public void Each_family_is_bundled_at_the_weight_the_page_used(string family, int weight) =>
        Assert.Equal(weight, FontCatalog.Get(family).FontWeight);

    [Fact]
    public void Get_loads_each_family_once() =>
        Assert.Same(FontCatalog.Get("Cinzel"), FontCatalog.Get("Cinzel"));

    [Fact]
    public void Get_ignores_the_case_of_the_family_name() =>
        Assert.Same(FontCatalog.Get("Cinzel"), FontCatalog.Get("cINZEL"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Comic Sans")]
    [InlineData("Playfair Display")] // the name Cover Serif was made from is reserved, so it is not a family here
    public void Get_falls_back_to_Bodoni_Moda_for_an_unknown_family(string? family) =>
        Assert.Same(FontCatalog.Get("Bodoni Moda"), FontCatalog.Get(family));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Title_with_a_missing_family_name_keeps_the_default_font(string? family) =>
        Assert.Same(FontCatalog.Get("Bodoni Moda"), FontCatalog.ForText(family, "FILMES"));

    [Fact]
    public void Title_without_glyphs_uses_fallback_font()
    {
        // Bebas Neue has no Cyrillic, so the glyphs have to come from a system font (through Fontconfig on Linux).
        Assert.False(FontCatalog.Get("Bebas Neue").ContainsGlyphs("ФИЛЬМЫ"));
        Assert.True(FontCatalog.ForText("Bebas Neue", "ФИЛЬМЫ").ContainsGlyphs("ФИЛЬМЫ"));
    }

    [Fact]
    public void Title_with_glyphs_keeps_the_chosen_font()
    {
        Assert.Same(FontCatalog.Get("Bebas Neue"), FontCatalog.ForText("Bebas Neue", "FILMES 2026"));
        Assert.Same(FontCatalog.Get("Bebas Neue"), FontCatalog.ForText("Bebas Neue", string.Empty));
    }

    [Fact]
    public void Title_no_font_can_draw_keeps_the_chosen_font()
    {
        // U+10FFFF is a noncharacter: no font has a glyph for it, so there is nothing to fall back to.
        Assert.Same(FontCatalog.Get("Cinzel"), FontCatalog.ForText("Cinzel", "FILMES \U0010FFFF"));
    }

    [Fact]
    public void Cover_Serif_carries_no_reserved_font_name()
    {
        // The font behind Cover Serif is a modified instance of Playfair Display, and that license reserves the name.
        Assert.Equal("Cover Serif", FontCatalog.Get("Cover Serif").FamilyName);
        Assert.DoesNotContain("Playfair Display", FontCatalog.Families);
    }

    // The license text that ships with each family's font. Cover Serif is made from Playfair Display.
    private static readonly Dictionary<string, string> LicenseFiles = new()
    {
        ["Bodoni Moda"] = "OFL-BodoniModa.txt",
        ["Cover Serif"] = "OFL-PlayfairDisplay.txt",
        ["Cinzel"] = "OFL-Cinzel.txt",
        ["Bebas Neue"] = "OFL-BebasNeue.txt",
        ["Oswald"] = "OFL-Oswald.txt",
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Each_font_ships_with_its_license(string family)
    {
        Assert.True(LicenseFiles.TryGetValue(family, out var file), $"No license file is listed for {family}.");
        using var stream = typeof(FontCatalog).Assembly.GetManifestResourceStream("Fonts/" + file);
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        Assert.StartsWith("Copyright", text);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", text);
    }

    [Theory]
    [InlineData("Bodoni Moda", 0.2377f, 0.2378f)]
    [InlineData("Cover Serif", 0.3117f, 0.3118f)]
    [InlineData("Cinzel", 0.2240f, 0.2240f)]
    [InlineData("Bebas Neue", 0.2500f, 0.2500f)]
    [InlineData("Oswald", 0.3050f, 0.3050f)]
    public void Baseline_ratio_of_a_bundled_family_is_pinned(string family, float pinned, float measuredInChrome)
    {
        var ratio = FontCatalog.BaselineBelowMiddle(FontCatalog.Get(family));

        // The fixed value itself, not whatever ascent and descent the Skia backend reports for the font...
        Assert.Equal(pinned, ratio);

        // ...and it is what Chrome 155 measured for the page's middle baseline, in ems (measureText at 800 px).
        Assert.Equal(measuredInChrome, ratio, 0.0002f);
    }

    [Fact]
    public void Baseline_ratio_of_a_system_font_is_computed_from_its_metrics()
    {
        // No font of the catalog has Cyrillic, so a system font draws it. Nothing is pinned for that typeface: the
        // ratio follows from the ascent (negative in Skia) and the descent that Skia reports for it.
        var system = FontCatalog.ForText("Bodoni Moda", "ФИЛЬМЫ");
        Assert.DoesNotContain(FontCatalog.Families, name => ReferenceEquals(FontCatalog.Get(name), system));

        using var font = new SKFont(system, 100) { LinearMetrics = true, Hinting = SKFontHinting.None };
        var metrics = font.Metrics;
        var expected = (-metrics.Ascent - metrics.Descent) / (2 * (-metrics.Ascent + metrics.Descent));

        Assert.Equal(expected, FontCatalog.BaselineBelowMiddle(system), 0.0001f);
    }

    [Theory]
    [InlineData("Bodoni Moda")] // weight 700
    [InlineData("Bebas Neue")] // weight 400
    public void Fallback_font_is_asked_for_in_the_weight_of_the_family(string family)
    {
        // The typeface that the machine's font manager gives for a Cyrillic letter in the family's own font style,
        // asked for directly. ForText has to return a typeface with the same family name and the same weight, which
        // is all that is compared below. On a machine whose Cyrillic font has a single weight every request gets the
        // same typeface, and a wrong weight request would go unnoticed.
        const string text = "ФИЛЬМЫ";
        using var style = FontCatalog.Get(family).FontStyle;

        // The first letter of the text, U+0424, is the character ForText asks the font manager about: neither Bodoni
        // Moda nor Bebas Neue has any Cyrillic.
        var expected = SKFontManager.Default.MatchCharacter(null, style, null, text.EnumerateRunes().First().Value);
        Assert.NotNull(expected);

        // ForText makes the same request with a font style of its own. A font style is a native object that its wrapper
        // frees when the wrapper is collected, and a native object held only as a temporary can be freed while a call
        // still uses it, so ForText keeps the style in a using. A reviewer once reported wrong weights under a forced
        // storm of collections, before the using was there. That was not reproduced here, so the collections forced
        // between the calls below try to provoke it; they do not prove that it cannot happen.
        for (var call = 0; call < 300; call++)
        {
            if (call % 10 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            var actual = FontCatalog.ForText(family, text);
            Assert.Equal((expected.FamilyName, expected.FontWeight), (actual.FamilyName, actual.FontWeight));
        }
    }

    private static SKFont ShapingFont(SKTypeface typeface) =>
        new(typeface, 80) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };

    [Fact]
    public void Kerned_pair_is_narrower_than_unshaped()
    {
        using var font = ShapingFont(FontCatalog.Get("Bodoni Moda"));

        // The font's kern feature pulls W and A together by about 0.18 em (read from its GPOS table), while Skia's
        // own layout adds the two advances unchanged.
        var unshaped = font.MeasureText("WA");
        var shaped = FontCatalog.Shape(font, "WA").Width;
        Assert.True(unshaped - shaped > 10, $"Shaped {shaped} px, unshaped {unshaped} px.");

        // A pair the font does not kern is as wide as the sum of its advances, so the difference above is kerning.
        Assert.Equal(font.MeasureText("HH"), FontCatalog.Shape(font, "HH").Width, 0.5);
    }

    [Theory]
    [InlineData("סרטים", false)] // Hebrew: five letters that do not join
    [InlineData("أفلام", true)] // Arabic: the letters join, and lam with alef makes a ligature
    public void Right_to_left_title_is_shaped(string text, bool joins)
    {
        // Whichever installed font has the letters draws them; DejaVu Sans does in the stock Jellyfin image.
        var typeface = FontCatalog.ForText("Bodoni Moda", text);
        Assert.True(typeface.ContainsGlyphs(text), "No installed font has these letters.");
        using var font = ShapingFont(typeface);

        // HarfBuzz reads the text as right to left...
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        Assert.Equal(HarfBuzzSharp.Direction.RightToLeft, buffer.Direction);

        // ...so the glyphs come out in visual order, left to right: the first glyph belongs to the last letter, the
        // clusters (where each glyph's letters start in the text) fall, and the positions rise.
        var shaped = FontCatalog.Shape(font, text);
        Assert.True(shaped.Clusters.Zip(shaped.Clusters.Skip(1), (a, b) => a > b).All(fell => fell));
        Assert.True(shaped.Points.Zip(shaped.Points.Skip(1), (a, b) => a.X < b.X).All(rose => rose));

        var nominal = text.Select(letter => (uint)typeface.GetGlyph(letter)).ToArray();
        if (joins)
        {
            // Joined forms and the ligature: fewer glyphs than letters, and glyphs the character map does not give.
            Assert.True(shaped.Codepoints.Length < text.Length);
            Assert.NotEmpty(shaped.Codepoints.Except(nominal));
        }
        else
        {
            Assert.Equal(Enumerable.Reverse(nominal), shaped.Codepoints);
        }
    }
}

public sealed class OverlayRendererTests
{
    private const int Width = 640;
    private const int Height = 360;

    private static readonly OverlayRenderer Renderer = new();

    public static IEnumerable<object[]> Families => FontCatalog.Families.Select(family => new object[] { family });

    // Draws the title and nothing else, so every pixel that is not transparent belongs to the text.
    private static CoverStyle TextOnly(string title, string font = "Bodoni Moda", int textSize = 120, bool shadow = false) =>
        new() { Title = title, Font = font, TextSize = textSize, Darken = 0, Frame = false, Shadow = shadow };

    private static SKBitmap Render(CoverStyle style, string libraryName = "Library", int width = Width, int height = Height) =>
        SKBitmap.Decode(Renderer.RenderPng(style, libraryName, width, height))
            ?? throw new InvalidOperationException("RenderPng did not return an image.");

    private static bool IsOpaqueWhite(SKColor color) =>
        color.Alpha == 255 && color.Red >= 250 && color.Green >= 250 && color.Blue >= 250;

    // The box around every pixel that is not fully transparent.
    private static SKRectI InkBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        var pixels = bitmap.Pixels;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (pixels[y * bitmap.Width + x].Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    // How different the alpha of an area of one overlay is from the alpha of an area of another that starts at the
    // given column, row for row: the sum of the differences.
    private static long AlphaDifference(SKBitmap overlay, SKRectI area, SKBitmap other, int otherLeft)
    {
        long sum = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = 0; x < area.Width; x++)
            {
                var a = overlay.GetPixel(area.Left + x, y).Alpha;
                var b = otherLeft + x < other.Width ? other.GetPixel(otherLeft + x, y).Alpha : 0;
                sum += Math.Abs(a - b);
            }
        }

        return sum;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 38)] // round(0.15 * 255)
    [InlineData(70, 179)]
    [InlineData(-5, 0)]
    [InlineData(71, 179)] // the documented range ends at 70 percent, and a value beyond it is clamped to it
    [InlineData(100, 179)]
    [InlineData(1000, 179)]
    public void Darkening_alpha_matches_style(int darken, int expectedAlpha)
    {
        using var overlay = Render(new CoverStyle { Darken = darken });

        // Pixel (5, 5) lies outside the frame, so only the darkening layer reaches it.
        var pixel = overlay.GetPixel(5, 5);
        Assert.Equal((expectedAlpha, 0, 0, 0), (pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue));
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(960, 540)]
    public void Frame_is_white_on_the_stroke_and_absent_when_off(int width, int height)
    {
        // The stroke is centered on the frame's left edge, 24 design pixels in.
        var x = 24 * width / 960;
        var y = height / 2;

        using var withFrame = Render(new CoverStyle(), "A", width, height);
        using var withoutFrame = Render(new CoverStyle { Frame = false }, "A", width, height);

        Assert.True(IsOpaqueWhite(withFrame.GetPixel(x, y)));

        // Without the frame only the darkening is left there.
        var bare = withoutFrame.GetPixel(x, y);
        Assert.Equal((38, 0), (bare.Alpha, bare.Red));
    }

    [Fact]
    public void Frame_is_a_stroke_and_not_a_filled_rectangle()
    {
        using var overlay = Render(new CoverStyle(), "A");

        // Just inside and just outside the stroke, on the middle row.
        Assert.False(IsOpaqueWhite(overlay.GetPixel(24 * Width / 960 + 8, Height / 2)));
        Assert.False(IsOpaqueWhite(overlay.GetPixel(24 * Width / 960 - 8, Height / 2)));
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(960, 540)]
    public void Frame_corners_are_rounded(int width, int height)
    {
        var scale = width / 960.0;
        var radius = 22 * scale;

        // The corner arc is centered one radius in from the frame's top left corner (24, 22) design pixels.
        var arcX = (int)(24 * scale + radius - radius * Math.Cos(Math.PI / 4));
        var arcY = (int)(22 * scale + radius - radius * Math.Sin(Math.PI / 4));

        using var overlay = Render(new CoverStyle(), "A", width, height);

        Assert.True(IsOpaqueWhite(overlay.GetPixel(arcX, arcY)));

        // A frame with square corners would be white here, where the rounded one leaves a gap.
        Assert.False(IsOpaqueWhite(overlay.GetPixel(24 * width / 960, 22 * width / 960)));
    }

    [Fact]
    public void Frame_casts_a_soft_shadow()
    {
        // No darkening and no text, so the only thing besides the stroke is its shadow.
        var style = new CoverStyle { Darken = 0 };
        var x = 24 * Width / 960 - 4;

        using var withFrame = Render(style, string.Empty);
        using var withoutFrame = Render(new CoverStyle { Darken = 0, Frame = false }, string.Empty);

        var shadow = withFrame.GetPixel(x, Height / 2);
        Assert.True(shadow.Alpha > 0 && shadow.Alpha < 128);
        Assert.Equal(0, shadow.Red);
        Assert.Equal(0, withoutFrame.GetPixel(x, Height / 2).Alpha);
    }

    [Fact]
    public void Title_is_drawn_white_near_the_center()
    {
        using var overlay = Render(new CoverStyle { Title = "MOVIES" });

        var found = false;
        for (var y = Height / 2 - 20; y <= Height / 2 + 20 && !found; y++)
        {
            for (var x = Width / 2 - 20; x <= Width / 2 + 20 && !found; x++)
            {
                found = IsOpaqueWhite(overlay.GetPixel(x, y));
            }
        }

        Assert.True(found);
    }

    [Fact]
    public void Empty_title_uses_library_name_in_uppercase()
    {
        var named = Renderer.RenderPng(new CoverStyle { Title = string.Empty }, "Movies", Width, Height);
        var blank = Renderer.RenderPng(new CoverStyle { Title = "   " }, "Movies", Width, Height);
        var typed = Renderer.RenderPng(new CoverStyle { Title = "MOVIES" }, "Ignored", Width, Height);
        var nothing = Renderer.RenderPng(new CoverStyle { Title = string.Empty }, string.Empty, Width, Height);

        Assert.Equal(typed, named);
        Assert.Equal(typed, blank);

        // Guards the comparison above: a renderer that drew no text at all would pass it.
        Assert.NotEqual(nothing, named);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Missing_font_name_draws_the_default_font(string? font)
    {
        var expected = Renderer.RenderPng(new CoverStyle { Font = "Bodoni Moda" }, "Movies", Width, Height);
        var actual = Renderer.RenderPng(new CoverStyle { Font = font! }, "Movies", Width, Height);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Title_is_uppercased_and_trimmed()
    {
        var typed = Renderer.RenderPng(new CoverStyle { Title = "SÉRIES" }, "x", Width, Height);
        var sloppy = Renderer.RenderPng(new CoverStyle { Title = "  Séries " }, "x", Width, Height);

        Assert.Equal(typed, sloppy);
    }

    [Fact]
    public void Long_title_shrinks_to_fit()
    {
        var maxWidth = Width - 120 * Width / 960;
        using var overlay = Render(TextOnly("THE COMPLETE COLLECTION OF EVERY MOVIE EVER MADE, IN ORDER, TWICE"));

        var ink = InkBounds(overlay);

        Assert.True(ink.Width < maxWidth, $"The title is {ink.Width} px wide, the limit is {maxWidth} px.");

        // It shrinks to fit, not to nothing: the text still fills most of the width it is allowed.
        Assert.True(ink.Width > maxWidth * 0.9, $"The title is only {ink.Width} px wide, the limit is {maxWidth} px.");
    }

    [Fact]
    public void Long_title_in_a_fallback_script_shrinks_to_fit()
    {
        var maxWidth = Width - 120 * Width / 960;
        using var overlay = Render(TextOnly("ПОЛНАЯ КОЛЛЕКЦИЯ ВСЕХ ФИЛЬМОВ ВСЕХ ВРЕМЕН И НАРОДОВ, ПО ПОРЯДКУ"));

        var ink = InkBounds(overlay);

        Assert.True(ink.Width < maxWidth, $"The title is {ink.Width} px wide, the limit is {maxWidth} px.");
        Assert.True(ink.Width > maxWidth * 0.9, $"The title is only {ink.Width} px wide, the limit is {maxWidth} px.");
    }

    [Theory]
    [InlineData("Bebas Neue")]
    [InlineData("Bodoni Moda")]
    public void Title_without_glyphs_is_drawn_as_letters_not_empty_boxes(string family)
    {
        // Neither font has Cyrillic. If the missing glyphs were drawn as the font's empty box, six boxes would look
        // the same in either order; real letters do not.
        var forward = Renderer.RenderPng(TextOnly("ФИЛЬМЫ", family), "x", Width, Height);
        var backward = Renderer.RenderPng(TextOnly("ЫМЬЛИФ", family), "x", Width, Height);

        Assert.NotEqual(forward, backward);
    }

    [Theory]
    [InlineData(640, 120)]
    [InlineData(960, 120)]
    [InlineData(640, 60)]
    public void Text_size_scales_with_the_output_width(int width, int textSize)
    {
        var height = width * 9 / 16;
        using var overlay = Render(TextOnly("H", textSize: textSize), "x", width, height);

        // The cap height of the font at the pixel size the style asks for.
        using var font = new SKFont(FontCatalog.Get("Bodoni Moda"), textSize * width / 960f);
        font.MeasureText("H", out var bounds);

        Assert.InRange((double)InkBounds(overlay).Height, bounds.Height - 2.5, bounds.Height + 2.5);
    }

    [Theory]
    [InlineData("Bodoni Moda", 0.2378)]
    [InlineData("Cover Serif", 0.3118)]
    [InlineData("Cinzel", 0.2240)]
    [InlineData("Bebas Neue", 0.2500)]
    [InlineData("Oswald", 0.3050)]
    public void Title_sits_where_the_page_drew_it(string family, double baselineBelowMiddle)
    {
        // The page drew with textBaseline "middle" at H / 2 + size * 0.04. The numbers are how far below that line
        // Chrome 155 put each font's baseline, in ems: measureText().alphabeticBaseline at 800 px.
        var size = 120.0 * Width / 960;
        var baseline = Height / 2.0 + size * 0.04 + size * baselineBelowMiddle;

        using var overlay = Render(TextOnly("H", family));

        // A capital H stands on the baseline and nothing of it hangs below, so its ink ends there.
        Assert.InRange((double)InkBounds(overlay).Bottom, baseline - 1, baseline + 2);
    }

    [Theory]
    [InlineData("Bodoni Moda", "WAR", 221, 419)]
    [InlineData("Bodoni Moda", "AVATAR", 154, 486)]
    [InlineData("Cinzel", "AVATAR", 161, 481)]
    [InlineData("Cover Serif", "SHOWS", 172, 469)]
    [InlineData("Oswald", "DOCUMENTARIES", 50, 592)]
    public void Title_is_kerned_like_the_page(string family, string text, int left, int right)
    {
        // Where Chrome 155 drew the ink of these titles at 80 px on a 640 by 360 canvas, kerning on. With kerning off
        // the same page spans x 214 to 426 for WAR and x 135 to 505 for AVATAR in Bodoni Moda.
        using var overlay = Render(TextOnly(text, family));

        var ink = InkBounds(overlay);
        Assert.InRange(ink.Left, left - 1, left + 1);
        Assert.InRange(ink.Right, right - 1, right + 1);
    }

    [Fact]
    public void Kerned_title_shrinks_by_its_shaped_width()
    {
        // On a 960 by 540 canvas the page shrank AVATAR AVATAR AVATAR to 64 px, because kerning makes the line 1565 px
        // wide at 120 px and not 1737, and drew the ink over x 63 to 897 and y 239 to 289 (Chrome 155). By its unkerned
        // width the title would have shrunk to 58 px and spanned x 61 to 899, y 242 to 288.
        using var overlay = Render(TextOnly("AVATAR AVATAR AVATAR"), "x", 960, 540);

        var ink = InkBounds(overlay);
        Assert.InRange(ink.Left, 62, 64);
        Assert.InRange(ink.Right, 896, 898);
        Assert.InRange(ink.Top, 238, 240);
        Assert.InRange(ink.Bottom, 288, 290);
    }

    [Fact]
    public void Marks_sit_where_the_shaper_puts_them()
    {
        // A fatha over a tall Arabic letter is lifted above the letter, and a kasra under a letter is lowered below it.
        // Each mark is a glyph of its own that the shaper moves, up or down, with a vertical offset.
        var tall = InkBounds(Render(TextOnly("ط")));
        var withFatha = InkBounds(Render(TextOnly("طَ")));
        var low = InkBounds(Render(TextOnly("ب")));
        var withKasra = InkBounds(Render(TextOnly("بِ")));

        Assert.True(withFatha.Top < tall.Top - 8, $"Top {withFatha.Top} with the mark, {tall.Top} without.");
        Assert.True(withKasra.Bottom > low.Bottom + 8, $"Bottom {withKasra.Bottom} with the mark, {low.Bottom} without.");
    }

    [Fact]
    public void Right_to_left_title_is_drawn_with_the_shaped_layout()
    {
        const string arabic = "أفلام";
        var typeface = FontCatalog.ForText("Bodoni Moda", arabic);
        using var font = new SKFont(typeface, 120 * Width / 960f) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
        var shaped = FontCatalog.Shape(font, arabic).Width;
        var unshaped = font.MeasureText(arabic);

        // Joined letters and the ligature make the shaped line a very different width from five separate letters.
        Assert.True(Math.Abs(shaped - unshaped) > 30, $"Shaped {shaped} px, unshaped {unshaped} px.");

        // The ink is the shaped line less its side bearings, so it is far closer to the shaped width than to the other.
        using var overlay = Render(TextOnly(arabic));
        double ink = InkBounds(overlay).Width;
        Assert.True(Math.Abs(ink - shaped) < Math.Abs(ink - unshaped), $"Ink {ink} px, shaped {shaped} px, unshaped {unshaped} px.");
    }

    [Fact]
    public void Right_to_left_title_is_drawn_in_visual_order()
    {
        // Alef and Bet written as one word, Alef first. Read right to left, Alef stands on the right.
        using var pair = Render(TextOnly("אב"));
        using var alef = Render(TextOnly("א"));
        using var bet = Render(TextOnly("ב"));

        var box = InkBounds(pair);
        var alefInk = InkBounds(alef);
        var betInk = InkBounds(bet);

        // The left end of the pair looks like Bet on its own and the right end like Alef on its own, and not the
        // other way round.
        var leftEnd = new SKRectI(box.Left, box.Top, box.Left + betInk.Width, box.Bottom);
        var rightEnd = new SKRectI(box.Right - alefInk.Width, box.Top, box.Right, box.Bottom);
        Assert.True(AlphaDifference(pair, leftEnd, bet, betInk.Left) * 4 < AlphaDifference(pair, leftEnd, alef, alefInk.Left));
        Assert.True(AlphaDifference(pair, rightEnd, alef, alefInk.Left) * 4 < AlphaDifference(pair, rightEnd, bet, betInk.Left));
    }

    [Fact]
    public void Shrunk_title_sits_where_the_page_drew_it()
    {
        // On a 960 by 540 canvas the page shrank 44 capital Hs to 23 px and drew their ink over x 73 to 888 and
        // y 258 to 276 (Chrome 155). The drop below the center follows the shrunk size, not the configured one.
        using var overlay = Render(TextOnly(new string('H', 44)), "x", 960, 540);

        var ink = InkBounds(overlay);
        Assert.InRange(ink.Left, 72, 74);
        Assert.InRange(ink.Right, 887, 889);
        Assert.InRange(ink.Top, 257, 259);
        Assert.InRange(ink.Bottom, 275, 277);
    }

    [Fact]
    public void Title_in_a_fallback_script_keeps_the_line_of_its_family()
    {
        // Cinzel has no Cyrillic, so a system font draws the letter. The page still put the baseline where Cinzel's
        // own metrics put it (0.2240 em below the middle line), not where the fallback font's would be.
        var size = 120.0 * Width / 960;
        var baseline = Height / 2.0 + size * 0.04 + size * 0.2240;

        using var overlay = Render(TextOnly("П", "Cinzel"));

        // The Cyrillic Pe is flat at the bottom, so its ink ends on the baseline.
        Assert.InRange((double)InkBounds(overlay).Bottom, baseline - 1, baseline + 2);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Title_is_centered_for_every_family(string family)
    {
        using var overlay = Render(TextOnly("FILMES", family));

        var ink = InkBounds(overlay);
        var pixelSize = 120 * Width / 960;

        // The ink box sits near the canvas center. Side bearings move it sideways by a pixel or two. Each font's
        // capitals sit a little higher or lower on the page's middle line, by up to about a tenth of the text size.
        Assert.InRange(ink.MidX, Width / 2 - 3, Width / 2 + 3);
        Assert.InRange(ink.MidY, Height / 2 - pixelSize / 8, Height / 2 + pixelSize / 8);
    }

    [Fact]
    public void Each_family_draws_its_own_glyphs()
    {
        var renders = FontCatalog.Families
            .Select(family => Convert.ToBase64String(Renderer.RenderPng(TextOnly("FILMES", family), "x", Width, Height)))
            .ToList();

        Assert.NotEmpty(renders);
        Assert.Equal(renders.Count, renders.Distinct().Count());
    }

    [Fact]
    public void Text_shadow_is_drawn_only_when_enabled()
    {
        using var flat = Render(TextOnly("MOVIES"));
        using var shadowed = Render(TextOnly("MOVIES", shadow: true));

        // Without the shadow every visible pixel is white; with it, dark pixels surround the letters.
        Assert.All(flat.Pixels.Where(p => p.Alpha > 0), p => Assert.True(p.Red >= 240));
        Assert.Contains(shadowed.Pixels, p => p.Alpha > 0 && p.Red < 50);
    }

    [Fact]
    public void Overlay_without_darkening_frame_or_text_is_fully_transparent()
    {
        using var overlay = Render(new CoverStyle { Darken = 0, Frame = false }, string.Empty);

        Assert.Equal(SKRectI.Empty, InkBounds(overlay));
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(960, 540)]
    public void Overlay_is_a_png_of_the_requested_size(int width, int height)
    {
        var png = Renderer.RenderPng(new CoverStyle(), "Movies", width, height);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());

        using var overlay = SKBitmap.Decode(png);
        Assert.Equal((width, height), (overlay.Width, overlay.Height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Non_positive_text_size_draws_no_title(int textSize)
    {
        using var overlay = Render(TextOnly("MOVIES", textSize: textSize));

        Assert.Equal(SKRectI.Empty, InkBounds(overlay));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Renderer.RenderPng(null!, "Movies", Width, Height));
        Assert.Throws<ArgumentNullException>(() => Renderer.RenderPng(new CoverStyle(), null!, Width, Height));
        Assert.Throws<ArgumentOutOfRangeException>(() => Renderer.RenderPng(new CoverStyle(), "Movies", 0, Height));
        Assert.Throws<ArgumentOutOfRangeException>(() => Renderer.RenderPng(new CoverStyle(), "Movies", Width, -1));
    }
}
