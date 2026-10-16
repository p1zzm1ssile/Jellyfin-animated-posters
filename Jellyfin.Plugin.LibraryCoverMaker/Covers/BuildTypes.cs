namespace Jellyfin.Plugin.LibraryCoverMaker.Covers;

/// <summary>Why a cover is being built.</summary>
public enum BuildReason
{
    Scheduled,
    Manual,
    AudienceChanged,
    Slots
}

/// <summary>What a build did: whether a cover was applied, the error if not, and how many slots it filled.</summary>
public sealed record BuildResult(bool Applied, string? Error, int SlotsUsed);
