using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LibraryCoverMaker.Model;

namespace Jellyfin.Plugin.LibraryCoverMaker.Api;

/// <summary>One library on the Dashboard page's grid: what the card shows.</summary>
public sealed class LibraryCardDto
{
    /// <summary>The library's item id.</summary>
    public Guid Id { get; set; }

    /// <summary>The library's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The library's type as Jellyfin names it in lowercase (movies, tvshows, music), or null for a mixed library.
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>Whether the library is switched on. A switched-off library is visible to nobody, not built.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the plugin renders covers for this library. False for music, books and photos, which only get upload
    /// and pin.
    /// </summary>
    public bool CanRender { get; set; }

    /// <summary>How the library's cover content is chosen.</summary>
    public CoverMode Mode { get; set; }

    /// <summary>Whether the library is out of the rotation.</summary>
    public bool Pinned { get; set; }

    /// <summary>When the current cover was made, or null when the library has none yet.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// When the cover renews, for a library with a timetable of its own. Null for the others, which follow the
    /// weekly task or are not renewed (see <see cref="Schedule"/>).
    /// </summary>
    public DateTimeOffset? NextRenewal { get; set; }

    /// <summary>
    /// <c>own</c> (renews every <see cref="RenewEveryDays"/> days), <c>weekly</c> (follows the scheduled task) or
    /// <c>off</c> (pinned, manual or not rendered by the plugin).
    /// </summary>
    public string Schedule { get; set; } = "off";

    /// <summary>Days between renewals of this library, or null when it has no timetable of its own.</summary>
    public int? RenewEveryDays { get; set; }

    /// <summary>Why the last render failed, until a render succeeds.</summary>
    public string? LastError { get; set; }

    /// <summary>Something that happened without an admin asking, such as a locked title unlocked.</summary>
    public string? Notice { get; set; }

    /// <summary>When <see cref="Notice"/> happened.</summary>
    public DateTimeOffset? NoticeAt { get; set; }

    /// <summary>The cover that is the library's Primary image now. Null when the plugin has not changed it.</summary>
    public Guid? CurrentCoverId { get; set; }
}

/// <summary>
/// Everything the page needs to know about one library's cover. It is <see cref="LibraryCoverState"/> without the
/// bookkeeping about where Jellyfin keeps the Primary image, which holds server paths.
/// </summary>
public sealed class LibraryStateDto
{
    /// <summary>The library's item id.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>How the library's cover content is chosen.</summary>
    public CoverMode Mode { get; set; }

    /// <summary>Whether the library is out of the rotation.</summary>
    public bool Pinned { get; set; }

    /// <summary>How the cover looks and which titles feed it.</summary>
    public CoverStyle Style { get; set; } = new();

    /// <summary>The five slots, in order.</summary>
    public CoverSlot[] Slots { get; set; } = Array.Empty<CoverSlot>();

    /// <summary>The cover that is the library's Primary image now.</summary>
    public Guid? CurrentCoverId { get; set; }

    /// <summary>The Primary image the library had before the plugin first replaced it.</summary>
    public CoverRecord? Original { get; set; }

    /// <summary>The covers the library has had, newest first.</summary>
    public IReadOnlyList<CoverRecord> History { get; set; } = Array.Empty<CoverRecord>();

    /// <summary>Days between renewals, or null for the weekly task.</summary>
    public int? RenewEveryDays { get; set; }

    /// <summary>When the plugin last rendered a cover.</summary>
    public DateTimeOffset? LastRenderedAt { get; set; }

    /// <summary>Why the last render failed.</summary>
    public string? LastError { get; set; }

    /// <summary>When <see cref="LastError"/> happened.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Something that happened without an admin asking.</summary>
    public string? Notice { get; set; }

    /// <summary>When <see cref="Notice"/> happened.</summary>
    public DateTimeOffset? NoticeAt { get; set; }

    /// <summary>Copies what the page shows out of a state.</summary>
    internal static LibraryStateDto From(LibraryCoverState state) => new()
    {
        LibraryId = state.LibraryId,
        Mode = state.Mode,
        Pinned = state.Pinned,
        Style = state.Style,
        Slots = state.Slots,
        CurrentCoverId = state.CurrentCoverId,
        Original = state.Original,
        History = state.History,
        RenewEveryDays = state.RenewEveryDays,
        LastRenderedAt = state.LastRenderedAt,
        LastError = state.LastError,
        LastErrorAt = state.LastErrorAt,
        Notice = state.Notice,
        NoticeAt = state.NoticeAt,
    };
}

/// <summary>
/// The style fields an admin changes. A field left out stays as it is. Values outside their range are refused, not
/// clamped.
/// </summary>
public sealed class StyleUpdateDto
{
    /// <summary>Text on the cover (up to 100 characters). Empty means the library's own name.</summary>
    public string? Title { get; set; }

    /// <summary>One of the bundled font families.</summary>
    public string? Font { get; set; }

    /// <summary>Size of the text, 40 to 200.</summary>
    public int? TextSize { get; set; }

    /// <summary>How much the picture is darkened, in percent, 0 to 70.</summary>
    public int? Darken { get; set; }

    /// <summary>Whether the cover has the rounded white frame.</summary>
    public bool? Frame { get; set; }

    /// <summary>Whether the text casts a shadow.</summary>
    public bool? Shadow { get; set; }

    /// <summary>Total length of the animation in seconds, 2 to 12.</summary>
    public int? LengthSeconds { get; set; }

    /// <summary>Whether the slots fade into one another.</summary>
    public bool? Fade { get; set; }

    /// <summary>How many slots a cover uses, 1 to 5.</summary>
    public int? SlotCount { get; set; }

    /// <summary>Whether free slots may draw from the most watched titles.</summary>
    public bool? UseMostWatched { get; set; }

    /// <summary>Whether free slots may draw from the top rated titles.</summary>
    public bool? UseTopRated { get; set; }

    /// <summary>Whether free slots may draw from the recently added titles.</summary>
    public bool? UseRecent { get; set; }
}

/// <summary>One slot as an admin sets it: free (no item) or locked to a title.</summary>
public sealed class SlotUpdateDto
{
    /// <summary>The title to lock into the slot. Null leaves the slot free.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>For a locked slot in scenes mode: where the clip starts, in seconds.</summary>
    public double? MomentSeconds { get; set; }

    /// <summary>For a locked slot in art mode: which of the title's backdrops to use.</summary>
    public int? BackdropIndex { get; set; }
}

/// <summary>
/// What an admin changes about a library. A part left out stays as it is.
/// </summary>
public sealed class LibraryUpdateDto
{
    /// <summary>The content mode.</summary>
    public CoverMode? Mode { get; set; }

    /// <summary>The style fields to change.</summary>
    public StyleUpdateDto? Style { get; set; }

    /// <summary>All five slots, in order. Never fewer or more.</summary>
    public SlotUpdateDto[]? Slots { get; set; }

    /// <summary>Days between renewals, 1 to 365. Zero gives the library back to the weekly task.</summary>
    public int? RenewEveryDays { get; set; }
}

/// <summary>The settings that apply to every library.</summary>
public sealed class SettingsDto
{
    /// <summary>Whether a library's Primary image is the animated or the still version of its cover.</summary>
    public PrimaryFormat PrimaryFormat { get; set; }

    /// <summary>The content mode a library starts with.</summary>
    public CoverMode DefaultMode { get; set; }

    /// <summary>Width of a rendered cover, in pixels.</summary>
    public int OutputWidth { get; set; }

    /// <summary>Height of a rendered cover, in pixels.</summary>
    public int OutputHeight { get; set; }

    /// <summary>The font families a cover can use. Read only.</summary>
    public IReadOnlyList<string> Fonts { get; set; } = Array.Empty<string>();
}

/// <summary>The settings an admin changes. A field left out stays as it is.</summary>
public sealed class SettingsUpdateDto
{
    /// <summary>Whether a library's Primary image is the animated or the still version of its cover.</summary>
    public PrimaryFormat? PrimaryFormat { get; set; }

    /// <summary>The content mode a library starts with.</summary>
    public CoverMode? DefaultMode { get; set; }

    /// <summary>Width of a rendered cover, 320 to 1920 and even. The height must keep the 16 by 9 shape.</summary>
    public int? OutputWidth { get; set; }

    /// <summary>Height of a rendered cover, 180 to 1080 and even.</summary>
    public int? OutputHeight { get; set; }
}

/// <summary>A title an admin can lock into a slot.</summary>
public sealed class TitleDto
{
    /// <summary>The title's item id. For a series, the series.</summary>
    public Guid Id { get; set; }

    /// <summary>The title's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The year it was made, when Jellyfin knows it.</summary>
    public int? Year { get; set; }

    /// <summary>Whether the title has a backdrop image on this server, which art mode needs.</summary>
    public bool HasBackdrop { get; set; }
}

/// <summary>One backdrop image of a title.</summary>
public sealed class BackdropDto
{
    /// <summary>The number a locked slot stores as its backdrop index.</summary>
    public int Index { get; set; }

    /// <summary>Where Jellyfin serves the image, relative to the server's address.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether the image is a file on this server, so a cover can use it.</summary>
    public bool Usable { get; set; }
}

/// <summary>The answer to a request to build covers: how many libraries were put to work.</summary>
public sealed class GenerateAcceptedDto
{
    /// <summary>Libraries whose build was started or is already running.</summary>
    public int Queued { get; set; }
}

/// <summary>The body of an error answer: what went wrong, in a sentence or more.</summary>
/// <param name="Detail">The reasons, ready to show to an admin.</param>
public sealed record ApiError(string Detail);
