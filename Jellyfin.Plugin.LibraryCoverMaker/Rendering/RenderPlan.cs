using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>What a slot of a cover is made from.</summary>
public enum SlotKind
{
    /// <summary>A picture, shown with a slow zoom.</summary>
    Image,

    /// <summary>A clip from a video file.</summary>
    Video
}

/// <summary>One slot of a render: the file it is made from and how long it lasts.</summary>
/// <param name="Kind">Whether the slot is a picture or a clip.</param>
/// <param name="Path">The picture or video file. An absolute path.</param>
/// <param name="StartSeconds">For a video slot, where in the file the clip starts. Not used for a picture.</param>
/// <param name="DurationSeconds">How long the slot lasts, fades included. <see cref="CoverRenderer.SlotSeconds"/> gives the value that makes the whole cover last its length.</param>
/// <param name="IsHdr">For a video slot, whether the video is HDR and must be tone mapped to SDR. Not used for a picture.</param>
public sealed record SlotInput(SlotKind Kind, string Path, double StartSeconds, double DurationSeconds, bool IsHdr);

/// <summary>Everything the renderer needs to make one cover: the slots, the overlay laid over them and where the result goes.</summary>
/// <param name="Slots">The slots, in order. At least one.</param>
/// <param name="OverlayPngPath">The overlay drawn over every frame: the darkening, the frame and the title. An absolute path to a PNG of exactly <paramref name="Width"/> by <paramref name="Height"/>.</param>
/// <param name="Width">Width of the cover, in pixels. Even.</param>
/// <param name="Height">Height of the cover, in pixels. Even.</param>
/// <param name="Fps">Frames per second of the animation.</param>
/// <param name="Fade">Whether the slots fade into one another (<see cref="CoverRenderer.FadeFrames"/> frames) instead of cutting.</param>
/// <param name="AnimatedOut">Where the animated WebP ends up. An absolute path where nothing exists yet: a render never replaces a file.</param>
/// <param name="StillOut">Where the still JPEG of the first frame ends up. An absolute path where nothing exists yet: a render never replaces a file.</param>
/// <param name="TempFolder">
/// Where the render is written before it is moved to its final paths, so that a half made file never sits at a
/// final path. An absolute path, best on the same volume as the final files so that the move is a rename. Null
/// means beside the final files. The folder is created when it is missing.
/// </param>
public sealed record RenderPlan(
    IReadOnlyList<SlotInput> Slots,
    string OverlayPngPath,
    int Width,
    int Height,
    int Fps,
    bool Fade,
    string AnimatedOut,
    string StillOut,
    string? TempFolder = null);
