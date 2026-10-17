using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryCoverMaker.Sources;

/// <summary>
/// The art mode slot of a title: one of its backdrop images, which the renderer shows with a slow zoom.
/// </summary>
public sealed class ArtworkSource
{
    /// <summary>
    /// The slot that shows a backdrop of <paramref name="title"/>, or null when the title has no backdrop that is a
    /// file on this server (a backdrop Jellyfin only knows by its address, or whose file is gone, does not count).
    /// </summary>
    /// <param name="title">The title. For a series, the series.</param>
    /// <param name="backdropIndex">
    /// The backdrop an admin chose for a locked slot, as Jellyfin numbers the title's backdrops. Null, out of range or
    /// not on disk any more, one of the usable backdrops is drawn instead.
    /// </param>
    /// <param name="seconds">How long the slot lasts. Positive and finite.</param>
    /// <param name="random">The source of the draw.</param>
    public SlotInput? ForTitle(BaseItem title, int? backdropIndex, double seconds, Random random)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(random);
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A slot needs a positive length.");
        }

        var backdrops = title.GetImages(ImageType.Backdrop).ToList();
        if (backdropIndex is int index && index >= 0 && index < backdrops.Count && IsOnDisk(backdrops[index]))
        {
            return Slot(backdrops[index].Path, seconds);
        }

        var usable = backdrops.Where(IsOnDisk).ToList();
        return usable.Count == 0 ? null : Slot(usable[random.Next(usable.Count)].Path, seconds);
    }

    private static SlotInput Slot(string path, double seconds) => new(SlotKind.Image, path, 0, seconds, IsHdr: false);

    private static bool IsOnDisk(ItemImageInfo image) =>
        image.IsLocalFile
        && !string.IsNullOrEmpty(image.Path)
        && Path.IsPathRooted(image.Path)
        && File.Exists(image.Path);
}
