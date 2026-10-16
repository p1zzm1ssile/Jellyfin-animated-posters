namespace Jellyfin.Plugin.LibraryCoverMaker.Model;

/// <summary>How the content of a library's cover is chosen.</summary>
public enum CoverMode
{
    /// <summary>The titles' own artwork, in motion.</summary>
    Art,

    /// <summary>Short clips from the titles' videos, picked by score.</summary>
    Scenes,

    /// <summary>Content picked by an admin, slot by slot.</summary>
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
