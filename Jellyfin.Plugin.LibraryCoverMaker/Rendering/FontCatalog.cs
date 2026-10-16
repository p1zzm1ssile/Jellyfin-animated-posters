using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>
/// The fonts bundled with the plugin, the choice of a font for a piece of text (the family a cover asks for when it
/// has every glyph, otherwise a system font that has them), and the shaping of that text with HarfBuzz.
/// </summary>
/// <remarks>
/// <para>
/// Each font is embedded in the plugin assembly together with its SIL Open Font License 1.1 text, which has to travel
/// with it. Nothing is fetched at run time. A typeface is loaded on first use and then shared: it belongs to this class
/// (or, for a fallback, to the system font manager), so callers must not dispose it.
/// </para>
/// <para>
/// Provenance. All five come from the google/fonts repository at commit 47d54ca1ebb14fd864695f1d39dc734f39017c33
/// (folders ofl/bodonimoda, ofl/playfairdisplay, ofl/cinzel, ofl/bebasneue and ofl/oswald), and each <c>OFL-*.txt</c> is
/// that folder's <c>OFL.txt</c> unchanged. Bebas Neue is the published file. The other four are static instances of the
/// published variable fonts, made once with <c>fonttools varLib.instancer {file} wght={weight}</c>, at the weights the
/// cover page used: Bodoni Moda 700, Playfair Display 800, Cinzel 800 and Oswald 700. Bodoni Moda is also pinned to its
/// default optical size, <c>opsz=11</c>: the page asked Google Fonts for the weight only, and the title it drew matches
/// that size, not the 96 at the top of the axis. The instances' name tables were then made to agree with their weight.
/// Playfair Display declares a Reserved Font Name, which a Modified Version of the font may not use (OFL 1.1, section 3),
/// so its instance is called Cover Serif: inside the file, in the file's name (<c>CoverSerif-ExtraBold.ttf</c>) and
/// everywhere the plugin presents it. Its license file stays <c>OFL-PlayfairDisplay.txt</c>, the text of the work it
/// was made from.
/// </para>
/// </remarks>
public static class FontCatalog
{
    // The ratio is explained on BaselineBelowMiddle.
    private sealed record BundledFont(string Family, string File, float BaselineBelowMiddle);

    // The page's order. The first is the default font: it is what an unknown family name gets.
    private static readonly BundledFont[] Bundled =
    [
        new("Bodoni Moda", "BodoniModa-Bold.ttf", 0.2377f),
        new("Cover Serif", "CoverSerif-ExtraBold.ttf", 0.3117f),
        new("Cinzel", "Cinzel-ExtraBold.ttf", 0.2240f),
        new("Bebas Neue", "BebasNeue-Regular.ttf", 0.2500f),
        new("Oswald", "Oswald-Bold.ttf", 0.3050f),
    ];

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKTypeface> Loaded = new();

    // One shaper per typeface, kept as long as the typeface is. The bundled typefaces live for good, so their shapers do
    // too; a fallback typeface can be collected, and takes its shaper with it.
    private static readonly ConditionalWeakTable<SKTypeface, SKShaper> Shapers = new();

    /// <summary>The bundled families, in the order the cover page lists its fonts. The first is the default.</summary>
    public static IReadOnlyList<string> Families { get; } = Array.AsReadOnly(Array.ConvertAll(Bundled, font => font.Family));

    /// <summary>
    /// The typeface of a bundled family. The name is matched ignoring case; a name that is unknown, blank or missing
    /// gets the default family, Bodoni Moda, instead of an error, so a cover is still drawn when its settings name a
    /// font that is gone.
    /// </summary>
    public static SKTypeface Get(string? family)
    {
        var font = Array.Find(Bundled, candidate => string.Equals(candidate.Family, family, StringComparison.OrdinalIgnoreCase))
            ?? Bundled[0];

        lock (Gate)
        {
            if (!Loaded.TryGetValue(font.Family, out var typeface))
            {
                typeface = Load(font.File);
                Loaded.Add(font.Family, typeface);
            }

            return typeface;
        }
    }

    /// <summary>
    /// The typeface that should draw <paramref name="text"/>: the family's own when it has a glyph for every
    /// character, otherwise a system font that has the first character the family lacks (in the family's weight when
    /// the system has one), and the family's own again when no font has it. One typeface draws the whole text, so a
    /// title that mixes scripts no single fallback covers shows empty boxes for the rest. The family is looked up like
    /// <see cref="Get"/> does.
    /// </summary>
    public static SKTypeface ForText(string? family, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var typeface = Get(family);
        foreach (var rune in text.EnumerateRunes())
        {
            if (typeface.ContainsGlyph(rune.Value))
            {
                continue;
            }

            // The font style is a native object that its wrapper frees when it is collected. Held by this variable it
            // outlives the call below; as a temporary it could be freed while the font manager still reads it.
            using var style = typeface.FontStyle;
            return SKFontManager.Default.MatchCharacter(null, style, null, rune.Value) ?? typeface;
        }

        return typeface;
    }

    /// <summary>
    /// Lays out <paramref name="text"/> with HarfBuzz, in the font's typeface and at its size, as a browser lays out canvas
    /// text: pairs the font kerns are kerned, scripts whose letters join take their joined forms, and right-to-left text
    /// comes out in visual order, so the first glyph is always the leftmost. The result holds the glyph ids, where each
    /// glyph goes measured from the start of the line, and the width of the line.
    /// </summary>
    /// <remarks>
    /// HarfBuzz shapes the whole text as one run, with the direction and script it guesses from the first strong
    /// character. A title that mixes directions or scripts therefore comes out in the direction and with the rules of
    /// its first letters.
    /// </remarks>
    public static SKShaper.Result Shape(SKFont font, string text)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        // The string overload of Shape gives every call a buffer of its own, so one shaper serves any number of threads.
        // The shaper of a typeface that nothing else holds is reachable only through the table, which does not keep it
        // alive while HarfBuzz runs. The GC.KeepAlive call below does, until the native shaping has returned; a local
        // variable alone would not, because the runtime counts a local as dead after its last use.
        var shaper = Shapers.GetValue(font.Typeface, static typeface => new SKShaper(typeface));
        var shaped = shaper.Shape(text, font);
        GC.KeepAlive(shaper);
        return shaped;
    }

    /// <summary>
    /// How far below the line the page called "middle" the baseline of text in <paramref name="typeface"/> lies, as a
    /// fraction of the text size. The page drew on a canvas with <c>textBaseline = "middle"</c>, and Chrome puts that
    /// line half way up an em that has the font's ascent and descent scaled to add up to one em: the baseline lies
    /// <c>(ascent - descent) / (ascent + descent) / 2</c> ems below it. Half way between the unscaled ascent and
    /// descent would put a title 4 to 12 pixels lower at 80 pixels. The page used the chosen family's metrics even when
    /// a fallback font drew the glyphs, so the caller passes the chosen family's typeface, not the fallback's, and the
    /// title stays on the line that family puts it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The five bundled fonts have the ratio fixed, to four decimals, which moves a title by 0.004 pixel at most at 80
    /// pixels. The values are what the rule gives for the ascent and descent that FreeType reads from the fonts (their
    /// hhea metrics), and are within 0.0001 of what Chrome 155 measured: 0.2378 for Bodoni Moda, 0.3118 for Cover
    /// Serif, 0.2240 for Cinzel, 0.2500 for Bebas Neue and 0.3050 for Oswald. They are fixed because Skia backends do
    /// not all report the same metrics for a font. Bebas Neue sets no USE_TYPO_METRICS bit and its OS/2 win metrics are
    /// 950 and 350 against 900 and 300 in hhea, so a backend that reads those would compute 0.2308 for it and draw
    /// the title 1.5 pixels higher at 80 pixels.
    /// </para>
    /// <para>
    /// Any other typeface, none of which is passed today, gets the rule computed from the metrics Skia reports.
    /// </para>
    /// </remarks>
    internal static float BaselineBelowMiddle(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);

        lock (Gate)
        {
            foreach (var font in Bundled)
            {
                if (Loaded.TryGetValue(font.Family, out var loaded) && ReferenceEquals(loaded, typeface))
                {
                    return font.BaselineBelowMiddle;
                }
            }
        }

        using var sized = new SKFont(typeface, 100) { LinearMetrics = true, Hinting = SKFontHinting.None };
        var metrics = sized.Metrics;

        // Skia's ascent is negative, up from the baseline; its descent is positive, down from it.
        var ascent = -metrics.Ascent;
        var descent = metrics.Descent;
        return (ascent - descent) / (2 * (ascent + descent));
    }

    private static SKTypeface Load(string file)
    {
        var resource = "Fonts/" + file;
        using var stream = typeof(FontCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The font resource {resource} is missing from the plugin assembly.");

        // SKData copies the bytes into native memory, which the typeface then keeps alive by itself.
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"SkiaSharp could not read the font resource {resource}.");
    }
}
