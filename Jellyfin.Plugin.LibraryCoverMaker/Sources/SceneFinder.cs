using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.LibraryCoverMaker.Sources;

/// <summary>Where a clip for a cover starts, and the part of the picture to keep.</summary>
/// <param name="StartSeconds">Second of the video where the clip starts.</param>
/// <param name="Crop">
/// The part of the picture to keep, in the pixels of the file, when the video has black bars burned in (letterbox or
/// pillarbox) that take at least 4% of its width or of its height. Null when it has none worth cutting.
/// </param>
public sealed record SceneChoice(double StartSeconds, CropRect? Crop);

/// <summary>
/// Finds the moment of a video that a cover slot should show, and the crop that takes away burned-in black bars.
/// </summary>
/// <remarks>
/// <para>
/// The candidates come from the first of three sources that gives any frame at or above
/// <see cref="FrameScorer.Floor"/>: Jellyfin's trickplay thumbnails (already on disk, so the cheapest), the chapters,
/// and 24 frames spread evenly over what is allowed. Only moments inside <see cref="SceneWindow.Allowed"/> count:
/// not the first and last tenth of the video, and not the media segments Jellyfin marks as intro, outro, recap,
/// preview or commercial. The five best frames are tried best first, and the first whose clip has no scene cut wins.
/// </para>
/// <para>
/// A clip is checked in one ffmpeg pass over exactly the seconds the cover will show. Two branches read the same
/// frames. One looks at three frames a second for the black bars (<c>cropdetect</c>, before any scaling, so the
/// crop is in the pixels of the file, and with a reset of 0, so that it keeps the largest picture seen and a dark
/// frame never shrinks it); its limit is relative, so that a 10-bit HDR video, whose black is a larger number than
/// in an 8-bit one, behaves the same. The other scales every frame to 320 px and looks for cuts (<c>scdet</c>).
/// Each branch writes what it found with the <c>metadata</c> filter to a small file that is read in full and
/// deleted afterwards. ffmpeg's messages are not used: a remux with many chapters and tagged tracks prints hundreds
/// of header lines, and the runner keeps only the last 200 lines of what ffmpeg says. The frames the crop branch
/// saw are also the count of how much of the clip could be read. The size of the picture, and whether it is
/// turned, come from Jellyfin's own probe of the file.
/// </para>
/// <para>
/// A title is never an error: a missing file, frames that cannot be read, a failed ffmpeg or a manager that throws
/// all mean "no choice" (null) with a log line. Only cancellation is thrown.
/// </para>
/// </remarks>
public sealed class SceneFinder
{
    // The width of the pictures that candidates are scored on: the scorer works on 160 px anyway.
    private const int FrameWidth = 160;

    // How many frames are read from the video when neither trickplay nor chapters give candidates.
    private const int SampledFrames = 24;

    // How many of the best frames are tried before the title is given up.
    private const int BestCount = 5;

    // The crop detector looks at this many frames a second.
    private const int CropSamplesPerSecond = 3;

    // A pass that looked at more than this many frames fewer than the clip should have is a short or damaged clip.
    private const int SampleTolerance = 1;

    // Bars narrower than this share of the width or of the height are noise (a few rows of dark picture edge).
    private const double MinBarShare = 0.04;

    // A crop that keeps less of the width or of the height than this is not a picture but a patch of light.
    private const double MinKeptShare = 0.25;

    // A clip check reads seconds of media: a minute with no sign of life is a file that hangs, such as a network share.
    private static readonly TimeSpan CheckStall = TimeSpan.FromMinutes(1);

    // The black limit is relative, 0.1 of the range of the format (the default, 24 of 255, is not), the box is
    // rounded to even sizes and offsets, every sampled frame is looked at, and the largest picture is never
    // forgotten. Each branch prints its frame metadata to a file: all of it for the crop branch (a line "frame:"
    // per sampled frame), only the cut time for the cut branch.
    private static readonly string CropBranch =
        FormattableString.Invariant($"[c]fps={CropSamplesPerSecond},")
        + "cropdetect=limit=0.1:round=2:skip=0:reset=0,metadata=mode=print:file={0},nullsink";

    private static readonly string CutBranch =
        "[s]scale=320:-2:reset_sar=1,scdet=threshold=10,metadata=mode=print:key=lavfi.scd.time:file={0}[o]";

    private static readonly MediaSegmentType[] ExcludedSegments =
    [
        MediaSegmentType.Intro,
        MediaSegmentType.Outro,
        MediaSegmentType.Recap,
        MediaSegmentType.Preview,
        MediaSegmentType.Commercial,
    ];

    private readonly ITrickplayManager _trickplay;
    private readonly IMediaSegmentManager _segments;
    private readonly IChapterManager _chapters;
    private readonly ILibraryManager _library;
    private readonly FfmpegRunner _ffmpeg;
    private readonly ILogger<SceneFinder> _log;
    private readonly string _scratch;

    /// <summary>Creates the finder.</summary>
    /// <param name="trickplay">Jellyfin's trickplay manager.</param>
    /// <param name="segments">Jellyfin's media segment manager.</param>
    /// <param name="chapters">Jellyfin's chapter manager.</param>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="ffmpeg">Runs Jellyfin's ffmpeg.</param>
    /// <param name="log">The log.</param>
    /// <param name="scratchFolder">
    /// Where the small result files of a clip check are written while ffmpeg runs. A folder ffmpeg can write to;
    /// null is the system's temporary folder.
    /// </param>
    public SceneFinder(
        ITrickplayManager trickplay,
        IMediaSegmentManager segments,
        IChapterManager chapters,
        ILibraryManager library,
        FfmpegRunner ffmpeg,
        ILogger<SceneFinder> log,
        string? scratchFolder = null)
    {
        ArgumentNullException.ThrowIfNull(trickplay);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(log);

        _trickplay = trickplay;
        _segments = segments;
        _chapters = chapters;
        _library = library;
        _ffmpeg = ffmpeg;
        _log = log;
        _scratch = scratchFolder ?? System.IO.Path.GetTempPath();
    }

    // What Jellyfin knows of the video stream of a file, which is where the size and the rotation come from.
    // A seam for the tests: they have no library to ask.
    internal Func<Video, MediaStream?> ProbeVideo { get; set; } =
        video => BaseItem.MediaSourceManager is null ? null : video.GetDefaultVideoStream();

    private enum Source
    {
        Trickplay,
        Chapters,
        Frames,
    }

    private enum ClipOutcome
    {
        // No cut and all the frames: usable.
        Clean,

        // A scene cut, or an output that cannot be trusted to show there is none.
        Cut,

        // Too few frames, or ffmpeg failed: the clip cannot be read.
        Unreadable,

        // ffmpeg did not run to its end (it hangs, it is not configured): nothing else will work either.
        Aborted,
    }

    /// <summary>
    /// The moment of <paramref name="video"/> where a clip of <paramref name="clipSeconds"/> should start, with the
    /// crop for its black bars, or null when the title has no usable scene.
    /// </summary>
    /// <param name="video">One video. For a series the caller chooses the episode.</param>
    /// <param name="clipSeconds">How long the clip lasts. Positive and finite.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="clipSeconds"/> is not a positive, finite number.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<SceneChoice?> FindStartAsync(Video video, double clipSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (!double.IsFinite(clipSeconds) || clipSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clipSeconds), clipSeconds, "A clip needs a positive length.");
        }

        ct.ThrowIfCancellationRequested();

        var path = video.Path;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !File.Exists(path))
        {
            _log.LogDebug(
                "Library Cover Maker: {Title} has no video file to take a scene from ({Path})",
                video.Name,
                path);
            return null;
        }

        if (video.RunTimeTicks is not > 0)
        {
            _log.LogDebug("Library Cover Maker: {Title} has no known length, so it gets no scene", video.Name);
            return null;
        }

        var runtime = video.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;

        LibraryOptions options;
        IReadOnlyList<(double Start, double End)> allowed;
        try
        {
            options = _library.GetLibraryOptions(video);
            var excluded = await ExcludedAsync(video, options).ConfigureAwait(false);
            allowed = SceneWindow.Allowed(runtime, excluded, clipSeconds);
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            // Without the segments the intro could end up on a cover, so the title waits for a better moment.
            _log.LogWarning(
                ex,
                "Library Cover Maker: the media segments of {Title} could not be read, so it gets no scene",
                video.Name);
            return null;
        }

        if (allowed.Count == 0)
        {
            _log.LogDebug(
                "Library Cover Maker: no stretch of {Title} can hold a clip of {Seconds} s",
                video.Name,
                clipSeconds);
            return null;
        }

        foreach (var source in new[] { Source.Trickplay, Source.Chapters, Source.Frames })
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await GatherAsync(source, video, path, options, allowed, ct).ConfigureAwait(false);
            var best = candidates
                .Where(c => c.Score >= FrameScorer.Floor)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Time)
                .Take(BestCount)
                .ToList();
            if (best.Count > 0)
            {
                return await FirstCleanAsync(video, path, clipSeconds, best, source, ct).ConfigureAwait(false);
            }
        }

        _log.LogDebug("Library Cover Maker: {Title} has no frame good enough for a cover", video.Name);
        return null;
    }

    // Only the caller's own token is a cancellation; an OperationCanceledException from inside a manager, such as a
    // timeout of its own, is a failure for this title.
    private static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsAllowed(IReadOnlyList<(double Start, double End)> allowed, double time) =>
        allowed.Any(piece => piece.Start <= time && time <= piece.End);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // The last few lines of what ffmpeg said, for a log line.
    private static string Tail(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines[Math.Max(0, lines.Length - 3)..]);
    }

    // 24 moments spread evenly over the allowed stretches taken together, so that a video that is mostly intro still
    // gets all of them in the part that is left.
    private static List<double> Spread(IReadOnlyList<(double Start, double End)> allowed, int count)
    {
        var total = allowed.Sum(piece => piece.End - piece.Start);
        if (total <= 0)
        {
            return allowed.Select(piece => piece.Start).Take(count).ToList();
        }

        var times = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = total * (i + 0.5) / count;
            foreach (var (start, end) in allowed)
            {
                var length = end - start;
                if (offset <= length)
                {
                    times.Add(Math.Clamp(start + offset, start, end));
                    break;
                }

                offset -= length;
            }
        }

        return times;
    }

    // The union of the boxes of light seen in the clip, as a crop, when it takes away enough to be worth it.
    private static CropRect? CropFrom(
        IReadOnlyList<(int X1, int X2, int Y1, int Y2)> boxes,
        (int Width, int Height)? size)
    {
        if (size is not var (frameWidth, frameHeight))
        {
            return null;
        }

        var left = boxes.Min(box => box.X1);
        var right = boxes.Max(box => box.X2) + 1;
        var top = boxes.Min(box => box.Y1);
        var bottom = boxes.Max(box => box.Y2) + 1;

        // yuv420p needs even sizes and offsets: the box is widened to them, never narrowed.
        left &= ~1;
        top &= ~1;
        right = Math.Min(frameWidth & ~1, (right + 1) & ~1);
        bottom = Math.Min(frameHeight & ~1, (bottom + 1) & ~1);
        var width = right - left;
        var height = bottom - top;
        if (width < 2 || height < 2 || width < frameWidth * MinKeptShare || height < frameHeight * MinKeptShare)
        {
            return null;
        }

        var cutWidth = (frameWidth - width) / (double)frameWidth;
        var cutHeight = (frameHeight - height) / (double)frameHeight;
        return cutWidth >= MinBarShare || cutHeight >= MinBarShare ? new CropRect(left, top, width, height) : null;
    }

    // What the probe says of the default video stream: its index in the file, so that the filter graph reads that
    // stream and no other, and the size ffmpeg's filters see: the stored size, swapped when the video is turned a
    // quarter, because ffmpeg turns it before the filter graph. Null parts when Jellyfin has not probed the file.
    private (int? Index, (int Width, int Height)? Size) Probe(Video video)
    {
        MediaStream? stream;
        try
        {
            stream = ProbeVideo(video);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogDebug(ex, "Library Cover Maker: the video stream of {Title} could not be read", video.Name);
            return (null, null);
        }

        if (stream is null)
        {
            return (null, null);
        }

        if (stream.Width is not > 0 || stream.Height is not > 0)
        {
            return (stream.Index, null);
        }

        var quarterTurn = Math.Abs(stream.Rotation ?? 0) % 180 == 90;
        var (width, height) = (stream.Width.Value, stream.Height.Value);
        return (stream.Index, quarterTurn ? (height, width) : (width, height));
    }

    // A path inside a filter graph is read at two levels, and each takes a backslash for its own special characters:
    // first the option value (a colon ends it), then the graph (brackets, commas and semicolons end the filter).
    private static string InGraph(string path)
    {
        var value = EscapeWith(path, "\\':");
        return EscapeWith(value, "\\'[],;");
    }

    private static string EscapeWith(string text, string special)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (special.Contains(c, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private async Task<List<(double Start, double End)>> ExcludedAsync(Video video, LibraryOptions options)
    {
        var segments = await _segments.GetSegmentsAsync(video, ExcludedSegments, options, true).ConfigureAwait(false);
        return segments
            .Select(segment => (
                segment.StartTicks / (double)TimeSpan.TicksPerSecond,
                segment.EndTicks / (double)TimeSpan.TicksPerSecond))
            .ToList();
    }

    // Scores the candidates of one source. A source that fails is an empty one, so that the next is tried.
    private async Task<List<Candidate>> GatherAsync(
        Source source,
        Video video,
        string path,
        LibraryOptions options,
        IReadOnlyList<(double Start, double End)> allowed,
        CancellationToken ct)
    {
        try
        {
            return source switch
            {
                Source.Trickplay => await TrickplayAsync(video, options, allowed, ct).ConfigureAwait(false),
                Source.Chapters => await ChaptersAsync(video, path, allowed, ct).ConfigureAwait(false),
                _ => await FramesAsync(path, allowed, ct).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            _log.LogWarning(
                ex,
                "Library Cover Maker: the {Source} of {Title} could not be read, so the next source is tried",
                source,
                video.Name);
            return [];
        }
    }

    // The thumbnails of the smallest width, cut out of their tiles. Jellyfin has made them for the seek bar, so
    // using them costs a decode of a few pictures and no ffmpeg run.
    private async Task<List<Candidate>> TrickplayAsync(
        Video video,
        LibraryOptions options,
        IReadOnlyList<(double Start, double End)> allowed,
        CancellationToken ct)
    {
        var result = new List<Candidate>();
        var resolutions = await _trickplay.GetTrickplayResolutions(video.Id).ConfigureAwait(false);
        if (resolutions.Count == 0)
        {
            return result;
        }

        var width = resolutions.Keys.Min();
        var info = resolutions[width];
        if (info.Width <= 0 || info.Height <= 0 || info.TileWidth <= 0 || info.TileHeight <= 0
            || info.Interval <= 0 || info.ThumbnailCount <= 0)
        {
            return result;
        }

        // Where the tiles are depends on where the library keeps trickplay: next to the media or in Jellyfin's data.
        var saveWithMedia = options.SaveTrickplayWithMedia;
        var perTile = info.TileWidth * info.TileHeight;
        var tiles = (info.ThumbnailCount + perTile - 1) / perTile;
        for (var tile = 0; tile < tiles; tile++)
        {
            ct.ThrowIfCancellationRequested();
            var first = tile * perTile;
            var wanted = Enumerable.Range(first, Math.Min(perTile, info.ThumbnailCount - first))
                .Where(index => IsAllowed(allowed, index * info.Interval / 1000.0))
                .ToList();
            if (wanted.Count == 0)
            {
                continue;
            }

            var tilePath = await _trickplay.GetTrickplayTilePathAsync(video, width, tile, saveWithMedia)
                .ConfigureAwait(false);
            if (!File.Exists(tilePath))
            {
                _log.LogDebug(
                    "Library Cover Maker: the trickplay tile {Path} of {Title} does not exist",
                    tilePath,
                    video.Name);
                continue;
            }

            using var sheet = SKBitmap.Decode(tilePath);
            if (sheet is null)
            {
                _log.LogDebug(
                    "Library Cover Maker: the trickplay tile {Path} of {Title} cannot be read",
                    tilePath,
                    video.Name);
                continue;
            }

            foreach (var index in wanted)
            {
                var cell = index - first;
                var column = cell % info.TileWidth;
                var row = cell / info.TileWidth;
                var area = SKRectI.Create(column * info.Width, row * info.Height, info.Width, info.Height);
                if (area.Right > sheet.Width || area.Bottom > sheet.Height)
                {
                    continue;
                }

                using var thumbnail = new SKBitmap();
                if (sheet.ExtractSubset(thumbnail, area))
                {
                    result.Add(new Candidate(index * info.Interval / 1000.0, FrameScorer.Score(thumbnail)));
                }
            }
        }

        return result;
    }

    // The start of each chapter, from the picture Jellyfin extracted for it, or from the video when it has none.
    private async Task<List<Candidate>> ChaptersAsync(
        Video video,
        string path,
        IReadOnlyList<(double Start, double End)> allowed,
        CancellationToken ct)
    {
        var result = new List<Candidate>();
        foreach (var chapter in _chapters.GetChapters(video.Id))
        {
            ct.ThrowIfCancellationRequested();
            var time = chapter.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
            if (!IsAllowed(allowed, time))
            {
                continue;
            }

            using var frame = !string.IsNullOrEmpty(chapter.ImagePath) && File.Exists(chapter.ImagePath)
                ? SKBitmap.Decode(chapter.ImagePath)
                : await ReadFrameAsync(path, time, ct).ConfigureAwait(false);
            if (frame is not null)
            {
                result.Add(new Candidate(time, FrameScorer.Score(frame)));
            }
        }

        return result;
    }

    private async Task<List<Candidate>> FramesAsync(
        string path,
        IReadOnlyList<(double Start, double End)> allowed,
        CancellationToken ct)
    {
        var result = new List<Candidate>();
        foreach (var time in Spread(allowed, SampledFrames))
        {
            ct.ThrowIfCancellationRequested();
            using var frame = await ReadFrameAsync(path, time, ct).ConfigureAwait(false);
            if (frame is not null)
            {
                result.Add(new Candidate(time, FrameScorer.Score(frame)));
            }
        }

        return result;
    }

    private async Task<SKBitmap?> ReadFrameAsync(string path, double time, CancellationToken ct)
    {
        var png = await _ffmpeg.ReadFrameAsync(path, time, FrameWidth, ct).ConfigureAwait(false);
        return png is null ? null : SKBitmap.Decode(png);
    }

    private async Task<SceneChoice?> FirstCleanAsync(
        Video video,
        string path,
        double clipSeconds,
        IReadOnlyList<Candidate> best,
        Source source,
        CancellationToken ct)
    {
        var probe = Probe(video);
        foreach (var candidate in best)
        {
            var (outcome, crop) = await CheckClipAsync(path, candidate.Time, clipSeconds, probe, ct)
                .ConfigureAwait(false);
            if (outcome == ClipOutcome.Clean)
            {
                return new SceneChoice(candidate.Time, crop);
            }

            if (outcome == ClipOutcome.Aborted)
            {
                return null;
            }
        }

        _log.LogDebug(
            "Library Cover Maker: none of the {Count} best moments ({Source}) of {Title} has a clip without a cut",
            best.Count,
            source,
            video.Name);
        return null;
    }

    // One pass of ffmpeg over the clip, and what the two result files say.
    private async Task<(ClipOutcome Outcome, CropRect? Crop)> CheckClipAsync(
        string path,
        double start,
        double clipSeconds,
        (int? Index, (int Width, int Height)? Size) probe,
        CancellationToken ct)
    {
        var name = System.IO.Path.Combine(_scratch, "scene-finder-" + Guid.NewGuid().ToString("N"));
        var cropFile = name + "-crop.txt";
        var cutFile = name + "-cut.txt";
        try
        {
            // The stream the size was probed from, or the first video stream when nothing is known (then no crop).
            var input = probe.Index is { } index ? FormattableString.Invariant($"[0:{index}]") : "[0:v:0]";
            var graph = input + "split=2[c][s];"
                + string.Format(CultureInfo.InvariantCulture, CropBranch, InGraph(cropFile)) + ";"
                + string.Format(CultureInfo.InvariantCulture, CutBranch, InGraph(cutFile));
            var args = new List<string>(FfmpegRunner.ThreadLimit);
            args.AddRange(["-ss", Seconds(start), "-t", Seconds(clipSeconds), "-i", path]);
            args.AddRange(["-nostats", "-an", "-sn"]);
            args.AddRange(["-filter_threads", FfmpegRunner.MaxThreads.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-filter_complex", graph, "-map", "[o]"]);
            args.AddRange(FfmpegRunner.ThreadLimit);
            args.AddRange(["-f", "null", "-"]);

            var (exit, errors) = await _ffmpeg.RunAsync(args, CheckStall, ct).ConfigureAwait(false);
            if (exit == FfmpegRunner.NotCompleted)
            {
                _log.LogWarning(
                    "Library Cover Maker: checking the clip at {Start} s of {Path} did not finish: {Errors}",
                    start,
                    path,
                    Tail(errors));
                return (ClipOutcome.Aborted, null);
            }

            if (exit != 0 || !File.Exists(cropFile) || !File.Exists(cutFile))
            {
                _log.LogDebug(
                    "Library Cover Maker: ffmpeg could not read the clip at {Start} s of {Path} ({Exit}): {Errors}",
                    start,
                    path,
                    exit,
                    Tail(errors));
                return (ClipOutcome.Unreadable, null);
            }

            if (File.ReadLines(cutFile).Any(line => line.StartsWith("lavfi.scd.time", StringComparison.Ordinal)))
            {
                return (ClipOutcome.Cut, null);
            }

            var boxes = ReadBoxes(cropFile, out var sampled);
            var needed = Math.Max(1, (int)Math.Ceiling(clipSeconds * CropSamplesPerSecond) - SampleTolerance);
            if (sampled < needed || boxes.Count == 0)
            {
                _log.LogDebug(
                    "Library Cover Maker: the clip at {Start} s of {Path} gave {Seen} of the {Needed} frames it needs",
                    start,
                    path,
                    sampled,
                    needed);
                return (ClipOutcome.Unreadable, null);
            }

            return (ClipOutcome.Clean, CropFrom(boxes, probe.Size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The result files cannot be read: nothing is known of the clip.
            _log.LogDebug(
                ex,
                "Library Cover Maker: the result for the clip at {Start} s of {Path} is unreadable",
                start,
                path);
            return (ClipOutcome.Unreadable, null);
        }
        finally
        {
            TryDelete(cropFile);
            TryDelete(cutFile);
        }
    }

    // The boxes of light the crop branch printed, and how many frames it saw. A black frame gives a reversed box
    // (x1 greater than x2), which the union ignores.
    private static List<(int X1, int X2, int Y1, int Y2)> ReadBoxes(string file, out int sampled)
    {
        sampled = 0;
        var boxes = new List<(int, int, int, int)>();
        int? x1 = null, x2 = null, y1 = null, y2 = null;
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                sampled++;
            }
            else if (line.StartsWith("lavfi.cropdetect.", StringComparison.Ordinal))
            {
                var split = line.IndexOf('=', StringComparison.Ordinal);
                var text = split > 0 ? line[(split + 1)..] : string.Empty;
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    switch (line[..split])
                    {
                        case "lavfi.cropdetect.x1":
                            x1 = value;
                            break;
                        case "lavfi.cropdetect.x2":
                            x2 = value;
                            break;
                        case "lavfi.cropdetect.y1":
                            y1 = value;
                            break;
                        case "lavfi.cropdetect.y2":
                            y2 = value;
                            break;
                    }
                }
            }

            if (x1 is { } a && x2 is { } b && y1 is { } c && y2 is { } d)
            {
                boxes.Add((a, b, c, d));
                x1 = x2 = y1 = y2 = null;
            }
        }

        return boxes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A small file in a temporary folder: nothing else to do.
        }
    }

    private readonly record struct Candidate(double Time, double Score);
}
