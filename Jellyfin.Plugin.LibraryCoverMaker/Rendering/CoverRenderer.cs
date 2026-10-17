using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>
/// Composes a cover with Jellyfin's ffmpeg in one pass: every slot (a picture with a slow zoom, or a clip, tone
/// mapped when it is HDR) is cut to a whole number of frames, the slots fade into one another or are joined, the
/// overlay goes on top, and the result is written as an animated WebP and as a JPEG of the first frame.
/// </summary>
/// <remarks>
/// <para>
/// The filter graph was validated against jellyfin-ffmpeg 8.1.3 (<c>webpinfo</c> reports no error on the WebP).
/// Three details keep a cover what the plan says it is. A picture is its first frame only (<c>trim</c>), stretched
/// by <c>zoompan</c> to the slot's frame count, and never a looped input, which froze the zoom: an animated picture
/// (a GIF) would otherwise give a whole zoom for each of its frames and make the cover much longer. Every
/// <c>scale</c> resets the sample aspect ratio (<c>reset_sar=1</c>), so that a clip with non-square pixels (DVD, DV)
/// keeps its shape instead of coming out about 15% thin. A clip is padded with its last frame and cut at its frame
/// count: <c>xfade</c> starts each fade at a fixed offset, so a clip that came out shorter than planned (a file that
/// ends before its container says) would otherwise make a later fade start after its input had ended, and the slots
/// after it were lost.
/// </para>
/// <para>
/// Padding needs a frame to clone. A clip that gives no frame at all (it starts past the end of its file) cannot be
/// padded: the slots after it are lost all the same, and ffmpeg still exits 0 with nothing on stderr. So after a
/// clean exit <see cref="RenderAsync"/> reads how long the WebP it wrote lasts, and refuses one that is not the
/// planned length.
/// </para>
/// </remarks>
public static class CoverRenderer
{
    /// <summary>How many frames a fade between two slots lasts: <c>FadeFrames / fps</c> seconds.</summary>
    public const int FadeFrames = 4;

    // The artwork zooms from 1.00 to 1.12 over its slot. Text, not a number, so no culture can change its decimal
    // point.
    private const string ZoomGain = "0.12";

    private const string ToneMap = "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p";

    // How much of ffmpeg's error output a failed render carries.
    private const int ErrorLinesKept = 20;

    // The animated WebP is written only at the end of a render, so what shows that ffmpeg is alive is its processor
    // time and its messages (see FfmpegRunner). Three minutes with neither is a file that hangs, such as a network
    // share.
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long each slot lasts, in seconds, so that <paramref name="slots"/> slots that fade into one another (the
    /// fade takes <see cref="FadeFrames"/> frames and both slots are on screen during it) add up to a cover of
    /// <paramref name="lengthSeconds"/>: <c>(length + (slots - 1) * fade) / slots</c>. Without a fade it is
    /// <c>length / slots</c>.
    /// </summary>
    public static double SlotSeconds(double lengthSeconds, int slots, bool fade, int fps)
    {
        if (!double.IsFinite(lengthSeconds) || lengthSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lengthSeconds),
                lengthSeconds,
                "The length must be a positive number of seconds.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);

        var fadeSeconds = fade ? FadeFrames / (double)fps : 0;
        return (lengthSeconds + ((slots - 1) * fadeSeconds)) / slots;
    }

    /// <summary>
    /// The ffmpeg arguments that render <paramref name="plan"/>, without the options every run starts with, which
    /// <see cref="FfmpegRunner"/> adds. The outputs are the temporary files of <see cref="TempOutputs"/>, not the
    /// final paths: <see cref="RenderAsync"/> moves them once ffmpeg has succeeded. The same plan always gives the
    /// same arguments. Throws <see cref="ArgumentException"/> for a plan that cannot be rendered.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(RenderPlan plan)
    {
        var frames = Validate(plan);
        var (animated, still) = TempOutputs(plan);
        return Compose(plan, frames, animated, still);
    }

    /// <summary>
    /// Renders the cover. ffmpeg writes into the plan's temporary files. Only when it has exited cleanly, both files
    /// exist and are not empty, and the animated WebP lasts the planned length (to within one frame) are they moved to
    /// the final paths, the folders of which are created if they are missing, so a half made or a short cover is
    /// never at a final path. On any failure the temporary files are deleted, and, apart from cancellation,
    /// <see cref="CoverRenderException"/> says why.
    /// </summary>
    /// <remarks>
    /// A cover whose frames are all the same (the name-only cover, one flat colour) comes out of libwebp as a still
    /// image: its muxer writes an animation of one frame as a still, and identical frames are one frame. A still has
    /// no length to check, so it is accepted for a plan of one slot, where nothing can be missing. With several slots
    /// it means that they gave one frame between them, and it is refused.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The plan cannot be rendered, or a final path already exists: a render never replaces a file, so that a cover is
    /// both its files or neither. Nothing has been started or written.
    /// </exception>
    /// <exception cref="CoverRenderException">ffmpeg failed, the cover is not what was planned, or its files could not
    /// be stored.</exception>
    /// <exception cref="OperationCanceledException">Cancelled: ffmpeg was stopped and the temporary files
    /// removed.</exception>
    public static async Task RenderAsync(FfmpegRunner ffmpeg, RenderPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);

        var frames = Validate(plan);
        RequireNew(plan.AnimatedOut, "The final path of the animated WebP");
        RequireNew(plan.StillOut, "The final path of the still JPEG");

        var (animatedTemp, stillTemp) = TempOutputs(plan);
        var args = Compose(plan, frames, animatedTemp, stillTemp);
        var plannedMilliseconds = PlannedFrames(plan, frames) * 1000.0 / plan.Fps;
        try
        {
            CreateFolders(animatedTemp, stillTemp);

            // What an earlier render that was cut short left behind must not pass for this render's output.
            TryDelete(animatedTemp);
            TryDelete(stillTemp);

            var run = await ffmpeg.RunDetailedAsync(args, StallLimit, ct).ConfigureAwait(false);
            var tail = LastLines(run.Errors);
            if (run.Exit != 0)
            {
                throw new CoverRenderException(FailureMessage(run), run.Exit, tail);
            }

            RequireContent(animatedTemp, "animated WebP", tail);
            RequireContent(stillTemp, "still JPEG", tail);
            RequireLength(animatedTemp, plan, plannedMilliseconds, tail);
            Publish(plan, animatedTemp, stillTemp, tail);
        }
        finally
        {
            // After a successful render both files have been moved and there is nothing left to delete.
            TryDelete(animatedTemp);
            TryDelete(stillTemp);
        }
    }

    /// <summary>
    /// How long the cover of <paramref name="plan"/> lasts, in milliseconds: the frames of its slots, less the frames
    /// that two neighbouring slots share while one fades into the other.
    /// </summary>
    internal static double PlannedMilliseconds(RenderPlan plan) =>
        PlannedFrames(plan, Validate(plan)) * 1000.0 / plan.Fps;

    /// <summary>
    /// Where ffmpeg writes the animated WebP and the still JPEG before they are moved to their final paths: the
    /// plan's temporary folder, or beside each final file when it has none. The names come from the final names,
    /// so they are the same every time and never collide with a final file.
    /// </summary>
    internal static (string Animated, string Still) TempOutputs(RenderPlan plan) =>
        (TempPath(plan, plan.AnimatedOut, ".webp"), TempPath(plan, plan.StillOut, ".jpg"));

    private static string TempPath(RenderPlan plan, string finalPath, string extension)
    {
        var folder = plan.TempFolder ?? Path.GetDirectoryName(finalPath)!;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(finalPath) + ".render" + extension);
    }

    private static List<string> Compose(RenderPlan plan, int[] frames, string animatedTemp, string stillTemp)
    {
        // -threads belongs to one file, so every input and both outputs carry their own; the filter graph has its own
        // option.
        var args = new List<string>
        {
            "-filter_complex_threads",
            FfmpegRunner.MaxThreads.ToString(CultureInfo.InvariantCulture),
        };

        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            args.AddRange(FfmpegRunner.ThreadLimit);
            if (slot.Kind == SlotKind.Video)
            {
                // Seeking before the input jumps to the moment instead of decoding up to it; the clip is read for its
                // length only.
                args.AddRange(["-ss", Seconds(slot.StartSeconds), "-t", Seconds(frames[i] / (double)plan.Fps)]);
            }

            args.AddRange(["-i", slot.Path]);
        }

        // The overlay is the one looped input: the picture slots are single frames, because looping them froze the
        // zoom.
        args.AddRange(FfmpegRunner.ThreadLimit);
        args.AddRange(["-loop", "1", "-i", plan.OverlayPngPath]);

        args.AddRange(["-filter_complex", BuildGraph(plan, frames)]);

        args.AddRange(["-map", "[anim]"]);
        args.AddRange(FfmpegRunner.ThreadLimit);
        args.AddRange(["-c:v", "libwebp_anim", "-quality", "78", "-loop", "0", "-an", animatedTemp]);

        args.AddRange(["-map", "[st]"]);
        args.AddRange(FfmpegRunner.ThreadLimit);
        args.AddRange(["-frames:v", "1", "-q:v", "3", stillTemp]);
        return args;
    }

    private static string BuildGraph(RenderPlan plan, int[] frames)
    {
        var chains = new List<string>();
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            var chain = slot.Kind == SlotKind.Image
                ? ImageChain(frames[i], plan.Width, plan.Height, plan.Fps)
                : VideoChain(slot.IsHdr, slot.Crop, frames[i], plan.Width, plan.Height, plan.Fps);
            chains.Add(Inv($"[{i}:v]{chain}[s{i}]"));
        }

        var joined = JoinSlots(plan, frames, chains);
        chains.Add(
            Inv($"[{joined}][{plan.Slots.Count}:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]"));
        return string.Join(';', chains);
    }

    // Adds the chains that join the slots and returns the label of the joined video.
    private static string JoinSlots(RenderPlan plan, int[] frames, List<string> chains)
    {
        var count = plan.Slots.Count;
        if (count == 1)
        {
            return "s0";
        }

        if (!plan.Fade)
        {
            var inputs = string.Concat(Enumerable.Range(0, count).Select(i => Inv($"[s{i}]")));
            chains.Add(Inv($"{inputs}concat=n={count}:v=1:a=0[v]"));
            return "v";
        }

        // Each slot starts fading in FadeFrames before everything joined so far ends, and that adds the rest of the
        // slot to the length. The offsets come from the frames each slot really has, which is why they are exact.
        var fade = Seconds(FadeFrames / (double)plan.Fps);
        var joined = "s0";
        var running = frames[0];
        for (var i = 1; i < count; i++)
        {
            var offset = Seconds((running - FadeFrames) / (double)plan.Fps);
            chains.Add(Inv($"[{joined}][s{i}]xfade=transition=fade:duration={fade}:offset={offset}[x{i}]"));
            joined = Inv($"x{i}");
            running += frames[i] - FadeFrames;
        }

        return joined;
    }

    // The first frame only, doubled in size so that the zoom has pixels to spare, then stretched by zoompan to its
    // frame count. Without the trim, an animated picture gives zoompan a whole zoom for each of its frames.
    private static string ImageChain(int frames, int width, int height, int fps) =>
        string.Join(
            ',',
            "trim=end_frame=1",
            Inv($"scale={2 * width}:{2 * height}:force_original_aspect_ratio=increase:reset_sar=1"),
            Inv($"crop={2 * width}:{2 * height}"),
            Inv($"zoompan=z='1+{ZoomGain}*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':")
                + Inv($"d={frames}:s={width}x{height}:fps={fps}"),
            "setsar=1",
            "format=yuv420p");

    // The clip's own frames, cut to the picture inside its burned-in bars when it has any (the crop is in the pixels
    // of the file and works on any pixel format, so it comes first), tone mapped when it is HDR, then pinned to
    // exactly its frame count.
    private static string VideoChain(bool hdr, CropRect? crop, int frames, int width, int height, int fps)
    {
        var filters = new List<string>();
        if (crop is { } rect)
        {
            filters.Add(Inv($"crop={rect.Width}:{rect.Height}:{rect.X}:{rect.Y}"));
        }

        if (hdr)
        {
            filters.Add(ToneMap);
        }

        filters.Add(Inv($"fps={fps}"));
        filters.Add(Inv($"scale={width}:{height}:force_original_aspect_ratio=increase:reset_sar=1"));
        filters.Add(Inv($"crop={width}:{height}"));
        filters.Add("setsar=1");
        filters.Add("format=yuv420p");
        filters.Add(Inv($"tpad=stop_mode=clone:stop={frames}"));
        filters.Add(Inv($"trim=end_frame={frames}"));
        return string.Join(',', filters);
    }

    /// <summary>
    /// Checks the plan and returns the number of frames of each slot. The frames are shared out by cumulative
    /// rounding, so the slots add up to the length of the cover to the frame (five slots of 1.4667 s at 12 fps get 18,
    /// 17, 18, 17 and 18 frames, 88 in all) instead of each rounding in the same direction.
    /// </summary>
    private static int[] Validate(RenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var slots = plan.Slots ?? throw new ArgumentException("The plan has no list of slots.", nameof(plan));
        if (slots.Count == 0)
        {
            throw new ArgumentException("A cover needs at least one slot.", nameof(plan));
        }

        // yuv420p halves the colour planes, which needs even sizes.
        if (plan.Width <= 0 || plan.Height <= 0 || plan.Width % 2 != 0 || plan.Height % 2 != 0)
        {
            throw new ArgumentException(
                $"The size of a cover must be positive and even, not {plan.Width}x{plan.Height}.",
                nameof(plan));
        }

        if (plan.Fps <= 0)
        {
            throw new ArgumentException($"The frame rate must be positive, not {plan.Fps}.", nameof(plan));
        }

        RequireAbsolute(plan.OverlayPngPath, "The overlay");
        RequireAbsolute(plan.AnimatedOut, "The final path of the animated WebP");
        RequireAbsolute(plan.StillOut, "The final path of the still JPEG");
        if (plan.TempFolder is not null)
        {
            RequireAbsolute(plan.TempFolder, "The temporary folder");
        }

        // A fade that takes the whole slot, or more, leaves nothing of it to see.
        var shortest = plan.Fade && slots.Count > 1 ? FadeFrames + 1 : 1;
        var frames = new int[slots.Count];
        var seconds = 0.0;
        var done = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i] ?? throw new ArgumentException($"Slot {i} is missing.", nameof(plan));
            RequireAbsolute(slot.Path, $"The file of slot {i}");
            if (!double.IsFinite(slot.DurationSeconds) || slot.DurationSeconds <= 0)
            {
                throw new ArgumentException(
                    $"Slot {i} must last a positive number of seconds, not {slot.DurationSeconds}.",
                    nameof(plan));
            }

            if (slot.Kind == SlotKind.Video && (!double.IsFinite(slot.StartSeconds) || slot.StartSeconds < 0))
            {
                throw new ArgumentException(
                    $"The clip of slot {i} must start at 0 seconds or later, not {slot.StartSeconds}.",
                    nameof(plan));
            }

            if (slot.Kind == SlotKind.Video && slot.Crop is { } crop
                && (crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0))
            {
                throw new ArgumentException(
                    $"The crop of slot {i} must have a positive size and a non-negative offset, not {crop}.",
                    nameof(plan));
            }

            seconds += slot.DurationSeconds;
            var boundary = (int)Math.Round(seconds * plan.Fps, MidpointRounding.AwayFromZero);
            frames[i] = boundary - done;
            done = boundary;
            if (frames[i] < shortest)
            {
                throw new ArgumentException(
                    $"Slot {i} is too short: it gets {frames[i]} frames at {plan.Fps} fps and needs at least "
                    + $"{shortest}.",
                    nameof(plan));
            }
        }

        return frames;
    }

    // The slots show all their frames, and two neighbours share the frames of the fade between them.
    private static int PlannedFrames(RenderPlan plan, int[] frames) =>
        frames.Sum() - (plan.Fade && frames.Length > 1 ? FadeFrames * (frames.Length - 1) : 0);

    // A path that does not start at the root could be taken for an option or a URL by ffmpeg, or point into the
    // working directory of the server.
    private static void RequireAbsolute(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw new ArgumentException($"{what} must be an absolute path, not \"{path}\".", "plan");
        }
    }

    // A render never replaces a file. A cover is both its files or neither, and that could not be kept if the second
    // file failed to move after the first had replaced one that was there.
    private static void RequireNew(string path, string what)
    {
        if (Path.Exists(path))
        {
            throw new ArgumentException($"{what} already exists ({path}): a render never replaces a file.", "plan");
        }
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string LastLines(string errors)
    {
        var lines = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines[Math.Max(0, lines.Length - ErrorLinesKept)..]);
    }

    private static string FailureMessage(RunResult run)
    {
        // A negative code means ffmpeg did not run to its end (it could not be started, or it was stopped), and
        // the runner has said why in the last line.
        if (run.Exit < 0)
        {
            var lines = run.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "ffmpeg did not run to its end." : lines[^1];
        }

        // ffmpeg prints the cause of a failure first and then what follows from it ("Nothing was written into
        // output file", "Task finished with error code"), so the first line is the one that says why. The runner
        // keeps it apart from the tail, so it is still the one shown after more than 200 lines. What it cannot do
        // is look past decoder noise that comes before the cause, from a file that is damaged but usable.
        return run.FirstLine.Length == 0
            ? $"ffmpeg failed with exit code {run.Exit}."
            : $"ffmpeg failed with exit code {run.Exit}: {WithoutContext(run.FirstLine)}";
    }

    // "[mjpeg @ 0x788d44ad4a80] sof: invalid len": the component and its address mean nothing on a library's card.
    private static string WithoutContext(string line)
    {
        while (line.StartsWith('['))
        {
            var close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close < 0 || !line.AsSpan(0, close).Contains(" @ 0x", StringComparison.Ordinal))
            {
                break;
            }

            line = line[(close + 2)..];
        }

        return line;
    }

    // The folders ffmpeg writes into. A folder that cannot be made is its own reason, and ffmpeg has not run.
    private static void CreateFolders(string animatedTemp, string stillTemp)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(animatedTemp)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stillTemp)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CoverRenderException(
                $"The folder for the render in progress could not be created: {ex.Message}",
                FfmpegRunner.NotCompleted,
                string.Empty,
                ex);
        }
    }

    private static void RequireContent(string path, string what, string tail)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new CoverRenderException($"ffmpeg did not write the {what}.", 0, tail);
        }

        if (file.Length == 0)
        {
            throw new CoverRenderException($"ffmpeg wrote an empty {what}.", 0, tail);
        }
    }

    // ffmpeg exits 0 when a clip gave no frame at all, and the slots after it are gone: only the WebP knows how long it
    // is.
    private static void RequireLength(string animatedTemp, RenderPlan plan, double plannedMilliseconds, string tail)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(animatedTemp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CoverRenderException(
                $"The animated WebP that ffmpeg wrote could not be read back: {ex.Message}",
                0,
                tail,
                ex);
        }

        (bool Animated, long Milliseconds) timeline;
        try
        {
            timeline = WebpTimeline.Read(file);
        }
        catch (InvalidDataException ex)
        {
            throw new CoverRenderException(
                $"ffmpeg wrote an animated WebP that cannot be used: it {ex.Message}.",
                0,
                tail,
                ex);
        }

        if (!timeline.Animated)
        {
            // A still has no length to check. One slot cannot have lost another, and may be one flat colour.
            if (plan.Slots.Count == 1)
            {
                return;
            }

            throw new CoverRenderException(
                "The cover came out as a still picture instead of an animation: all but one slot gave no frames, or "
                + "every frame was the same.",
                0,
                tail);
        }

        // One frame of leeway: the durations of the frames are whole milliseconds.
        var actual = timeline.Milliseconds;
        if (Math.Abs(actual - plannedMilliseconds) <= 1000.0 / plan.Fps)
        {
            return;
        }

        var cause = actual < plannedMilliseconds
            ? "a clip gave fewer frames than planned"
            : "a picture or clip gave more frames than planned";
        throw new CoverRenderException(
            $"The cover came out {Describe(actual)} long instead of {Describe(plannedMilliseconds)}: {cause}.",
            0,
            tail);
    }

    // 2.3 s, 6 s, 26.2 s.
    private static string Describe(double milliseconds) =>
        (milliseconds / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";

    // Moves the two files into place. Neither replaces a file, and when the second cannot be stored the first is
    // taken away again, so that a cover is both its files or neither. Only the file this method placed is removed.
    private static void Publish(RenderPlan plan, string animatedTemp, string stillTemp, string tail)
    {
        var animatedPlaced = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.AnimatedOut)!);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.StillOut)!);

            File.Move(animatedTemp, plan.AnimatedOut, overwrite: false);
            animatedPlaced = true;
            File.Move(stillTemp, plan.StillOut, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (animatedPlaced)
            {
                TryDelete(plan.AnimatedOut);
            }

            throw new CoverRenderException($"The rendered cover could not be stored: {ex.Message}", 0, tail, ex);
        }
    }

    // Best effort: whatever stays behind is in the temp folder, which is emptied when the server starts.
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do with it.
        }
    }
}

/// <summary>
/// A cover could not be rendered. <see cref="Exception.Message"/> says why in one line, for the library's card.
/// </summary>
public sealed class CoverRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the render failed, in one line.</param>
    /// <param name="exitCode">The exit code of ffmpeg: 0 when it succeeded and what came after it failed, negative when
    /// it did not run to its end.</param>
    /// <param name="errors">The last 20 lines of ffmpeg's error output.</param>
    /// <param name="inner">The exception behind it, if any.</param>
    public CoverRenderException(string message, int exitCode, string errors, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
        Errors = errors;
    }

    /// <summary>
    /// The exit code of ffmpeg: 0 when it succeeded and what came after it failed, negative when it did not run to its
    /// end.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>The last 20 lines of ffmpeg's error output, one per line. Empty when ffmpeg said nothing.</summary>
    public string Errors { get; }
}

/// <summary>
/// Reads how long a WebP lasts from its container, without decoding a frame: the RIFF chunks are walked and the
/// durations of the ANMF (animation frame) chunks are added up.
/// </summary>
internal static class WebpTimeline
{
    /// <summary>
    /// For a WebP with animation frames, true and the sum of their durations in milliseconds. For a still WebP (an
    /// image chunk and no frames), false and 0. A file that is neither, or is cut short, throws
    /// <see cref="InvalidDataException"/>, and nothing else, whatever its bytes are; the message completes the
    /// sentence "the file ...".
    /// </summary>
    public static (bool Animated, long Milliseconds) Read(ReadOnlySpan<byte> file)
    {
        // RIFF, the size of what follows (little endian), WEBP; then chunks: four letters, a size, the payload,
        // and a padding byte after a payload of odd size.
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("is not a WebP file");
        }

        var end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(4, 4));
        if (end > file.Length)
        {
            throw new InvalidDataException("ends before its header says it does");
        }

        long milliseconds = 0;
        var frames = 0;
        var hasImage = false;
        var position = 12L;
        while (position + 8 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice((int)position + 4, 4));
            var payload = position + 8;
            if (payload + size > end)
            {
                throw new InvalidDataException("has a chunk that ends past the end of the file");
            }

            var tag = file.Slice((int)position, 4);
            if (tag.SequenceEqual("ANMF"u8))
            {
                // Frame x, y, width and height take 12 bytes, then the duration (24 bits), then flags.
                if (size < 16)
                {
                    throw new InvalidDataException("has a frame chunk too small to hold a frame");
                }

                var duration = file.Slice((int)payload + 12, 3);
                milliseconds += duration[0] | (duration[1] << 8) | (duration[2] << 16);
                frames++;
            }
            else if (tag.SequenceEqual("VP8 "u8) || tag.SequenceEqual("VP8L"u8))
            {
                hasImage = true;
            }

            position = payload + size + (size & 1);
        }

        if (frames > 0)
        {
            return (true, milliseconds);
        }

        return hasImage ? (false, 0) : throw new InvalidDataException("holds no image");
    }
}
