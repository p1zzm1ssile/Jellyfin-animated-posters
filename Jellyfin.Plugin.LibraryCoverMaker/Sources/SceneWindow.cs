using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LibraryCoverMaker.Sources;

/// <summary>
/// Works out where in a video a scene for a cover may come from: not the first and last tenth of the runtime,
/// and not the stretches Jellyfin marks as intro, outro, recap, preview or commercial.
/// </summary>
public static class SceneWindow
{
    /// <summary>
    /// The stretches of the video where a clip of <paramref name="clipSeconds"/> may be taken from.
    /// </summary>
    /// <param name="runtimeSeconds">
    /// Length of the video, in seconds. Zero, negative or not a finite number (a video with no known length)
    /// allows nothing.
    /// </param>
    /// <param name="excluded">
    /// Stretches to keep out, as (start, end) in seconds: the media segments to skip. They may come in any order,
    /// overlap, touch, or reach outside [0, <paramref name="runtimeSeconds"/>]; they are merged and clipped to the
    /// video. An entry that is empty or reversed (end not after start) or not a number excludes nothing.
    /// </param>
    /// <param name="clipSeconds">Length of the clip, in seconds. Must be a positive, finite number.</param>
    /// <param name="headTail">
    /// The share of the runtime to skip at the start and, separately, at the end. The default is a tenth, so the
    /// first 10% and the last 10% of the video are out. Zero keeps the whole video; 0.5 or more leaves nothing.
    /// Must be a finite number, not negative.
    /// </param>
    /// <returns>
    /// The pieces that can hold a whole clip, in time order, as (<c>Start</c>, <c>End</c>) in seconds.
    /// <b><c>End</c> is the latest second a clip may START at, not the second where the piece stops.</b>
    /// A clip of <paramref name="clipSeconds"/> that starts anywhere from <c>Start</c> to <c>End</c> (both
    /// included) ends inside the allowed stretch, so a candidate time <c>t</c> is allowed exactly when
    /// <c>Start &lt;= t &lt;= End</c> for one of the pieces. A stretch exactly as long as the clip gives
    /// <c>Start == End</c>; a shorter one gives no piece. Nothing allowed is an empty list.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="excluded"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="clipSeconds"/> is not positive and finite, or <paramref name="headTail"/> is negative or
    /// not finite.
    /// </exception>
    public static IReadOnlyList<(double Start, double End)> Allowed(
        double runtimeSeconds,
        IReadOnlyList<(double Start, double End)> excluded,
        double clipSeconds,
        double headTail = 0.10)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        if (!double.IsFinite(clipSeconds) || clipSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clipSeconds), clipSeconds, "A clip needs a positive length.");
        }

        if (!double.IsFinite(headTail) || headTail < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headTail), headTail, "The share to skip cannot be negative.");
        }

        var pieces = new List<(double Start, double End)>();
        if (!double.IsFinite(runtimeSeconds) || runtimeSeconds <= 0)
        {
            return pieces;
        }

        var windowStart = runtimeSeconds * headTail;
        var windowEnd = runtimeSeconds - windowStart;
        if (windowEnd <= windowStart)
        {
            return pieces;
        }

        // Walk the exclusions in order of start, with a cursor at the first second not yet settled. What lies
        // between the cursor and the next exclusion is a stretch of the window. An exclusion that overlaps an
        // earlier one only pushes the cursor on, which merges them; one that ends before the window starts never
        // moves the cursor, and one that reaches past the window's end is cut short by it.
        var cursor = windowStart;
        foreach (var (cutStart, cutEnd) in excluded.Where(cut => cut.End > cut.Start).OrderBy(cut => cut.Start))
        {
            if (cutStart > cursor)
            {
                AddIfItFits(pieces, cursor, Math.Min(cutStart, windowEnd), clipSeconds);
            }

            cursor = Math.Max(cursor, cutEnd);
            if (cursor >= windowEnd)
            {
                return pieces;
            }
        }

        AddIfItFits(pieces, cursor, windowEnd, clipSeconds);
        return pieces;
    }

    private static void AddIfItFits(
        List<(double Start, double End)> pieces,
        double start,
        double end,
        double clipSeconds)
    {
        var latestStart = end - clipSeconds;
        if (latestStart >= start)
        {
            pieces.Add((start, latestStart));
        }
    }
}
