using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LibraryCoverMaker.Model;

/// <summary>How the content of a library's cover is chosen.</summary>
public enum CoverMode
{
    /// <summary>The titles' own artwork, in motion.</summary>
    Art,

    /// <summary>Short clips from the titles' videos, picked by score.</summary>
    Scenes,

    /// <summary>
    /// Content picked by an admin, slot by slot. A manual library is not rebuilt by the schedule,
    /// only when an admin asks for it.
    /// </summary>
    Manual
}

/// <summary>Where a cover in a library's history came from.</summary>
public enum CoverOrigin
{
    /// <summary>Rendered by this plugin.</summary>
    Generated,

    /// <summary>Uploaded by an admin.</summary>
    Uploaded,

    /// <summary>The Primary image the library had before the plugin first replaced it.</summary>
    Original
}

/// <summary>Which version of a cover is stored as the library's Primary image.</summary>
public enum PrimaryFormat
{
    /// <summary>The animated WebP.</summary>
    Animated,

    /// <summary>The still JPEG of the cover's first frame, for clients that cannot show the animation.</summary>
    Still
}

/// <summary>
/// How a library's cover looks and which titles feed it. Each library has its own. The defaults are the Jellyfin look:
/// the name written the way Jellyfin writes it on the library images it draws itself, with no frame.
/// </summary>
public class CoverStyle
{
    /// <summary>Text on the cover. Empty means the library's own name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Font family of the text: Jellyfin (the typeface and layout of Jellyfin's own library images) or one of the
    /// families bundled with the plugin. The names are <c>FontCatalog.Families</c>.
    /// </summary>
    public string Font { get; set; } = "Jellyfin";

    /// <summary>
    /// Size of the text (40 to 200) on a cover 960 pixels wide, scaled with the width. 112 is the size Jellyfin
    /// writes a library's name at on its 960 pixel wide library images.
    /// </summary>
    public int TextSize { get; set; } = 112;

    /// <summary>How much the picture is darkened, in percent (0 to 70).</summary>
    public int Darken { get; set; } = 15;

    /// <summary>Whether the cover has the rounded white frame.</summary>
    public bool Frame { get; set; }

    /// <summary>Whether the text casts a shadow.</summary>
    public bool Shadow { get; set; } = true;

    /// <summary>Total length of the animation, in seconds (2 to 30).</summary>
    public int LengthSeconds { get; set; } = 6;

    /// <summary>Whether the slots fade into one another instead of cutting.</summary>
    public bool Fade { get; set; } = true;

    /// <summary>How many of the slots a cover uses.</summary>
    public int SlotCount { get; set; } = LibraryCoverState.MaxSlots;

    /// <summary>Whether free slots may draw from the most watched titles.</summary>
    public bool UseMostWatched { get; set; } = true;

    /// <summary>Whether free slots may draw from the top rated titles.</summary>
    public bool UseTopRated { get; set; } = true;

    /// <summary>Whether free slots may draw from the recently added titles.</summary>
    public bool UseRecent { get; set; } = true;
}

/// <summary>One slot of a cover: free, or locked to the title an admin chose.</summary>
public class CoverSlot
{
    /// <summary>The title locked into this slot. Null while the slot is free.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>For a locked slot in scenes mode: where in the title's video the clip starts, in seconds.</summary>
    public double? MomentSeconds { get; set; }

    /// <summary>For a locked slot in art mode: which of the title's backdrop images to use.</summary>
    public int? BackdropIndex { get; set; }

    /// <summary>A locked slot keeps its title on every render. A free slot gets a new pick.</summary>
    public bool Locked => ItemId.HasValue;
}

/// <summary>A cover a library has had: where it came from and which titles it shows.</summary>
public class CoverRecord
{
    /// <summary>Names the cover's files in the store.</summary>
    public Guid Id { get; set; }

    /// <summary>When the cover was made, uploaded or kept.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Whether the plugin made the cover, an admin uploaded it, or it is the library's original Primary image.
    /// </summary>
    public CoverOrigin Origin { get; set; }

    /// <summary>The content mode the library was in when the cover was added.</summary>
    public CoverMode Mode { get; set; }

    /// <summary>
    /// For the library's original: whether it was Jellyfin's automatic collage rather than an image an admin set.
    /// Restoring an automatic original means deleting the Primary image and refreshing the library, so Jellyfin
    /// draws the collage again. False for every other cover, and for a state saved before this field existed.
    /// </summary>
    public bool Automatic { get; set; }

    /// <summary>The titles the cover shows, so it can be checked against users' parental controls later.</summary>
    public List<Guid> TitleIds { get; set; } = new();
}

/// <summary>Everything the plugin keeps about one library's cover.</summary>
public class LibraryCoverState : IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>How many slots a cover has.</summary>
    public const int MaxSlots = 5;

    /// <summary>The defaults a new style gets: 2, the Jellyfin look. See <see cref="StyleDefaults"/>.</summary>
    public const int CurrentStyleDefaults = 2;

    // The first look, the default style before the Jellyfin look: Bodoni Moda at 120 in the rounded white frame.
    private const int FirstLook = 1;
    private const string FirstLookFont = "Bodoni Moda";
    private const int FirstLookTextSize = 120;

    /// <summary>The Jellyfin library this state belongs to.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>How the library's cover content is chosen.</summary>
    public CoverMode Mode { get; set; } = CoverMode.Scenes;

    /// <summary>
    /// Whether the library is out of the rotation, so the schedule leaves its cover alone: an admin uploaded,
    /// pinned or restored a cover, or the library's saved settings could not be read.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Which defaults <see cref="Style"/> was made under: 1 for the first look, which a state saved before this field
    /// existed has, and <see cref="CurrentStyleDefaults"/> since. A state read with 1 whose style still has the first
    /// look's font, text size and frame (Bodoni Moda, 120, frame on) gets the Jellyfin look's instead, keeping the
    /// rest of its style: it is taken for a style nobody changed, since one that was set to exactly those values
    /// cannot be told apart from it. Read, the state holds <see cref="CurrentStyleDefaults"/>, so once it is saved
    /// its style is never moved again.
    /// </summary>
    public int StyleDefaults { get; set; } = CurrentStyleDefaults;

    /// <summary>How the library's cover looks and which titles feed it.</summary>
    public CoverStyle Style { get; set; } = new();

    /// <summary>
    /// The slots, in order: always <see cref="MaxSlots"/> of them, none missing. The store refuses to save any other
    /// number and sets aside a file that holds one. A locked slot keeps the title an admin chose. Free slots get new
    /// picks on every render.
    /// </summary>
    public CoverSlot[] Slots { get; set; } = Enumerable.Range(0, MaxSlots).Select(_ => new CoverSlot()).ToArray();

    /// <summary>The cover that is the library's Primary image now: an entry of the history, or the original.</summary>
    public Guid? CurrentCoverId { get; set; }

    /// <summary>
    /// The Primary image the library had before the plugin first replaced it. It is kept apart from the history,
    /// so it does not count against the history size and trimming the history never drops it.
    /// </summary>
    public CoverRecord? Original { get; set; }

    /// <summary>
    /// The covers the library has had, newest first. <c>CoverApplier.AddToHistory</c> keeps one entry per cover id.
    /// The current cover is one of them, unless it is the original, which is kept apart in
    /// <see cref="Original"/>.
    /// </summary>
    public List<CoverRecord> History { get; set; } = new();

    /// <summary>Days between renewals of this library's cover. Null follows the weekly scheduled task.</summary>
    public int? RenewEveryDays { get; set; }

    /// <summary>When the plugin last rendered a cover for this library.</summary>
    public DateTimeOffset? LastRenderedAt { get; set; }

    /// <summary>
    /// Why the last render failed, shown on the library's card. Cleared by the next successful render.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>When <see cref="LastError"/> happened.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>
    /// Something that happened without an admin asking, such as a locked title unlocked or a cover replaced.
    /// </summary>
    public string? Notice { get; set; }

    /// <summary>When <see cref="Notice"/> happened.</summary>
    public DateTimeOffset? NoticeAt { get; set; }

    /// <summary>
    /// Where Jellyfin kept the Primary image right after the plugin applied a cover. A different path or time
    /// later means the image was changed outside the plugin, for example in Jellyfin's Edit images dialog.
    /// </summary>
    public string? AppliedPrimaryPath { get; set; }

    /// <summary>
    /// When the Primary image file was last modified, as Jellyfin recorded it after the plugin applied a cover.
    /// </summary>
    public DateTime? AppliedPrimaryModified { get; set; }

    // Called before the stored properties are read: a file without StyleDefaults was saved under the first look.
    void IJsonOnDeserializing.OnDeserializing() => StyleDefaults = FirstLook;

    // Called once the stored properties are read. See StyleDefaults.
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (StyleDefaults >= CurrentStyleDefaults)
        {
            return;
        }

        var style = Style;
        if (string.Equals(style.Font, FirstLookFont, StringComparison.OrdinalIgnoreCase)
            && style.TextSize == FirstLookTextSize
            && style.Frame)
        {
            var jellyfin = new CoverStyle();
            style.Font = jellyfin.Font;
            style.TextSize = jellyfin.TextSize;
            style.Frame = jellyfin.Frame;
        }

        StyleDefaults = CurrentStyleDefaults;
    }
}
