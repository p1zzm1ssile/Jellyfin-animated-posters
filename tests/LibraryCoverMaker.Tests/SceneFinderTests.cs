using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The scene finder against fakes: Jellyfin's managers are stubs, and a shell script stands in for ffmpeg, answering
/// a frame request with a PNG made for the test and a clip check with the text a real ffmpeg prints. The script
/// logs every call, so the tests can read the order in which candidates were tried.
/// </summary>
[Collection(TestCollections.BaseItemStatics)]
public sealed class SceneFinderTests : IDisposable
{
    private const double Clip = 6;
    private const int ClipSamples = 18;
    private const int CopyWidth = 160;
    private const int CopyHeight = 90;

    private readonly string _root = Directory.CreateTempSubdirectory("scene-finder-tests-").FullName;

    private readonly Dictionary<string, Func<object?[], object?>> _trickplay = new();
    private readonly Dictionary<string, Func<object?[], object?>> _segments = new();
    private readonly Dictionary<string, Func<object?[], object?>> _chapters = new();
    private readonly Dictionary<string, Func<object?[], object?>> _library = new();
    private readonly List<string> _log = new();

    private readonly string _ffmpegPath;

    public SceneFinderTests()
    {
        _ffmpegPath = WriteFakeFfmpeg();
        SetClip(null, ClipText());
        _trickplay["GetTrickplayResolutions"] = _ => Task.FromResult(new Dictionary<int, TrickplayInfo>());
        _segments["GetSegmentsAsync"] = _ => Segments();
        _chapters["GetChapters"] = _ => (IReadOnlyList<ChapterInfo>)Array.Empty<ChapterInfo>();
        _library["GetLibraryOptions"] = _ => new LibraryOptions();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Stands in for one of Jellyfin's interfaces: each method is a delegate; an unlisted one fails.</summary>
    public class HandlerStub : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Methods { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Methods.TryGetValue(targetMethod!.Name, out var handler)
                ? handler(args ?? [])
                : throw new NotSupportedException($"{targetMethod.Name} is not part of this stub.");
    }

    internal static T Stub<T>(Dictionary<string, Func<object?[], object?>> methods)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, HandlerStub>();
        ((HandlerStub)(object)proxy).Methods = methods;
        return proxy;
    }

    /// <summary>Keeps what is logged.</summary>
    private sealed class RecordingLogger : ILogger<SceneFinder>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{logLevel}: {formatter(state, exception)} {exception?.Message}".TrimEnd());
    }

    private static T Some<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private string Path(string name) => System.IO.Path.Combine(_root, name);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // The fake ffmpeg. It finds out from its arguments what it is asked for (a frame on pipe:1, or a clip check with
    // the null muxer), logs "<mode> <-ss value>" and the whole command line, and answers from the files of the test.
    private string WriteFakeFfmpeg()
    {
        var script = Path("fake-ffmpeg.sh");
        File.WriteAllText(
            script,
            $$"""
            #!/bin/sh
            dir='{{_root}}'
            mode=''; ss=''; prev=''
            for a in "$@"; do
              if [ "$prev" = '-ss' ]; then ss="$a"; fi
              if [ "$a" = 'pipe:1' ]; then mode=frame; fi
              if [ "$prev" = '-f' ] && [ "$a" = 'null' ]; then mode=clip; fi
              prev="$a"
            done
            graph=''
            prev=''
            for a in "$@"; do
              if [ "$prev" = '-filter_complex' ]; then graph="$a"; fi
              prev="$a"
            done
            echo "$mode $ss" >> "$dir/calls.log"
            printf '%s\n' "$@" > "$dir/last-$mode.args"
            case "$mode" in
              frame) cat "$dir/frame-$ss.png" || exit 1 ;;
              clip)
                # Noise like the header of a remux with many chapters and tracks: it must not matter.
                i=0
                while [ "$i" -lt 600 ]; do echo "    Chapter #0:$i: start $i.0, end $i.5" >&2; i=$((i+1)); done
                crop=$(printf '%s' "$graph" | sed -n "s/.*file=\([^,;]*-crop\.txt\).*/\1/p")
                cut=$(printf '%s' "$graph" | sed -n "s/.*file=\([^,;[]*-cut\.txt\).*/\1/p")
                for kind in crop cut; do
                  from="$dir/clip-$ss.$kind"
                  if [ ! -f "$from" ]; then from="$dir/clip-default.$kind"; fi
                  case "$kind" in crop) cp "$from" "$crop" ;; cut) cp "$from" "$cut" ;; esac
                done
                if [ -f "$dir/unreadable" ]; then chmod 000 "$crop"; fi
                if [ -f "$dir/clip-$ss.exit" ]; then exit "$(cat "$dir/clip-$ss.exit")"; fi
                ;;
            esac
            """.Replace("\r", string.Empty, StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return script;
    }

    private void SetClip(double? start, ClipFiles files, int exit = 0)
    {
        var name = start is { } at ? Seconds(at) : "default";
        File.WriteAllText(Path($"clip-{name}.crop"), files.Crop);
        File.WriteAllText(Path($"clip-{name}.cut"), files.Cut);
        if (exit != 0)
        {
            File.WriteAllText(Path($"clip-{name}.exit"), exit.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void SetFrame(double at, SKBitmap frame) =>
        File.WriteAllBytes(Path($"frame-{Seconds(at)}.png"), Png(frame));

    internal static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>What the two metadata filters of a clip check write: the crop branch and the cut branch.</summary>
    private sealed record ClipFiles(string Crop, string Cut);

    // One frame as the metadata filter prints it, the way jellyfin-ffmpeg 8.1.3 writes it.
    private static string CropFrame(int index, int x1, int x2, int y1, int y2) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"frame:{index,-4} pts:{index,-7} pts_time:{index / 3.0:0.######}\n"
            + $"lavfi.cropdetect.x1={x1}\nlavfi.cropdetect.x2={x2}\n"
            + $"lavfi.cropdetect.y1={y1}\nlavfi.cropdetect.y2={y2}\n"
            + $"lavfi.cropdetect.w={x2 - x1 + 1}\nlavfi.cropdetect.h={y2 - y1 + 1}\n"
            + $"lavfi.cropdetect.x={x1}\nlavfi.cropdetect.y={y1}\nlavfi.cropdetect.limit=0.100000\n");

    // One line of the crop branch for each frame it looked at (the whole picture, or the box given), and the cut
    // branch's line if there was a scene cut.
    private static ClipFiles ClipText(
        int samples = ClipSamples,
        bool cut = false,
        (int X1, int X2, int Y1, int Y2)? box = null,
        int width = 1280,
        int height = 720)
    {
        var (x1, x2, y1, y2) = box ?? (0, width - 1, 0, height - 1);
        var crop = string.Concat(Enumerable.Range(0, samples).Select(i => CropFrame(i, x1, x2, y1, y2)));
        return new ClipFiles(crop, cut ? "frame:75   pts:38400   pts_time:3\nlavfi.scd.time=3\n" : string.Empty);
    }

    // What Jellyfin's probe says of the video stream.
    private MediaStream? _probe = new() { Type = MediaStreamType.Video, Width = 1280, Height = 720 };

    internal static SKBitmap Bitmap(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Black);
        return bitmap;
    }

    // A frame of noise around mid gray: the higher the quality, the more contrast, colour and sharpness it has, so the
    // score grows with it. 0 is a flat gray frame, which is under the floor; 1 is the best a frame can be.
    internal static SKBitmap Frame(double quality, int seed = 1)
    {
        var random = new Random(seed);
        var bitmap = Bitmap(CopyWidth, CopyHeight);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                byte Channel() => (byte)Math.Clamp(128 + ((random.NextDouble() - 0.5) * 255 * quality), 0, 255);
                bitmap.SetPixel(x, y, new SKColor(Channel(), Channel(), Channel()));
            }
        }

        return bitmap;
    }

    private static double Score(double quality, int seed = 1)
    {
        using var frame = Frame(quality, seed);
        return FrameScorer.Score(frame);
    }

    private Video MakeVideo(double runtimeSeconds = 1000, bool exists = true)
    {
        var path = Path("video.mkv");
        if (exists)
        {
            File.WriteAllBytes(path, [0]);
        }

        return new Video
        {
            Id = Guid.NewGuid(),
            Path = path,
            RunTimeTicks = (long)(runtimeSeconds * TimeSpan.TicksPerSecond),
        };
    }

    private SceneFinder Finder(RecordingLogger? log = null, string? ffmpegPath = null)
    {
        var encoder = DispatchProxy.Create<IMediaEncoder, FfmpegRunnerTests.EncoderStub>();
        ((FfmpegRunnerTests.EncoderStub)(object)encoder).EncoderPath = ffmpegPath ?? _ffmpegPath;
        var runner = new FfmpegRunner(encoder, NullLogger<FfmpegRunner>.Instance);
        return new SceneFinder(
            Stub<ITrickplayManager>(_trickplay),
            Stub<IMediaSegmentManager>(_segments),
            Stub<IChapterManager>(_chapters),
            Stub<ILibraryManager>(_library),
            runner,
            log ?? (ILogger<SceneFinder>)NullLogger<SceneFinder>.Instance,
            _root)
        {
            ProbeVideo = _ => _probe,
        };
    }

    // The calls the fake ffmpeg has had, in order: ("frame" or "clip", the -ss it was given).
    private List<(string Mode, double Start)> Calls()
    {
        var file = Path("calls.log");
        return File.Exists(file)
            ? File.ReadAllLines(file)
                .Select(line => line.Split(' '))
                .Select(parts => (parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture)))
                .ToList()
            : new List<(string, double)>();
    }

    private List<double> ClipStarts() => Calls().Where(call => call.Mode == "clip").Select(call => call.Start).ToList();

    private List<double> FrameTimes() =>
        Calls().Where(call => call.Mode == "frame").Select(call => call.Start).ToList();

    private void SetChapters(params (double Start, SKBitmap? Image)[] chapters)
    {
        var list = new List<ChapterInfo>();
        for (var i = 0; i < chapters.Length; i++)
        {
            string? imagePath = null;
            if (chapters[i].Image is { } image)
            {
                imagePath = Path($"chapter-{i}.png");
                File.WriteAllBytes(imagePath, Png(image));
            }

            list.Add(new ChapterInfo
            {
                StartPositionTicks = (long)(chapters[i].Start * TimeSpan.TicksPerSecond),
                Name = $"Chapter {i}",
                ImagePath = imagePath!,
            });
        }

        _chapters["GetChapters"] = _ => (IReadOnlyList<ChapterInfo>)list;
    }

    // A trickplay of thumbnails 160x90 in tiles of 3 by 2, every 10 s, with the quality of each thumbnail given.
    private List<(int Width, int Index, bool SaveWithMedia)> SetTrickplay(
        double[] qualities,
        int tileColumns = 3,
        int tileRows = 2,
        int intervalMilliseconds = 10_000,
        bool shortLastTile = false)
    {
        var info = new TrickplayInfo
        {
            Width = CopyWidth,
            Height = CopyHeight,
            TileWidth = tileColumns,
            TileHeight = tileRows,
            ThumbnailCount = qualities.Length,
            Interval = intervalMilliseconds,
        };
        var perTile = tileColumns * tileRows;
        var tiles = (qualities.Length + perTile - 1) / perTile;
        for (var tile = 0; tile < tiles; tile++)
        {
            var first = tile * perTile;
            var last = Math.Min(first + perTile, qualities.Length);
            var rows = shortLastTile && tile == tiles - 1 ? 1 : tileRows;
            using var sheet = Bitmap(tileColumns * CopyWidth, rows * CopyHeight);
            using (var canvas = new SKCanvas(sheet))
            {
                for (var i = first; i < last; i++)
                {
                    var cell = i - first;
                    if (cell / tileColumns >= rows)
                    {
                        continue;
                    }

                    using var thumbnail = Frame(qualities[i], seed: i + 1);
                    canvas.DrawBitmap(thumbnail, (cell % tileColumns) * CopyWidth, (cell / tileColumns) * CopyHeight);
                }
            }

            File.WriteAllBytes(Path($"tile-{CopyWidth}-{tile}.png"), Png(sheet));
        }

        var asked = new List<(int, int, bool)>();
        _trickplay["GetTrickplayResolutions"] = _ => Task.FromResult(new Dictionary<int, TrickplayInfo>
        {
            [320] = new TrickplayInfo
            {
                Width = 320,
                Height = 180,
                TileWidth = 1,
                TileHeight = 1,
                ThumbnailCount = 1,
                Interval = 1000,
            },
            [CopyWidth] = info,
        });
        _trickplay["GetTrickplayTilePathAsync"] = args =>
        {
            asked.Add(((int)args[1]!, (int)args[2]!, (bool)args[3]!));
            return Task.FromResult(Path($"tile-{(int)args[1]!}-{(int)args[2]!}.png"));
        };
        return asked;
    }

    // ---- candidate sources ----

    [LinuxFact]
    public async Task Trickplay_thumbnails_are_the_first_source_and_the_best_five_are_tried_best_first()
    {
        // 120 s: the window is 12 s to 108 s, a clip starts at 102 s at the latest. Thumbnail i is at 10 s x i.
        double[] qualities = [0.9, 0.8, 0.2, 0.5, 0.95, 0.6, 0.7, 0.4, 0.3, 0.85];
        SetTrickplay(qualities);
        SetClip(null, ClipText(cut: true));

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(choice);
        var expected = Enumerable.Range(0, qualities.Length)
            .Where(i => i * 10.0 >= 12 && i * 10.0 <= 102)
            .Select(i => (Time: i * 10.0, Score: Score(qualities[i], seed: i + 1)))
            .Where(c => c.Score >= FrameScorer.Floor)
            .OrderByDescending(c => c.Score)
            .Take(5)
            .Select(c => c.Time)
            .ToList();
        Assert.Equal(5, expected.Count);
        Assert.Equal(expected, ClipStarts());

        // The thumbnails were enough: no frame was read from the video.
        Assert.Empty(FrameTimes());
    }

    [LinuxFact]
    public async Task Trickplay_uses_the_smallest_width_and_the_libraries_save_with_media_setting()
    {
        var asked = SetTrickplay([0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9]);
        _library["GetLibraryOptions"] = _ => new LibraryOptions { SaveTrickplayWithMedia = true };

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        // Both tiles hold an allowed thumbnail, and 160 is smaller than 320.
        Assert.Equal(new[] { (CopyWidth, 0, true), (CopyWidth, 1, true) }, asked);
    }

    [LinuxFact]
    public async Task Trickplay_thumbnails_are_cut_from_their_cell_of_the_tile()
    {
        // Only thumbnail 7 (second row, first column of the second tile) is good; a wrong cell finds a flat one.
        double[] qualities = [0, 0, 0, 0, 0, 0, 0, 0.9, 0, 0];
        SetTrickplay(qualities);
        SetClip(null, ClipText(cut: true));

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new[] { 70.0 }, ClipStarts());
    }

    [LinuxFact]
    public async Task A_tile_that_is_shorter_than_its_grid_is_read_as_far_as_it_goes()
    {
        // The second tile has one row of three, so thumbnail 9 has no pixels; 6 and 8 are read.
        double[] qualities = [0, 0, 0, 0, 0, 0, 0.8, 0, 0.9, 0.95];
        SetTrickplay(qualities, shortLastTile: true);
        SetClip(null, ClipText(cut: true));

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new[] { 80.0, 60.0 }, ClipStarts());
    }

    [LinuxFact]
    public async Task A_tile_with_no_allowed_thumbnail_is_not_asked_for()
    {
        // 1000 s: the window is 100 s to 900 s (latest start 894). Ten thumbnails 10 s apart end at 90 s.
        var asked = SetTrickplay([0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9]);

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 1000), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Empty(asked);
    }

    [LinuxFact]
    public async Task A_tile_that_is_missing_or_unreadable_is_skipped()
    {
        SetTrickplay([0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9]);
        File.Delete(Path("tile-160-0.png"));
        File.WriteAllText(Path("tile-160-1.png"), "this is not a picture");

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(choice);
    }

    [LinuxFact]
    public async Task Chapters_are_used_when_there_is_no_trickplay()
    {
        using var good = Frame(0.9, seed: 3);
        SetChapters((100, good), (200, null), (300, null));
        SetFrame(200, Frame(0.7, seed: 4));
        SetFrame(300, Frame(0.0, seed: 5));
        SetClip(null, ClipText(cut: true));

        await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        // A chapter with a picture is read from it, the others from the video; the flat one is under the floor.
        Assert.Equal(new[] { 200.0, 300.0 }, FrameTimes());
        Assert.Equal(new[] { 100.0, 200.0 }, ClipStarts());
    }

    [LinuxFact]
    public async Task Without_trickplay_or_chapters_24_evenly_spaced_frames_are_read()
    {
        // 1000 s with nothing excluded: the window allows 100 s to 894 s.
        await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        var times = FrameTimes();
        Assert.Equal(24, times.Count);
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.All(times, t => Assert.InRange(t, 100, 894));
        var steps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.All(steps, step => Assert.Equal(steps[0], step, tolerance: 0.01));
    }

    [LinuxFact]
    public async Task The_frames_are_spread_over_what_is_allowed_when_a_segment_takes_most_of_the_video()
    {
        // Everything but 700 s to 800 s is an intro: all 24 frames must fall in that stretch.
        _segments["GetSegmentsAsync"] = _ => Task.FromResult<IEnumerable<MediaSegmentDto>>(
        [
            Segment(MediaSegmentType.Intro, 0, 700),
            Segment(MediaSegmentType.Outro, 800, 1000),
        ]);

        await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        var times = FrameTimes();
        Assert.Equal(24, times.Count);
        Assert.All(times, t => Assert.InRange(t, 700, 794));
    }

    [LinuxFact]
    public async Task A_source_that_gives_nothing_above_the_floor_hands_over_to_the_next()
    {
        SetTrickplay([0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        using var good = Frame(0.9, seed: 8);
        SetChapters((50, good));

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new[] { 50.0 }, ClipStarts());
        Assert.Empty(FrameTimes());
    }

    [LinuxFact]
    public async Task A_source_that_throws_hands_over_to_the_next_and_is_logged()
    {
        var log = new RecordingLogger();
        _trickplay["GetTrickplayResolutions"] = _ => throw new InvalidOperationException("the database is locked");
        using var good = Frame(0.9, seed: 8);
        SetChapters((50, good));

        var choice = await Finder(log).FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(50.0, Some(choice).StartSeconds);
        Assert.Contains(
            log.Messages,
            message => message.StartsWith("Warning:", StringComparison.Ordinal)
                && message.Contains("database is locked", StringComparison.Ordinal));
    }

    // ---- the window ----

    private static MediaSegmentDto Segment(MediaSegmentType type, double start, double end) =>
        new()
        {
            Type = type,
            StartTicks = (long)(start * TimeSpan.TicksPerSecond),
            EndTicks = (long)(end * TimeSpan.TicksPerSecond),
        };

    private static Task<IEnumerable<MediaSegmentDto>> Segments(params MediaSegmentDto[] segments) =>
        Task.FromResult<IEnumerable<MediaSegmentDto>>(segments);

    [LinuxFact]
    public async Task Only_times_with_Start_up_to_t_up_to_End_of_an_allowed_piece_are_candidates()
    {
        // 1000 s, clip 6: the window is 100 to 900, so the latest start is 894: a chapter at 894 is in, at 894.5 out.
        // The intro 300 to 400 leaves the pieces 100 to 294 and 400 to 894; 294 is in, 294.5 and 299 are out.
        _segments["GetSegmentsAsync"] = _ => Segments(Segment(MediaSegmentType.Intro, 300, 400));
        SetChapters(
            (99.5, null),
            (100, null),
            (294, null),
            (294.5, null),
            (299, null),
            (350, null),
            (400, null),
            (894, null),
            (894.5, null));
        for (var i = 0; i < 9; i++)
        {
            double[] at = [99.5, 100, 294, 294.5, 299, 350, 400, 894, 894.5];
            SetFrame(at[i], Frame(0.9, seed: i + 1));
        }

        await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Equal(new[] { 100.0, 294.0, 400.0, 894.0 }.OrderBy(t => t), FrameTimes().OrderBy(t => t));
    }

    [LinuxFact]
    public async Task The_five_segment_kinds_are_asked_for_with_the_library_options_of_the_video()
    {
        IEnumerable<MediaSegmentType>? kinds = null;
        LibraryOptions? passedOptions = null;
        var options = new LibraryOptions();
        bool? filtered = null;
        _library["GetLibraryOptions"] = _ => options;
        _segments["GetSegmentsAsync"] = args =>
        {
            kinds = (IEnumerable<MediaSegmentType>)args[1]!;
            passedOptions = (LibraryOptions)args[2]!;
            filtered = (bool)args[3]!;
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
        };

        await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                MediaSegmentType.Intro,
                MediaSegmentType.Outro,
                MediaSegmentType.Recap,
                MediaSegmentType.Preview,
                MediaSegmentType.Commercial,
            }.OrderBy(k => k),
            Some(kinds).OrderBy(k => k));
        Assert.Same(options, passedOptions);
        Assert.True(filtered);
    }

    // ---- the clip check ----

    [LinuxFact]
    public async Task The_first_candidate_whose_clip_has_no_cut_wins_and_later_ones_are_not_tried()
    {
        double[] qualities = [0.9, 0.8, 0.2, 0.5, 0.95, 0.6, 0.7, 0.4, 0.3, 0.85];
        SetTrickplay(qualities);
        var ranked = Enumerable.Range(2, 8)
            .Select(i => (Time: i * 10.0, Score: Score(qualities[i], seed: i + 1)))
            .Where(c => c.Score >= FrameScorer.Floor)
            .OrderByDescending(c => c.Score)
            .Select(c => c.Time)
            .ToList();
        SetClip(ranked[0], ClipText(cut: true));
        SetClip(ranked[1], ClipText(cut: true));

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(ranked[2], Some(choice).StartSeconds);
        Assert.Equal(ranked.Take(3), ClipStarts());
    }

    [LinuxFact]
    public async Task No_cut_free_clip_among_the_best_five_means_no_choice()
    {
        SetTrickplay([0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9]);
        SetClip(null, ClipText(cut: true));
        var log = new RecordingLogger();

        var choice = await Finder(log).FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Equal(5, ClipStarts().Count);
    }

    [LinuxFact]
    public async Task A_clip_with_too_few_frames_is_unreadable_and_the_next_candidate_is_tried()
    {
        double[] qualities = [0, 0, 0.9, 0.8, 0, 0, 0, 0, 0, 0];
        SetTrickplay(qualities);

        // 6 s sampled 3 times a second is 18 frames; 17 pass (a frame of rounding at the cut), 16 do not.
        SetClip(20, ClipText(samples: 16));
        SetClip(30, ClipText(samples: 17));

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(30.0, Some(choice).StartSeconds);
        Assert.Equal(new[] { 20.0, 30.0 }, ClipStarts());
    }

    [LinuxFact]
    public async Task A_clip_check_that_fails_moves_on_to_the_next_candidate()
    {
        SetTrickplay([0, 0, 0.9, 0.8, 0, 0, 0, 0, 0, 0]);
        SetClip(20, ClipText(), exit: 1);

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(30.0, Some(choice).StartSeconds);
    }

    [LinuxFact]
    public async Task Hundreds_of_lines_of_ffmpeg_messages_do_not_make_a_clean_clip_a_cut()
    {
        // The fake prints 600 chapter lines for every clip check, as a remux with many chapters does.
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(20.0, Some(choice).StartSeconds);
    }

    [LinuxFact]
    public async Task The_result_files_are_deleted_after_every_check_even_a_failed_one()
    {
        SetTrickplay([0, 0, 0.9, 0.8, 0, 0, 0, 0, 0, 0]);
        SetClip(20, ClipText(), exit: 1);

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_root, "scene-finder-*"));
    }

    [LinuxFact]
    public async Task The_clip_check_is_one_pass_with_the_thread_limit_before_the_input_and_cropdetect_before_scaling()
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);

        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        var args = File.ReadAllLines(Path("last-clip.args")).ToList();
        var input = args.IndexOf("-i");
        Assert.Equal("20", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("6", args[args.IndexOf("-t") + 1]);
        Assert.True(args.IndexOf("-ss") < input && args.IndexOf("-t") < input);
        Assert.Equal(Path("video.mkv"), args[input + 1]);
        Assert.Equal(new[] { "-threads", "2" }, args.Skip(args.IndexOf("-ss") - 2).Take(2));
        Assert.Equal(new[] { "-f", "null", "-" }, args.TakeLast(3));

        var graph = args[args.IndexOf("-filter_complex") + 1];
        var files = System.Text.RegularExpressions.Regex.Matches(graph, @"file=([^,;\[]*)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(
            "[0:0]split=2[c][s];[c]fps=3,cropdetect=limit=0.1:round=2:skip=0:reset=0,"
            + $"metadata=mode=print:file={files[0]},nullsink;"
            + "[s]scale=320:-2:reset_sar=1,scdet=threshold=10,"
            + $"metadata=mode=print:key=lavfi.scd.time:file={files[1]}[o]",
            graph);
        Assert.StartsWith(_root, files[0], StringComparison.Ordinal);
        Assert.EndsWith("-crop.txt", files[0], StringComparison.Ordinal);
        Assert.EndsWith("-cut.txt", files[1], StringComparison.Ordinal);
        Assert.Contains("-nostats", args);
        Assert.Equal("error", args[args.LastIndexOf("-loglevel") + 1]);
        Assert.Equal("[o]", args[args.IndexOf("-map") + 1]);
    }

    // ---- the crop ----

    [LinuxTheory]
    [InlineData(1280, 720, 0, 1279, 90, 629, 0, 90, 1280, 540)] // letterbox: 25% of the height
    [InlineData(1280, 720, 160, 1119, 0, 719, 160, 0, 960, 720)] // pillarbox: 25% of the width
    [InlineData(1920, 1080, 0, 1919, 140, 939, 0, 140, 1920, 800)] // 2.40:1 in a 16:9 frame
    [InlineData(1920, 1080, 240, 1679, 135, 944, 240, 134, 1440, 812)] // all four sides; odd edges go out to even
    public async Task Burned_in_bars_are_returned_as_a_crop(
        int w, int h, int x1, int x2, int y1, int y2, int x, int y, int cw, int ch)
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (x1, x2, y1, y2), width: w, height: h));
        _probe = new MediaStream { Width = w, Height = h };

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        var crop = Assert.NotNull(Some(choice).Crop);
        Assert.Equal(new CropRect(x, y, cw, ch), crop);
    }

    [LinuxTheory]
    [InlineData(0, 1279, 0, 719)] // no bars
    [InlineData(0, 1279, 14, 705)] // 1.9% of the height each side... 28 rows in all: 3.9% of 720
    [InlineData(10, 1269, 0, 719)] // 20 columns in all: 1.6% of 1280
    public async Task Bars_that_take_less_than_4_percent_are_noise_and_give_no_crop(int x1, int x2, int y1, int y2)
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (x1, x2, y1, y2)));

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(Some(choice).Crop);
    }

    [LinuxFact]
    public async Task Bars_of_exactly_4_percent_are_cut()
    {
        // 1000 x 1000: 40 rows are 4% of the height.
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (0, 999, 20, 979), width: 1000, height: 1000));
        _probe = new MediaStream { Width = 1000, Height = 1000 };

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(0, 20, 1000, 960), Assert.NotNull(Some(choice).Crop));
    }

    [LinuxTheory]
    [InlineData(600, 700, 300, 400)] // the lit part is a patch: it is not a picture worth cutting to
    [InlineData(0, 1279, 350, 369)] // a few rows
    public async Task A_crop_that_would_leave_too_little_of_the_picture_gives_no_crop(int x1, int x2, int y1, int y2)
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (x1, x2, y1, y2)));

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(Some(choice).Crop);
    }

    [LinuxFact]
    public async Task The_crop_is_the_largest_picture_seen_over_the_whole_clip()
    {
        // A dark frame has a small box and a bright one a large box: the crop is what contains them all.
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        var files = ClipText(box: (0, 1279, 200, 519));
        var more = CropFrame(5, 0, 1279, 90, 629) + CropFrame(6, 0, 1279, 300, 419);
        SetClip(null, files with { Crop = files.Crop + more });

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(0, 90, 1280, 540), Assert.NotNull(Some(choice).Crop));
    }

    [LinuxTheory]
    [InlineData(null, null)]
    [InlineData(1280, null)]
    [InlineData(0, 720)]
    public async Task A_picture_whose_size_is_not_known_gives_no_crop(int? width, int? height)
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (0, 1279, 90, 629)));
        _probe = new MediaStream { Width = width, Height = height };

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(Some(choice).Crop);
    }

    [LinuxFact]
    public async Task A_video_that_is_not_probed_gives_no_crop_and_a_probe_that_throws_does_too()
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (0, 1279, 90, 629)));
        _probe = null;

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(Some(choice).Crop);
        var finder = Finder();
        finder.ProbeVideo = _ => throw new InvalidOperationException("no media source manager");
        var throwing = await finder.FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);
        Assert.Null(Some(throwing).Crop);
    }

    [LinuxTheory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(270)]
    public async Task A_video_turned_a_quarter_is_measured_with_its_width_and_height_swapped(int rotation)
    {
        // Stored 640x360 and turned: ffmpeg's filters see 360x640. The whole picture is lit, so there is no crop;
        // read as 640x360 the box would be cut to the top square.
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (0, 359, 0, 639)));
        _probe = new MediaStream { Width = 640, Height = 360, Rotation = rotation };

        var turned = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(Some(turned).Crop);

        // Bars on the left and right of the turned picture are 40 columns each.
        SetClip(null, ClipText(box: (40, 319, 0, 639)));
        var barred = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(40, 0, 280, 640), Assert.NotNull(Some(barred).Crop));
    }

    [LinuxFact]
    public async Task A_rotation_of_180_does_not_swap()
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        SetClip(null, ClipText(box: (0, 639, 45, 314), width: 640, height: 360));
        _probe = new MediaStream { Width = 640, Height = 360, Rotation = 180 };

        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(0, 44, 640, 272), Assert.NotNull(Some(choice).Crop));
    }

    [LinuxFact]
    public void By_default_the_probe_is_the_default_video_stream_Jellyfin_knows()
    {
        // Video.GetDefaultVideoStream asks the media source manager that BaseItem keeps in a static property.
        var stream = new MediaStream { Type = MediaStreamType.Video, Index = 0, Width = 1920, Height = 1080 };
        var manager = Stub<IMediaSourceManager>(new()
        {
            ["GetMediaStreams"] = _ => (IReadOnlyList<MediaStream>)new[] { stream },
        });
        var previous = BaseItem.MediaSourceManager;
        BaseItem.MediaSourceManager = manager;
        try
        {
            var video = MakeVideo();
            video.DefaultVideoStreamIndex = 0;
            var finder = new SceneFinder(
                Stub<ITrickplayManager>(new()),
                Stub<IMediaSegmentManager>(new()),
                Stub<IChapterManager>(new()),
                Stub<ILibraryManager>(new()),
                new FfmpegRunner(
                    DispatchProxy.Create<IMediaEncoder, FfmpegRunnerTests.EncoderStub>(),
                    NullLogger<FfmpegRunner>.Instance),
                NullLogger<SceneFinder>.Instance);

            Assert.Same(stream, finder.ProbeVideo(video));
        }
        finally
        {
            BaseItem.MediaSourceManager = previous;
        }
    }

    [LinuxFact]
    public async Task The_graph_reads_the_stream_that_the_size_came_from()
    {
        SetTrickplay([0, 0, 0.9, 0, 0, 0, 0, 0, 0, 0]);
        _probe = new MediaStream { Index = 2, Width = 1280, Height = 720 };
        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);
        Assert.StartsWith("[0:2]split=2", ClipGraph());

        // Nothing probed: the first video stream, and no crop.
        _probe = null;
        await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);
        Assert.StartsWith("[0:v:0]split=2", ClipGraph());
    }

    private string ClipGraph()
    {
        var args = File.ReadAllLines(Path("last-clip.args")).ToList();
        return args[args.IndexOf("-filter_complex") + 1];
    }

    [LinuxFact]
    public async Task A_result_file_that_cannot_be_read_makes_the_clip_unreadable_and_not_an_error()
    {
        if (Environment.UserName == "root")
        {
            return; // root reads files whatever their mode
        }

        File.WriteAllText(Path("unreadable"), string.Empty);
        SetTrickplay([0, 0, 0.9, 0.8, 0, 0, 0, 0, 0, 0]);
        var log = new RecordingLogger();

        var choice = await Finder(log).FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Equal(2, ClipStarts().Count);
        Assert.Contains(log.Messages, message => message.Contains("unreadable", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_root, "scene-finder-*"));
    }

    // ---- never throws for a bad title ----

    [LinuxFact]
    public async Task A_missing_file_is_no_choice()
    {
        var log = new RecordingLogger();

        var choice = await Finder(log).FindStartAsync(MakeVideo(exists: false), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Empty(Calls());
        Assert.Contains(log.Messages, message => message.StartsWith("Debug:", StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task A_video_with_no_path_or_no_length_is_no_choice()
    {
        var noPath = MakeVideo();
        noPath.Path = null!;
        var noLength = MakeVideo();
        noLength.RunTimeTicks = null;

        Assert.Null(await Finder().FindStartAsync(noPath, Clip, CancellationToken.None));
        Assert.Null(await Finder().FindStartAsync(noLength, Clip, CancellationToken.None));
        Assert.Empty(Calls());
    }

    [LinuxFact]
    public async Task A_video_shorter_than_its_window_is_no_choice()
    {
        // 7 s: the window is 0.7 s to 6.3 s, shorter than the 6 s clip.
        Assert.Null(await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 7), Clip, CancellationToken.None));
        Assert.Empty(Calls());
    }

    [LinuxFact]
    public async Task A_failure_to_read_the_segments_is_no_choice_and_a_warning()
    {
        var log = new RecordingLogger();
        _segments["GetSegmentsAsync"] = _ => throw new InvalidOperationException("no providers");

        var choice = await Finder(log).FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Empty(Calls());
        Assert.Contains(log.Messages, message => message.StartsWith("Warning:", StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task Frames_that_cannot_be_read_are_no_choice()
    {
        // The fake has no frame files, so every frame request fails the way an ffmpeg that cannot decode would.
        var choice = await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Equal(24, FrameTimes().Count);
        Assert.Empty(ClipStarts());
    }

    [LinuxFact]
    public async Task A_picture_that_is_not_a_picture_is_skipped()
    {
        File.WriteAllText(Path("frame-100.png"), "not a png");

        var choice = await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Null(choice);
    }

    [LinuxFact]
    public async Task No_ffmpeg_is_no_choice()
    {
        using var good = Frame(0.9, seed: 2);
        SetChapters((200, good));

        var choice = await Finder(ffmpegPath: string.Empty).FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Null(choice);
    }

    [LinuxFact]
    public async Task A_clip_check_that_does_not_complete_ends_the_search()
    {
        // A clip check that is stopped (stalled or over budget) means the file hangs; the other candidates would too.
        using var good = Frame(0.9, seed: 2);
        SetChapters((200, good), (300, good), (400, good));

        var choice = await Finder(ffmpegPath: string.Empty).FindStartAsync(MakeVideo(), Clip, CancellationToken.None);

        Assert.Null(choice);
        Assert.Empty(Calls());
    }

    [LinuxFact]
    public async Task Cancellation_propagates_and_is_not_swallowed()
    {
        using var source = new CancellationTokenSource();
        _chapters["GetChapters"] = _ =>
        {
            source.Cancel();
            return (IReadOnlyList<ChapterInfo>)new[]
            {
                new ChapterInfo { StartPositionTicks = 200L * TimeSpan.TicksPerSecond },
            };
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Finder().FindStartAsync(MakeVideo(), Clip, source.Token));
    }

    [LinuxFact]
    public async Task A_cancellation_that_is_not_the_callers_is_a_failure_of_the_title_and_not_a_cancellation()
    {
        // A manager that times out on its own throws OperationCanceledException; the caller's token is not cancelled.
        _segments["GetSegmentsAsync"] = _ => throw new OperationCanceledException("the segment provider timed out");
        Assert.Null(await Finder().FindStartAsync(MakeVideo(), Clip, CancellationToken.None));

        _segments["GetSegmentsAsync"] = _ => Segments();
        _trickplay["GetTrickplayResolutions"] = _ => throw new TaskCanceledException("the database timed out");
        using var good = Frame(0.9, seed: 8);
        SetChapters((50, good));
        var choice = await Finder().FindStartAsync(MakeVideo(runtimeSeconds: 120), Clip, CancellationToken.None);

        Assert.Equal(50.0, Some(choice).StartSeconds);
    }

    [LinuxFact]
    public async Task A_token_that_is_cancelled_already_throws_before_anything_runs()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Finder().FindStartAsync(MakeVideo(), Clip, source.Token));
        Assert.Empty(Calls());
    }

    [LinuxTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public async Task A_clip_that_is_not_a_length_is_refused(double clip) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Finder().FindStartAsync(MakeVideo(), clip, CancellationToken.None));
}

/// <summary>Whether Docker and the Jellyfin image with jellyfin-ffmpeg are here; the real tests need both.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    internal const string Image = "jellyfin/jellyfin:12.2";

    private static readonly Lazy<string?> Reason = new(Probe);

    public DockerFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs jellyfin-ffmpeg through Docker, so it runs on Linux only.";
        }
        else if (Unavailable is { } reason && !Required)
        {
            Skip = reason;
        }
    }

    /// <summary>
    /// True when LCM_REQUIRE_FFMPEG_TESTS=1. Then a missing Docker or image does not skip the real-ffmpeg tests but
    /// fails them (the fixture throws), so that a CI job that is meant to run them cannot go green without doing so.
    /// </summary>
    internal static bool Required => Environment.GetEnvironmentVariable("LCM_REQUIRE_FFMPEG_TESTS") == "1";

    /// <summary>Why the real tests cannot run here, or null when Docker and the image are there.</summary>
    internal static string? Unavailable => Reason.Value;

    private static string? Probe()
    {
        try
        {
            var info = new ProcessStartInfo("docker", ["image", "inspect", Image, "--format", "{{.Id}}"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            process!.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"The image {Image} is not available locally.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Docker is not installed.";
        }
    }
}

/// <summary>
/// The scene finder and the renderer against the real jellyfin-ffmpeg (the image jellyfin/jellyfin:12.2, run locally
/// with <c>docker run --rm</c>): clips made on the spot with known bars and known cuts, so that what the finder says
/// about them can be told from what is true, and a cover rendered with the crop shows no bars.
/// </summary>
public sealed class SceneFinderFfmpegTests : IClassFixture<SceneFinderFfmpegTests.Clips>
{
    private const double Clip = 4;

    private readonly Clips _clips;

    public SceneFinderFfmpegTests(Clips clips) => _clips = clips;

    /// <summary>The clips, made once by the real ffmpeg, and the runner that goes through Docker.</summary>
    public sealed class Clips : IAsyncLifetime
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("scene-finder-ffmpeg-tests-").FullName;

        public FfmpegRunner Runner { get; private set; } = null!;

        public string Path(string name) => System.IO.Path.Combine(Root, name);

        public Task InitializeAsync() =>
            OperatingSystem.IsLinux() ? MakeClipsAsync() : Task.CompletedTask;

        private async Task MakeClipsAsync()
        {
            if (DockerFactAttribute.Unavailable is { } reason)
            {
                // Without the variable the tests are skipped; with it, a missing image must not pass unnoticed.
                if (DockerFactAttribute.Required)
                {
                    throw new InvalidOperationException($"LCM_REQUIRE_FFMPEG_TESTS=1 but {reason}");
                }

                return;
            }

            var script = Path("jellyfin-ffmpeg.sh");
            File.WriteAllText(
                script,
                "#!/bin/sh\nexec docker run --rm --name lcm-06b-scene-$$ --user \"$(id -u):$(id -g)\" "
                + $"-v '{Root}:{Root}' --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "
                + $"{DockerFactAttribute.Image} \"$@\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var encoder = DispatchProxy.Create<IMediaEncoder, FfmpegRunnerTests.EncoderStub>();
            ((FfmpegRunnerTests.EncoderStub)(object)encoder).EncoderPath = script;
            Runner = new FfmpegRunner(encoder, NullLogger<FfmpegRunner>.Instance);

            const string Source = "testsrc2=size=1280x720:rate=25:duration=24";
            const string Bars = "drawbox=y=0:w=iw:h=90:color=black:t=fill,drawbox=y=630:w=iw:h=90:color=black:t=fill";
            const string Columns =
                "drawbox=x=0:w=160:h=ih:color=black:t=fill,drawbox=x=1120:w=160:h=ih:color=black:t=fill";
            string[] h264 = ["-c:v", "libx264", "-pix_fmt", "yuv420p"];
            await Make("clean.mp4", ["-f", "lavfi", "-i", Source, .. h264]);
            await Make("letter.mp4", ["-f", "lavfi", "-i", Source, "-vf", Bars, .. h264]);
            await Make("pillar.mp4", ["-f", "lavfi", "-i", Source, "-vf", Columns, .. h264]);
            await Make(
                "letter-hdr.mkv",
                "-f", "lavfi", "-i", Source, "-vf", Bars + ",format=yuv420p10le", "-c:v", "libx265",
                "-x265-params", "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc",
                "-pix_fmt", "yuv420p10le");

            // One cut, at 6 s. And a cut every 3 s, so that no clip of 4 s is free of one.
            const string Other = "mandelbrot=size=1280x720:rate=25";
            await Make(
                "cut-once.mp4", "-f", "lavfi", "-i", Source, "-f", "lavfi", "-i", Other,
                "-filter_complex", "[0:v][1:v]blend=all_expr='if(lt(T,6),A,B)'[o]", "-map", "[o]", "-t", "24",
                "-c:v", "libx264", "-pix_fmt", "yuv420p");
            await Make(
                "cut-always.mp4", "-f", "lavfi", "-i", Source, "-f", "lavfi", "-i", Other,
                "-filter_complex", "[0:v][1:v]blend=all_expr='if(lt(mod(T,6),3),A,B)'[o]", "-map", "[o]", "-t", "24",
                "-c:v", "libx264", "-pix_fmt", "yuv420p");

            // A remux as a library has them: 40 chapters and 15 tagged streams; the header alone is hundreds of lines.
            var meta = new StringBuilder(";FFMETADATA1\ntitle=Film\n");
            for (var chapter = 0; chapter < 40; chapter++)
            {
                var start = chapter * 600;
                meta.Append(
                    CultureInfo.InvariantCulture,
                    $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={start + 599}\ntitle=Chapter {chapter}\n");
            }

            File.WriteAllText(Path("chapters.ffmeta"), meta.ToString());
            var remux = new List<string> { "-f", "lavfi", "-i", Source };
            for (var track = 0; track < 14; track++)
            {
                remux.AddRange(["-f", "lavfi", "-i", $"sine=frequency={220 + (track * 20)}:duration=24"]);
            }

            remux.AddRange(["-i", Path("chapters.ffmeta"), "-map", "0:v", "-vf", Bars]);
            for (var track = 1; track <= 14; track++)
            {
                remux.AddRange(["-map", $"{track}:a"]);
            }

            remux.AddRange(["-map_metadata", "15", "-map_chapters", "15", "-t", "24"]);
            remux.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
            for (var track = 0; track < 14; track++)
            {
                remux.AddRange([$"-metadata:s:a:{track}", $"title=Track number {track}"]);
                remux.AddRange([$"-metadata:s:a:{track}", "language=eng"]);
            }

            remux.AddRange(["-metadata:s:v:0", "title=Main video", "-metadata:s:v:0", "language=eng"]);
            await Make("remux.mkv", [.. remux]);

            // The same pictures turned a quarter by the display matrix: stored 1280x720, shown 720x1280.
            await Make("rotated-clean.mp4", ["-display_rotation", "90", "-i", Path("clean.mp4"), "-c", "copy"]);
            await Make("rotated-letter.mp4", ["-display_rotation", "90", "-i", Path("letter.mp4"), "-c", "copy"]);

            // Ten seconds of video that the library believes last 24: a clip that runs past the end.
            await Make("short.mp4", ["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=10", .. h264]);
        }

        public Task DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return Task.CompletedTask;
        }

        private async Task Make(string name, params string[] args)
        {
            // An encode on a small runner can go minutes without a line of output (the log level is error) or a
            // sign of life that the runner can see (it watches the docker client, not ffmpeg in the container).
            var all = new List<string>(args) { Path(name) };
            var (exit, errors) = await Runner.RunAsync(all, TimeSpan.FromMinutes(20), CancellationToken.None);
            Assert.True(exit == 0, $"Making {name} failed with exit code {exit}: {errors}");
        }
    }

    // A chapter at each given second, with a good picture, so that the finder tries exactly those moments.
    private SceneFinder Finder(params (double Start, double Quality)[] chapters) => FinderWith(null, null, chapters);

    private SceneFinder FinderWith(
        MediaStream? probe,
        string? scratch,
        params (double Start, double Quality)[] chapters)
    {
        var list = new List<ChapterInfo>();
        for (var i = 0; i < chapters.Length; i++)
        {
            var image = _clips.Path($"chapter-{Guid.NewGuid():N}.png");
            using var frame = SceneFinderTests.Frame(chapters[i].Quality, seed: i + 1);
            File.WriteAllBytes(image, SceneFinderTests.Png(frame));
            list.Add(new ChapterInfo
            {
                StartPositionTicks = (long)(chapters[i].Start * TimeSpan.TicksPerSecond),
                ImagePath = image,
            });
        }

        return new SceneFinder(
            SceneFinderTests.Stub<ITrickplayManager>(
                new() { ["GetTrickplayResolutions"] = _ => Task.FromResult(new Dictionary<int, TrickplayInfo>()) }),
            SceneFinderTests.Stub<IMediaSegmentManager>(
                new() { ["GetSegmentsAsync"] = _ => Task.FromResult<IEnumerable<MediaSegmentDto>>([]) }),
            SceneFinderTests.Stub<IChapterManager>(new() { ["GetChapters"] = _ => (IReadOnlyList<ChapterInfo>)list }),
            SceneFinderTests.Stub<ILibraryManager>(new() { ["GetLibraryOptions"] = _ => new LibraryOptions() }),
            _clips.Runner,
            NullLogger<SceneFinder>.Instance,
            scratch ?? _clips.Root)
        {
            ProbeVideo = _ => probe ?? new MediaStream { Type = MediaStreamType.Video, Width = 1280, Height = 720 },
        };
    }

    private Video VideoOf(string clip, double declaredSeconds = 24) =>
        new()
        {
            Id = Guid.NewGuid(),
            Path = _clips.Path(clip),
            RunTimeTicks = (long)(declaredSeconds * TimeSpan.TicksPerSecond),
        };

    [DockerFact]
    public async Task A_clip_with_a_scene_cut_is_rejected_and_the_next_best_moment_is_used()
    {
        // The cut is at 6 s. The clip from 4 s runs across it and scores better; the clip from 8 s is clean.
        var finder = Finder((4, 0.95), (8, 0.6));
        var choice = await finder.FindStartAsync(VideoOf("cut-once.mp4"), Clip, CancellationToken.None);

        Assert.Equal(8.0, Assert.IsType<SceneChoice>(choice).StartSeconds);
    }

    [DockerFact]
    public async Task A_video_that_cuts_every_3_seconds_has_no_scene()
    {
        var finder = Finder((4, 0.9), (7, 0.9), (10, 0.9), (13, 0.9));
        var choice = await finder.FindStartAsync(VideoOf("cut-always.mp4"), Clip, CancellationToken.None);

        Assert.Null(choice);
    }

    [DockerFact]
    public async Task A_clip_that_runs_past_the_end_of_the_file_is_unreadable_and_the_next_moment_is_used()
    {
        // The file has 10 s: from 8 s there are 2 s of the 4, and the finder asks for all of them.
        var finder = Finder((8, 0.95), (3, 0.6));
        var video = VideoOf("short.mp4", declaredSeconds: 24);
        var choice = await finder.FindStartAsync(video, Clip, CancellationToken.None);

        Assert.Equal(3.0, Assert.IsType<SceneChoice>(choice).StartSeconds);
    }

    [DockerFact]
    public async Task A_clean_16_by_9_clip_gets_no_crop()
    {
        var choice = await Finder((8, 0.9)).FindStartAsync(VideoOf("clean.mp4"), Clip, CancellationToken.None);

        var found = Assert.IsType<SceneChoice>(choice);
        Assert.Equal(8.0, found.StartSeconds);
        Assert.Null(found.Crop);
    }

    [DockerFact]
    public async Task A_letterboxed_clip_gets_the_crop_of_its_picture()
    {
        var choice = await Finder((8, 0.9)).FindStartAsync(VideoOf("letter.mp4"), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(0, 90, 1280, 540), Assert.IsType<SceneChoice>(choice).Crop);
    }

    [DockerFact]
    public async Task A_pillarboxed_clip_gets_the_crop_of_its_picture()
    {
        var choice = await Finder((8, 0.9)).FindStartAsync(VideoOf("pillar.mp4"), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(160, 0, 960, 720), Assert.IsType<SceneChoice>(choice).Crop);
    }

    [DockerFact]
    public async Task An_hdr_clip_gets_the_same_crop_as_its_sdr_twin()
    {
        var sdr = await Finder((8, 0.9)).FindStartAsync(VideoOf("letter.mp4"), Clip, CancellationToken.None);
        var hdr = await Finder((8, 0.9)).FindStartAsync(VideoOf("letter-hdr.mkv"), Clip, CancellationToken.None);

        // Black is 16 of 255 in the 8-bit file and 64 of 1023 in the 10-bit one: a limit given as a number would
        // see bars in one and none in the other.
        Assert.NotNull(Assert.IsType<SceneChoice>(sdr).Crop);
        Assert.Equal(sdr.Crop, Assert.IsType<SceneChoice>(hdr).Crop);
    }

    [DockerFact]
    public async Task Without_chapters_or_trickplay_the_finder_reads_frames_of_the_real_video_and_finds_the_crop()
    {
        var choice = await Finder().FindStartAsync(VideoOf("letter.mp4"), Clip, CancellationToken.None);

        var found = Assert.IsType<SceneChoice>(choice);
        Assert.InRange(found.StartSeconds, 2.4, 24 * 0.9 - Clip);
        Assert.Equal(new CropRect(0, 90, 1280, 540), found.Crop);
    }

    // ---- the cover: with the crop it shows no bars ----

    [DockerFact]
    public async Task A_remux_with_40_chapters_and_15_tagged_streams_is_found_clean_and_cropped()
    {
        // Its header is hundreds of lines at ffmpeg's info level. The finder reads files, not messages.
        var choice = await Finder((8, 0.9)).FindStartAsync(VideoOf("remux.mkv"), Clip, CancellationToken.None);

        var found = Assert.IsType<SceneChoice>(choice);
        Assert.Equal(8.0, found.StartSeconds);
        Assert.Equal(new CropRect(0, 90, 1280, 540), found.Crop);
        Assert.Empty(Directory.GetFiles(_clips.Root, "scene-finder-*"));
    }

    [DockerFact]
    public async Task The_remux_really_prints_more_than_200_lines_at_the_info_level()
    {
        // The reason for the test above: with the messages as the source, this file would have counted as a cut.
        var (exit, errors) = await _clips.Runner.RunAsync(
            ["-loglevel", "info", "-ss", "8", "-t", "4", "-i", _clips.Path("remux.mkv"), "-f", "null", "-"],
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(0, exit);
        // The runner keeps the last 200 lines: a full tail means there were at least that many.
        Assert.Equal(200, errors.Split('\n').Length);
    }

    [DockerFact]
    public async Task A_scratch_folder_with_colons_quotes_backslashes_spaces_and_commas_works_in_the_graph()
    {
        // Each of these ends an option or a filter at one level of ffmpeg's parsing of the graph.
        var folder = System.IO.Path.Combine(_clips.Root, "a:b'c\\d e,f[g];h");
        Directory.CreateDirectory(folder);

        var finder = FinderWith(null, folder, (8, 0.9));
        var choice = await finder.FindStartAsync(VideoOf("letter.mp4"), Clip, CancellationToken.None);

        Assert.Equal(new CropRect(0, 90, 1280, 540), Assert.IsType<SceneChoice>(choice).Crop);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [DockerFact]
    public async Task A_portrait_clip_stored_sideways_is_measured_in_its_turned_size()
    {
        // Stored 1280x720, turned 90 degrees: the filters see 720x1280. With the stored size the box would have been
        // cut to 720 rows, the top of the picture.
        var stream = new MediaStream { Type = MediaStreamType.Video, Width = 1280, Height = 720, Rotation = -90 };

        var finder = FinderWith(stream, null, (8, 0.9));
        var clean = await finder.FindStartAsync(VideoOf("rotated-clean.mp4"), Clip, CancellationToken.None);
        var barred = await finder.FindStartAsync(VideoOf("rotated-letter.mp4"), Clip, CancellationToken.None);

        Assert.Null(Assert.IsType<SceneChoice>(clean).Crop);
        Assert.Equal(new CropRect(90, 0, 540, 1280), Assert.IsType<SceneChoice>(barred).Crop);
    }

    [DockerFact]
    public async Task A_cover_rendered_with_the_crop_shows_no_letterbox_bars()
    {
        var found = await Finder((8, 0.9)).FindStartAsync(VideoOf("letter.mp4"), Clip, CancellationToken.None);
        var choice = Assert.IsType<SceneChoice>(found);

        var plain = await RenderStillAsync("letter.mp4", choice.StartSeconds, crop: null, "plain-letter");
        var cropped = await RenderStillAsync("letter.mp4", choice.StartSeconds, choice.Crop, "cropped-letter");

        // The control: without the crop the 90 px bars of a 720 px picture are 45 black rows at the top and the bottom.
        Assert.InRange(BlackRows(plain), 80, 100);
        Assert.Equal(0, BlackRows(cropped));
        Assert.Equal(0, BlackColumns(cropped));
    }

    [DockerFact]
    public async Task A_cover_rendered_with_the_crop_shows_no_pillarbox_bars()
    {
        var found = await Finder((8, 0.9)).FindStartAsync(VideoOf("pillar.mp4"), Clip, CancellationToken.None);
        var choice = Assert.IsType<SceneChoice>(found);

        var plain = await RenderStillAsync("pillar.mp4", choice.StartSeconds, crop: null, "plain-pillar");
        var cropped = await RenderStillAsync("pillar.mp4", choice.StartSeconds, choice.Crop, "cropped-pillar");

        // The control: 160 of 1280 px on each side is 80 black columns of 640 on each side.
        Assert.InRange(BlackColumns(plain), 150, 170);
        Assert.Equal(0, BlackColumns(cropped));
        Assert.Equal(0, BlackRows(cropped));
    }

    // Renders one clip of 2 s as a cover with a see-through overlay and returns the still of its first frame.
    private async Task<SKBitmap> RenderStillAsync(string clip, double start, CropRect? crop, string name)
    {
        var overlay = _clips.Path("overlay.png");
        if (!File.Exists(overlay))
        {
            using var transparent = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Rgba8888, SKAlphaType.Premul));
            transparent.Erase(SKColors.Transparent);
            File.WriteAllBytes(overlay, SceneFinderTests.Png(transparent));
        }

        var plan = new RenderPlan(
            [new SlotInput(SlotKind.Video, _clips.Path(clip), start, 2, false, crop)],
            overlay,
            640,
            360,
            12,
            false,
            _clips.Path($"{name}.webp"),
            _clips.Path($"{name}.jpg"),
            _clips.Path("tmp"));
        await CoverRenderer.RenderAsync(_clips.Runner, plan, CancellationToken.None);
        return SKBitmap.Decode(plan.StillOut);
    }

    // Rows and columns whose mean brightness is that of black bars; the clip has none of its own.
    private static int BlackRows(SKBitmap picture)
    {
        var count = 0;
        for (var y = 0; y < picture.Height; y++)
        {
            var sum = 0.0;
            for (var x = 0; x < picture.Width; x++)
            {
                sum += Luma(picture.GetPixel(x, y));
            }

            count += sum / picture.Width < 12 ? 1 : 0;
        }

        return count;
    }

    private static int BlackColumns(SKBitmap picture)
    {
        var count = 0;
        for (var x = 0; x < picture.Width; x++)
        {
            var sum = 0.0;
            for (var y = 0; y < picture.Height; y++)
            {
                sum += Luma(picture.GetPixel(x, y));
            }

            count += sum / picture.Height < 12 ? 1 : 0;
        }

        return count;
    }

    private static double Luma(SKColor color) => (0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue);
}
