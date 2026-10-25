using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>What the renderer asks ffmpeg to do, and how it reads a WebP. Nothing here starts a process.</summary>
public sealed class CoverRendererTests
{
    private const string TempFolder = "/data/library-cover-maker/tmp";

    private static RenderPlan Plan(IReadOnlyList<SlotInput> slots, bool fade = true, string? tempFolder = TempFolder) =>
        new(
            slots,
            "/data/overlay.png",
            640,
            360,
            12,
            fade,
            "/data/covers/ab12.webp",
            "/data/covers/ab12.jpg",
            tempFolder);

    private static SlotInput Image(string path, double seconds = 1.5) => new(SlotKind.Image, path, 0, seconds, false);

    private static SlotInput Video(string path, double start = 12.5, double seconds = 1.5, bool hdr = false) =>
        new(SlotKind.Video, path, start, seconds, hdr);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    // One chain of the filter graph, for example the one that feeds [1:v].
    private static string Chain(IReadOnlyList<string> args, int input) =>
        Graph(args).Split(';').Single(chain => chain.StartsWith($"[{input}:v]", StringComparison.Ordinal));

    [Fact]
    public void Slot_seconds_make_the_cover_last_its_length()
    {
        var withFade = CoverRenderer.SlotSeconds(6, 5, fade: true, fps: 12);
        Assert.Equal(6.0, (withFade * 5) - (4 * (4.0 / 12)), tolerance: 0.001);

        // Without a fade nothing overlaps, so the slots simply share the length.
        Assert.Equal(6.0 / 5, CoverRenderer.SlotSeconds(6, 5, fade: false, fps: 12), tolerance: 0.001);
    }

    [Fact]
    public void A_single_slot_lasts_the_whole_length()
    {
        Assert.Equal(6.0, CoverRenderer.SlotSeconds(6, 1, fade: true, fps: 12), tolerance: 0.001);
        Assert.Equal(6.0, CoverRenderer.SlotSeconds(6, 1, fade: false, fps: 12), tolerance: 0.001);
    }

    [Theory]
    [InlineData(0, 5, 12)]
    [InlineData(-1, 5, 12)]
    [InlineData(6, 0, 12)]
    [InlineData(6, 5, 0)]
    [InlineData(double.NaN, 5, 12)]
    public void Slot_seconds_refuses_nonsense(double length, int slots, int fps) =>
        Assert.ThrowsAny<ArgumentException>(() => CoverRenderer.SlotSeconds(length, slots, fade: true, fps));

    [Fact]
    public void Two_images_with_fade_build_zoompan_and_xfade()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Image("/media/b/fanart.jpg") }));
        var graph = Graph(args);

        Assert.Contains("zoompan=z='1+0.12*on/", graph);
        Assert.Contains("xfade=transition=fade:duration=0.333:offset=", graph);

        // The exact graph, so a change to a validated value cannot slip in unseen.
        Assert.Equal(
            string.Join(
                ';',
                "[0:v]trim=end_frame=1,scale=1280:720:force_original_aspect_ratio=increase:reset_sar=1,crop=1280:720,"
                + "zoompan=z='1+0.12*on/18':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=18:s=640x360:fps=12,setsar=1,"
                + "format=yuv420p[s0]",
                "[1:v]trim=end_frame=1,scale=1280:720:force_original_aspect_ratio=increase:reset_sar=1,crop=1280:720,"
                + "zoompan=z='1+0.12*on/18':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=18:s=640x360:fps=12,setsar=1,"
                + "format=yuv420p[s1]",
                "[s0][s1]xfade=transition=fade:duration=0.333:offset=1.167[x1]",
                "[x1][2:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]"),
            graph);
    }

    [Fact]
    public void Arguments_are_the_validated_command()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/Film (2020).mkv") }));

        Assert.Equal(
            new[]
            {
                "-filter_complex_threads", "2",
                "-threads", "2", "-i", "/media/a/fanart.jpg",
                "-threads", "2", "-ss", "12.5", "-t", "1.5", "-i", "/media/b/Film (2020).mkv",
                "-threads", "2", "-loop", "1", "-i", "/data/overlay.png",
                "-filter_complex",
                string.Join(
                    ';',
                    "[0:v]trim=end_frame=1,scale=1280:720:force_original_aspect_ratio=increase:reset_sar=1,"
                    + "crop=1280:720,"
                    + "zoompan=z='1+0.12*on/18':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=18:s=640x360:fps=12,"
                    + "setsar=1,format=yuv420p[s0]",
                    "[1:v]fps=12,scale=640:360:force_original_aspect_ratio=increase:reset_sar=1,crop=640:360,setsar=1,"
                    + "format=yuv420p,tpad=stop_mode=clone:stop=18,trim=end_frame=18[s1]",
                    "[s0][s1]xfade=transition=fade:duration=0.333:offset=1.167[x1]",
                    "[x1][2:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]"),
                "-map", "[anim]", "-threads", "2", "-c:v", "libwebp_anim", "-quality", "78", "-loop", "0", "-an",
                "/data/library-cover-maker/tmp/ab12.render.webp",
                "-map", "[st]", "-threads", "2", "-frames:v", "1", "-q:v", "3",
                "/data/library-cover-maker/tmp/ab12.render.jpg",
            },
            args);
    }

    [Fact]
    public void Hdr_video_slot_is_tone_mapped()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/hdr.mkv", hdr: true) }));

        var chain = Chain(args, input: 1);
        const string toneMap = "tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p,";
        Assert.Contains(toneMap, chain);

        // Tone mapping comes first, on the frames as decoded, before the frame rate and the size change.
        Assert.StartsWith("[1:v]" + toneMap + "fps=12,scale=640:360:", chain);
    }

    [Fact]
    public void Sdr_video_slot_is_not()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/sdr.mkv", hdr: false) }));

        Assert.DoesNotContain("tonemapx", Graph(args));
        Assert.StartsWith("[1:v]fps=12,scale=640:360:", Chain(args, input: 1));
    }

    [Fact]
    public void A_cropped_video_slot_crops_first_in_the_exact_graph()
    {
        var crop = new CropRect(0, 90, 1280, 540);
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/letterbox.mkv") with { Crop = crop } }));

        // The crop is in the pixels of the file, so it comes before the frame rate and the size change.
        Assert.Equal(
            string.Join(
                ';',
                "[0:v]trim=end_frame=1,scale=1280:720:force_original_aspect_ratio=increase:reset_sar=1,crop=1280:720,"
                + "zoompan=z='1+0.12*on/18':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=18:s=640x360:fps=12,setsar=1,"
                + "format=yuv420p[s0]",
                "[1:v]crop=1280:540:0:90,fps=12,scale=640:360:force_original_aspect_ratio=increase:reset_sar=1,"
                + "crop=640:360,setsar=1,format=yuv420p,tpad=stop_mode=clone:stop=18,trim=end_frame=18[s1]",
                "[s0][s1]xfade=transition=fade:duration=0.333:offset=1.167[x1]",
                "[x1][2:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]"),
            Graph(args));
    }

    [Fact]
    public void A_crop_comes_before_the_tone_mapping_of_an_hdr_slot()
    {
        var args = CoverRenderer.BuildArguments(Plan(new[]
        {
            Image("/media/a/fanart.jpg"),
            Video("/media/b/hdr.mkv", hdr: true) with { Crop = new CropRect(160, 0, 960, 720) },
        }));

        Assert.StartsWith("[1:v]crop=960:720:160:0,tonemapx=tonemap=bt2390:", Chain(args, input: 1));
    }

    [Fact]
    public void A_slot_without_a_crop_adds_no_crop_before_the_scale()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/clean.mkv") }));

        Assert.StartsWith("[1:v]fps=12,", Chain(args, input: 1));
    }

    [Theory]
    [InlineData(0, 0, 0, 540)]
    [InlineData(0, 0, 1280, -2)]
    [InlineData(-2, 0, 1280, 540)]
    [InlineData(0, -2, 1280, 540)]
    public void A_crop_that_is_not_a_picture_is_refused(int x, int y, int width, int height)
    {
        var plan = Plan(new[] { Video("/media/b/letterbox.mkv") with { Crop = new CropRect(x, y, width, height) } });

        Assert.Throws<ArgumentException>(() => CoverRenderer.BuildArguments(plan));
    }

    [Fact]
    public void No_fade_uses_concat()
    {
        var args = CoverRenderer.BuildArguments(Plan(
            new[]
            {
                Image("/media/a/fanart.jpg"),
                Video("/media/b/hdr.mkv", start: 3, hdr: true),
                Video("/media/c/sdr.mkv", start: 4),
            },
            fade: false));
        var graph = Graph(args);

        Assert.Contains("concat=n=3:v=1:a=0", graph);
        Assert.DoesNotContain("xfade", graph);
        Assert.Equal(
            string.Join(
                ';',
                "[0:v]trim=end_frame=1,scale=1280:720:force_original_aspect_ratio=increase:reset_sar=1,crop=1280:720,"
                + "zoompan=z='1+0.12*on/18':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=18:s=640x360:fps=12,setsar=1,"
                + "format=yuv420p[s0]",
                "[1:v]tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p,fps=12,"
                + "scale=640:360:force_original_aspect_ratio=increase:reset_sar=1,crop=640:360,setsar=1,format=yuv420p,"
                + "tpad=stop_mode=clone:stop=18,trim=end_frame=18[s1]",
                "[2:v]fps=12,scale=640:360:force_original_aspect_ratio=increase:reset_sar=1,crop=640:360,setsar=1,"
                + "format=yuv420p,tpad=stop_mode=clone:stop=18,trim=end_frame=18[s2]",
                "[s0][s1][s2]concat=n=3:v=1:a=0[v]",
                "[v][3:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]"),
            graph);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Single_slot_has_no_xfade(bool fade)
    {
        var graph = Graph(CoverRenderer.BuildArguments(Plan(new[] { Image("/media/a/fanart.jpg") }, fade)));

        Assert.DoesNotContain("xfade", graph);
        Assert.DoesNotContain("concat", graph);
        Assert.EndsWith("[s0][1:v]overlay=shortest=1:format=auto,format=yuv420p,split=2[anim][st]", graph);
    }

    [Fact]
    public void Image_slot_keeps_only_the_first_frame_of_an_animated_picture()
    {
        // A GIF or an animated WebP given to a picture slot would otherwise give zoompan a whole zoom for each of
        // its frames: a 10-frame GIF as the last slot made a cover of 26 s instead of 6 s.
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/anim.gif"), Image("/media/b/fanart.jpg") }));

        Assert.StartsWith("[0:v]trim=end_frame=1,scale=1280:720:", Chain(args, input: 0));
        Assert.StartsWith("[1:v]trim=end_frame=1,scale=1280:720:", Chain(args, input: 1));
    }

    [Fact]
    public void Every_scale_resets_the_pixel_aspect_ratio_so_that_anamorphic_sources_keep_their_shape()
    {
        // A DVD or DV clip (720x480, SAR 32:27) is 16:9 on screen. Scaling its stored pixels and then calling them
        // square made a square come out 15% too thin.
        var args = CoverRenderer.BuildArguments(Plan(new[]
        {
            Image("/media/a/fanart.jpg"),
            Video("/media/b/dvd.mkv"),
            Video("/media/c/hdr.mkv", hdr: true),
        }));
        var graph = Graph(args);

        Assert.Equal(3, Regex.Matches(graph, "scale=").Count);
        Assert.Equal(3, Regex.Matches(graph, @"scale=\d+:\d+:force_original_aspect_ratio=increase:reset_sar=1,").Count);
    }

    [Fact]
    public void Outputs_are_temp_files_in_the_temp_folder()
    {
        var plan = Plan(new[] { Image("/media/a/fanart.jpg") });
        var args = CoverRenderer.BuildArguments(plan);

        var animated = args[args.ToList().IndexOf("-an") + 1];
        var still = args[^1];

        Assert.EndsWith(".webp", animated);
        Assert.EndsWith(".jpg", still);
        Assert.Equal(TempFolder, Path.GetDirectoryName(animated));
        Assert.Equal(TempFolder, Path.GetDirectoryName(still));
        Assert.NotEqual(plan.AnimatedOut, animated);
        Assert.NotEqual(plan.StillOut, still);

        // The same names are used every time, so what is built is what is moved afterwards.
        Assert.Equal((animated, still), CoverRenderer.TempOutputs(plan));
    }

    [Fact]
    public void Temp_files_go_beside_the_final_files_without_a_temp_folder()
    {
        var plan = Plan(new[] { Image("/media/a/fanart.jpg") }, tempFolder: null);
        var (animated, still) = CoverRenderer.TempOutputs(plan);

        Assert.Equal("/data/covers", Path.GetDirectoryName(animated));
        Assert.Equal("/data/covers", Path.GetDirectoryName(still));
        Assert.NotEqual(plan.AnimatedOut, animated);
        Assert.NotEqual(plan.StillOut, still);
        Assert.EndsWith(".webp", animated);
        Assert.EndsWith(".jpg", still);
    }

    [Fact]
    public void Image_slot_is_one_frame_that_zoompan_stretches_and_is_never_looped()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Image("/media/b/fanart.jpg") }));
        var list = args.ToList();

        // Looping an image input froze the zoom; of all the inputs only the overlay is looped.
        Assert.Equal(
            new[] { "-loop", "1", "-i", "/data/overlay.png" },
            args.Skip(list.IndexOf("/data/overlay.png") - 3).Take(4));
        Assert.Equal(1, list.Take(list.IndexOf("-filter_complex")).Count(arg => arg == "-loop"));
        Assert.Equal(new[] { "-threads", "2", "-i", "/media/a/fanart.jpg" }, args.Skip(2).Take(4));
    }

    [Fact]
    public void Video_slot_seeks_before_its_input_and_reads_only_its_length()
    {
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Video("/media/b/Film.mkv", start: 1234.5678, seconds: 1.5) }));
        var list = args.ToList();
        var input = list.IndexOf("/media/b/Film.mkv");

        Assert.Equal("-i", list[input - 1]);
        Assert.Equal(new[] { "-ss", "1234.568", "-t", "1.5" }, args.Skip(input - 5).Take(4));
    }

    [Fact]
    public void Video_slot_is_pinned_to_its_frame_count()
    {
        // A clip that ends early (a file shorter than its container says) is padded with its last frame, and one
        // that runs a frame long is cut: otherwise every later fade starts in the wrong place and slots vanish.
        var args = CoverRenderer.BuildArguments(
            Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/Film.mkv") }));

        Assert.EndsWith(
            ",tpad=stop_mode=clone:stop=18,trim=end_frame=18",
            Chain(args, input: 1).Replace("[s1]", string.Empty));
    }

    [Fact]
    public void Frame_counts_make_the_cover_last_exactly_its_length()
    {
        // 6 s at 12 fps is 72 frames. Five slots of 1.4667 s are not whole frames, so the frames are shared out
        // by cumulative rounding: 18, 17, 18, 17, 18 (88 in all, less four fades of four frames).
        var slot = CoverRenderer.SlotSeconds(6, 5, fade: true, fps: 12);
        var images = Enumerable.Range(0, 5).Select(i => Image($"/media/{i}/fanart.jpg", slot)).ToList();
        var graph = Graph(CoverRenderer.BuildArguments(Plan(images)));

        var frames = Regex.Matches(graph, @":d=(\d+):").Select(
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(new[] { 18, 17, 18, 17, 18 }, frames);
        Assert.Equal(72, frames.Sum() - (CoverRenderer.FadeFrames * 4));

        // Each fade starts four frames before everything before it ends.
        var offsets = Regex.Matches(graph, @"offset=([\d.]+)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "1.167", "2.25", "3.417", "4.5" }, offsets);
    }

    [Fact]
    public void Frame_counts_without_a_fade_add_up_to_the_length()
    {
        var slot = CoverRenderer.SlotSeconds(6, 5, fade: false, fps: 12);
        var images = Enumerable.Range(0, 5).Select(i => Image($"/media/{i}/fanart.jpg", slot)).ToList();
        var graph = Graph(CoverRenderer.BuildArguments(Plan(images, fade: false)));

        var frames = Regex.Matches(graph, @":d=(\d+):").Select(
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(new[] { 14, 15, 14, 15, 14 }, frames);
        Assert.Equal(72, frames.Sum());
    }

    [Theory]
    [InlineData(1, true, 6.0)]
    [InlineData(3, true, 6.0)]
    [InlineData(5, true, 6.0)]
    [InlineData(5, false, 6.0)]
    [InlineData(2, true, 2.0)]
    [InlineData(5, true, 12.0)]
    [InlineData(4, false, 3.0)]
    [InlineData(1, true, 30.0)]
    [InlineData(5, true, 30.0)]
    [InlineData(3, false, 30.0)]
    public void The_planned_length_is_the_length_the_slots_were_made_for(int slots, bool fade, double length)
    {
        // The frames of the slots add up to more than the cover: the fades are on screen twice.
        var seconds = CoverRenderer.SlotSeconds(length, slots, fade, fps: 12);
        var images = Enumerable.Range(0, slots).Select(i => Image($"/media/{i}/fanart.jpg", seconds)).ToList();

        Assert.Equal(length * 1000, CoverRenderer.PlannedMilliseconds(Plan(images, fade)), tolerance: 0.001);
    }

    [Fact]
    public void Every_input_and_output_and_the_filter_graph_are_capped_at_two_threads()
    {
        var args = CoverRenderer.BuildArguments(Plan(new[]
        {
            Image("/media/a/fanart.jpg"),
            Video("/media/b/Film.mkv"),
            Video("/media/c/Film.mkv", hdr: true),
        }));
        var list = args.ToList();

        Assert.Equal(new[] { "-threads", "2" }, FfmpegRunner.ThreadLimit);
        Assert.Equal("2", list[list.IndexOf("-filter_complex_threads") + 1]);

        // Four inputs and two outputs, each with its own -threads, because the option belongs to one file.
        Assert.Equal(6, list.Count(arg => arg == "-threads"));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == "-threads")
            {
                Assert.Equal("2", list[i + 1]);
            }
        }
    }

    [Fact]
    public void Outputs_are_a_looping_webp_of_the_composed_frames_and_a_jpeg_of_the_first()
    {
        var list = CoverRenderer.BuildArguments(Plan(new[] { Image("/media/a/fanart.jpg") })).ToList();

        var webp = list.IndexOf("-c:v");
        Assert.Equal(new[] { "-c:v", "libwebp_anim", "-quality", "78", "-loop", "0", "-an" }, list.Skip(webp).Take(7));
        Assert.Equal("[anim]", list[list.IndexOf("-map") + 1]);

        var still = list.IndexOf("-frames:v");
        Assert.Equal(new[] { "-frames:v", "1", "-q:v", "3" }, list.Skip(still).Take(4));
        Assert.Equal("[st]", list[list.LastIndexOf("-map") + 1]);
    }

    [Fact]
    public void Numbers_do_not_depend_on_the_culture()
    {
        // A server set to Portuguese or German would otherwise write 0,333 and 1,167 into the filter graph.
        var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaCulture.NumberFormat.NumberDecimalSeparator = ",";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = commaCulture;
            var args = CoverRenderer.BuildArguments(
                Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/Film.mkv", start: 12.5) }));

            Assert.Contains("duration=0.333:offset=1.167", Graph(args));
            Assert.Contains("12.5", args);
            Assert.DoesNotContain(args, arg => arg.Contains("12,5", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Another_size_and_frame_rate_reach_every_filter()
    {
        var plan = Plan(new[] { Image("/media/a/fanart.jpg"), Video("/media/b/Film.mkv", seconds: 2) })
            with { Width = 960, Height = 540, Fps = 24 };
        var args = CoverRenderer.BuildArguments(plan);
        var graph = Graph(args);

        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=increase:reset_sar=1,crop=1920:1080", graph);
        Assert.Contains(":s=960x540:fps=24", graph);
        Assert.Contains("fps=24,scale=960:540:force_original_aspect_ratio=increase:reset_sar=1,crop=960:540", graph);

        // 1.5 s and 2 s at 24 fps are 36 and 48 frames; the fade is still four frames, a sixth of a second.
        Assert.Contains("d=36:", graph);
        Assert.Contains("tpad=stop_mode=clone:stop=48,trim=end_frame=48", graph);
        Assert.Contains("xfade=transition=fade:duration=0.167:offset=1.333", graph);
    }

    [Theory]
    [InlineData("no slots")]
    [InlineData("odd width")]
    [InlineData("odd height")]
    [InlineData("zero width")]
    [InlineData("zero frame rate")]
    [InlineData("relative slot path")]
    [InlineData("relative overlay path")]
    [InlineData("empty slot path")]
    [InlineData("slot with no duration")]
    [InlineData("slot with an unknown duration")]
    [InlineData("negative clip start")]
    [InlineData("slots too short to fade")]
    [InlineData("no final paths")]
    [InlineData("relative final path")]
    [InlineData("relative temp folder")]
    [InlineData("empty temp folder")]
    public void A_bad_plan_is_refused(string problem) =>
        Assert.ThrowsAny<ArgumentException>(() => CoverRenderer.BuildArguments(BadPlan(problem)));

    private static RenderPlan BadPlan(string problem)
    {
        var good = new[] { Image("/media/a/fanart.jpg"), Video("/media/b/Film.mkv") };
        return problem switch
        {
            "no slots" => Plan(Array.Empty<SlotInput>()),
            "odd width" => Plan(good) with { Width = 641 },
            "odd height" => Plan(good) with { Height = 359 },
            "zero width" => Plan(good) with { Width = 0 },
            "zero frame rate" => Plan(good) with { Fps = 0 },
            "relative slot path" => Plan(new[] { Image("fanart.jpg") }),
            "relative overlay path" => Plan(good) with { OverlayPngPath = "overlay.png" },
            "empty slot path" => Plan(new[] { Image(string.Empty) }),
            "slot with no duration" => Plan(new[] { Image("/media/a/fanart.jpg", seconds: 0) }),
            "slot with an unknown duration" => Plan(new[] { Image("/media/a/fanart.jpg", seconds: double.NaN) }),
            "negative clip start" => Plan(new[] { Video("/media/b/Film.mkv", start: -1) }),

            // 0.3 s is 4 frames at 12 fps: the fade would take the whole slot.
            "slots too short to fade" =>
                Plan(new[] { Image("/media/a/fanart.jpg", 0.3), Image("/media/b/fanart.jpg", 0.3) }),
            "no final paths" => Plan(good) with { AnimatedOut = string.Empty },
            "relative final path" => Plan(good) with { StillOut = "covers/ab12.jpg" },
            "relative temp folder" => Plan(good, tempFolder: "tmp"),
            "empty temp folder" => Plan(good, tempFolder: string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Not a known problem."),
        };
    }

    [Fact]
    public void The_timeline_of_a_webp_is_the_sum_of_its_frame_durations()
    {
        var (animated, milliseconds) = WebpTimeline.Read(WebpBuilder.Animated(83, 84, 83, 250, 16_777_215));

        Assert.True(animated);

        // 24 bits per frame: the largest value is 16777215 ms.
        Assert.Equal(83 + 84 + 83 + 250 + 16_777_215, milliseconds);
    }

    [Fact]
    public void The_padding_byte_after_a_chunk_of_odd_size_is_stepped_over()
    {
        // The next chunk starts one byte later than the size says; reading from the wrong place finds no frame, or
        // noise.
        var (animated, milliseconds) = WebpTimeline.Read(WebpBuilder.AnimatedAfterOddChunks(83, 84, 83));

        Assert.True(animated);
        Assert.Equal(250, milliseconds);
    }

    [Fact]
    public void A_still_webp_has_no_timeline()
    {
        // libwebp writes an animation of one frame (and so one whose frames are all the same) as a still image.
        var (animated, milliseconds) = WebpTimeline.Read(WebpBuilder.Still());

        Assert.False(animated);
        Assert.Equal(0, milliseconds);
    }

    [Theory]
    [InlineData("empty file", "is not a WebP file")]
    [InlineData("shorter than a header", "is not a WebP file")]
    [InlineData("not a riff file", "is not a WebP file")]
    [InlineData("not a webp file", "is not a WebP file")]
    [InlineData("riff size beyond the end", "ends before its header says it does")]
    [InlineData("cut in the middle of a frame", "ends before its header says it does")]
    [InlineData("chunk size beyond the end", "has a chunk that ends past the end of the file")]
    [InlineData("chunk size of four billion", "has a chunk that ends past the end of the file")]
    [InlineData("frame chunk too small", "has a frame chunk too small to hold a frame")]
    [InlineData("no image at all", "holds no image")]
    public void A_damaged_webp_is_refused_with_InvalidDataException_that_says_why(string problem, string reason)
    {
        var refused = Assert.Throws<InvalidDataException>(() => WebpTimeline.Read(WebpBuilder.Damaged(problem)));

        Assert.Equal(reason, refused.Message);
    }

    [Fact]
    public void A_webp_that_is_damaged_in_any_way_only_ever_throws_InvalidDataException()
    {
        // Bytes changed at random and files cut at random: whatever comes out, it is a result or InvalidDataException,
        // never an index, overflow or argument exception that would leave the render with an unexpected failure.
        var valid = WebpBuilder.Animated(83, 84, 83, 84, 83);
        var random = new Random(20261016);
        for (var round = 0; round < 5000; round++)
        {
            var damaged = (byte[])valid.Clone();
            for (var change = random.Next(1, 6); change > 0; change--)
            {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            if (random.Next(3) == 0)
            {
                damaged = damaged[..random.Next(damaged.Length)];
            }

            try
            {
                _ = WebpTimeline.Read(damaged);
            }
            catch (InvalidDataException)
            {
                // The expected way to refuse a file.
            }
        }
    }
}

/// <summary>
/// Builds WebP containers as libwebp writes them, with frame data that is not a picture: only the chunks the renderer
/// reads are real.
/// </summary>
internal static class WebpBuilder
{
    // 640x360 as the canvas: width and height minus one, 24 bits each, little endian.
    private static readonly byte[] CanvasSize = { 0x7F, 0x02, 0x00, 0x67, 0x01, 0x00 };

    /// <summary>An animated WebP with one frame for each duration, in milliseconds.</summary>
    public static byte[] Animated(params int[] frameMilliseconds) => Animated(oddChunks: false, frameMilliseconds);

    /// <summary>
    /// The same, with a colour profile of 3 bytes and metadata of 5 bytes ahead of the frames: chunks of odd size, each
    /// followed by a padding byte.
    /// </summary>
    public static byte[] AnimatedAfterOddChunks(params int[] frameMilliseconds) =>
        Animated(oddChunks: true, frameMilliseconds);

    private static byte[] Animated(bool oddChunks, int[] frameMilliseconds)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);

        // Flags: animation and alpha; then three reserved bytes and the canvas.
        Chunk(body, "VP8X"u8, new byte[] { 0x12, 0, 0, 0 }.Concat(CanvasSize).ToArray());

        // Background colour and loop count 0 (for ever).
        Chunk(body, "ANIM"u8, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 });
        if (oddChunks)
        {
            Chunk(body, "ICCP"u8, new byte[3]);
            Chunk(body, "EXIF"u8, new byte[5]);
        }

        foreach (var milliseconds in frameMilliseconds)
        {
            Chunk(body, "ANMF"u8, FrameHeader(milliseconds).Concat(SubChunk("VP8 "u8, new byte[10])).ToArray());
        }

        return Riff(body);
    }

    /// <summary>A still WebP: one image chunk and no animation.</summary>
    public static byte[] Still()
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        Chunk(body, "VP8 "u8, new byte[10]);
        return Riff(body);
    }

    /// <summary>A file that is damaged in one named way.</summary>
    public static byte[] Damaged(string problem)
    {
        var valid = Animated(83, 83, 83);

        // Offsets in a valid file: the RIFF header is 12 bytes, VP8X takes 18 (to 30), ANIM 14 (to 44), and the
        // first ANMF starts at 44 with its size field at 48.
        const int RiffSize = 4;
        const int FirstFrameSize = 48;
        switch (problem)
        {
            case "empty file":
                return Array.Empty<byte>();
            case "shorter than a header":
                return valid[..11];
            case "not a riff file":
                return Replace(valid, 0, "AVI "u8);
            case "not a webp file":
                return Replace(valid, 8, "WAVE"u8);
            case "riff size beyond the end":
                return Replace(
                    valid,
                    RiffSize,
                    U32(BinaryPrimitives.ReadUInt32LittleEndian(valid.AsSpan(RiffSize)) + 100));
            case "chunk size beyond the end":
                return Replace(valid, FirstFrameSize, U32((uint)valid.Length));
            case "chunk size of four billion":
                return Replace(valid, FirstFrameSize, U32(uint.MaxValue));
            case "frame chunk too small":
                return Replace(valid, FirstFrameSize, U32(10));
            case "cut in the middle of a frame":
                return valid[..^20];
            case "no image at all":
                using (var body = new MemoryStream())
                {
                    body.Write("WEBP"u8);
                    Chunk(body, "VP8X"u8, new byte[] { 0x10, 0, 0, 0 }.Concat(CanvasSize).ToArray());
                    return Riff(body);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(problem), problem, "Not a known problem.");
        }
    }

    private static byte[] FrameHeader(int milliseconds) =>
        new byte[] { 0, 0, 0, 0, 0, 0 }
            .Concat(CanvasSize)
            .Concat(new[] { (byte)milliseconds, (byte)(milliseconds >> 8), (byte)(milliseconds >> 16), (byte)0 })
            .ToArray();

    private static byte[] SubChunk(ReadOnlySpan<byte> tag, byte[] payload)
    {
        using var stream = new MemoryStream();
        Chunk(stream, tag, payload);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> tag, byte[] payload)
    {
        stream.Write(tag);
        stream.Write(U32((uint)payload.Length));
        stream.Write(payload);
        if ((payload.Length & 1) == 1)
        {
            stream.WriteByte(0);
        }
    }

    private static byte[] Riff(MemoryStream body)
    {
        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(U32((uint)body.Length));
        body.WriteTo(file);
        return file.ToArray();
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Replace(byte[] data, int offset, ReadOnlySpan<byte> bytes)
    {
        var copy = (byte[])data.Clone();
        bytes.CopyTo(copy.AsSpan(offset));
        return copy;
    }
}

/// <summary>Runs a test only where the fake ffmpeg (a shell script) and <c>/proc</c> exist.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs /bin/sh and /proc, so it runs on Linux only (like the SkiaSharp tests).";
        }
    }
}

/// <summary>The same for a test with data rows.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs /bin/sh and /proc, so it runs on Linux only (like the SkiaSharp tests).";
        }
    }
}

/// <summary>
/// Starts real processes: a shell script stands in for ffmpeg, so what the runner and the renderer do around the
/// process (options, priority, stall, budget, cancel, pipes, files) is tested without ffmpeg.
/// </summary>
public sealed class FfmpegRunnerTests : IDisposable
{
    // How long a test waits for something that should be quick before it calls it stuck.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // 18 frames of 83 ms: what the plan of PlanIn (one picture, 1.5 s, 12 fps) comes to, a few milliseconds short.
    private static readonly int[] OnTime = Enumerable.Repeat(83, 18).ToArray();

    private readonly string _root = Directory.CreateTempSubdirectory("ffmpeg-runner-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Stands in for <see cref="IMediaEncoder"/>: only the ffmpeg path is ever asked for.</summary>
    public class EncoderStub : DispatchProxy
    {
        public string EncoderPath { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_EncoderPath"
                ? EncoderPath
                : throw new NotSupportedException($"{targetMethod?.Name} is not part of this stub.");
    }

    /// <summary>Keeps what is logged, and says whether debug messages are wanted.</summary>
    private sealed class RecordingLogger : ILogger<FfmpegRunner>
    {
        private readonly bool _debug;

        public RecordingLogger(bool debug) => _debug = debug;

        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || _debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static FfmpegRunner RunnerAt(
        string ffmpegPath,
        RunnerLimits? limits = null,
        ILogger<FfmpegRunner>? log = null)
    {
        var encoder = DispatchProxy.Create<IMediaEncoder, EncoderStub>();
        ((EncoderStub)(object)encoder).EncoderPath = ffmpegPath;
        log ??= NullLogger<FfmpegRunner>.Instance;

        // The public constructor, with its documented limits, unless a test needs short ones.
        return limits is null ? new FfmpegRunner(encoder, log) : new FfmpegRunner(encoder, log, limits);
    }

    private string Path(string name) => System.IO.Path.Combine(_root, name);

    /// <summary>
    /// A shell script that behaves like ffmpeg for one test. <paramref name="body"/> runs with the arguments in
    /// <c>$@</c>.
    /// </summary>
    private FfmpegRunner FakeFfmpeg(string body, RunnerLimits? limits = null, ILogger<FfmpegRunner>? log = null)
    {
        var script = Path($"fake-ffmpeg-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return RunnerAt(script, limits, log);
    }

    private static RunnerLimits Limits(
        TimeSpan? processorTimeBudget = null,
        TimeSpan? frameStall = null,
        TimeSpan? stopGrace = null) =>
        RunnerLimits.Default with
        {
            ProcessorTimeBudget = processorTimeBudget ?? RunnerLimits.Default.ProcessorTimeBudget,
            FrameStall = frameStall ?? RunnerLimits.Default.FrameStall,
            StopGrace = stopGrace ?? RunnerLimits.Default.StopGrace,
        };

    // The shell code that finds the two output files in the arguments, as ffmpeg would: the one after -an and the last
    // one.
    private const string FindOutputs = "prev=''; webp=''; jpg=''; for a in \"$@\"; do if [ \"$prev\" = '-an' ]; then "
        + "webp=\"$a\"; fi; prev=\"$a\"; jpg=\"$a\"; done";

    private string RecordArguments() => $"printf '%s\\n' \"$@\" > '{Path("arguments.txt")}'";

    private string[] RecordedArguments() => File.ReadAllLines(Path("arguments.txt"));

    /// <summary>
    /// Writes a WebP with these frame durations and returns the shell code that copies it to the animated output.
    /// </summary>
    private string CopyWebp(params int[] frameMilliseconds) => CopyWebp(WebpBuilder.Animated(frameMilliseconds));

    private string CopyWebp(byte[] webp)
    {
        var file = Path($"crafted-{Guid.NewGuid():N}.webp");
        File.WriteAllBytes(file, webp);
        return $"cp '{file}' \"$webp\"";
    }

    // An ffmpeg that writes a cover as it should: the animated file, then the still.
    private string Writes(string copyWebp) => $"{FindOutputs}; {copyWebp}; printf 'JFIFfake' > \"$jpg\"";

    private RenderPlan PlanIn(string? tempFolder)
    {
        Directory.CreateDirectory(Path("media"));
        return new RenderPlan(
            new[] { new SlotInput(SlotKind.Image, Path("media/fanart.jpg"), 0, 1.5, false) },
            Path("overlay.png"),
            640,
            360,
            12,
            true,
            Path("covers/lib/ab12.webp"),
            Path("covers/lib/ab12.jpg"),
            tempFolder);
    }

    // Three pictures that fade into one another: 27, 26 and 27 frames, 80 less two fades of 4 is 72 frames, 6 s.
    private RenderPlan ThreeSlotsIn(string? tempFolder) =>
        PlanIn(tempFolder) with
        {
            Slots = Enumerable.Range(0, 3)
                .Select(i => new SlotInput(
                    SlotKind.Image,
                    Path($"media/{i}.jpg"),
                    0,
                    CoverRenderer.SlotSeconds(6, 3, fade: true, fps: 12),
                    false))
                .ToList(),
        };

    private static bool IsRunning(int pid)
    {
        try
        {
            // A killed process may stay as a zombie until its parent collects it; that is dead for our purpose.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] is not ('Z' or 'X');
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(until.Elapsed < Patience, "Timed out waiting for the condition.");
            await Task.Delay(25);
        }
    }

    private int ReadPid(string file) => int.Parse(File.ReadAllText(Path(file)).Trim(), CultureInfo.InvariantCulture);

    // A pid file that is missing, empty or without its end of line was not written (yet): there is no process to look
    // for.
    private int? TryReadPid(string file)
    {
        try
        {
            var text = File.ReadAllText(Path(file));
            return text.EndsWith('\n')
                && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void KillIfRunning(int? pid)
    {
        if (pid is not { } id)
        {
            return;
        }

        try
        {
            Process.GetProcessById(id).Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Not running any more: nothing to clean up.
        }
    }

    [LinuxFact]
    public async Task Run_starts_every_ffmpeg_with_the_standard_options_before_the_callers_own()
    {
        var runner = FakeFfmpeg(RecordArguments());

        var (exit, _) = await runner.RunAsync(
            new[] { "-loglevel", "info", "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(0, exit);

        // A caller that needs more output than errors (scene cut detection) can say so: the last -loglevel wins.
        Assert.Equal(
            new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-loglevel", "info", "-i", "/media/a.mkv" },
            RecordedArguments());
    }

    [LinuxFact]
    public async Task Run_returns_the_exit_code_and_what_ffmpeg_said()
    {
        var runner = FakeFfmpeg("echo 'first' >&2; echo 'second' >&2; exit 3");

        var (exit, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Equal("first\nsecond", errors);
    }

    [LinuxFact]
    public async Task Run_keeps_only_the_end_of_a_very_long_error_output()
    {
        var runner = FakeFfmpeg("i=1; while [ $i -le 1000 ]; do echo \"line $i\" >&2; i=$((i+1)); done; exit 1");

        var (_, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        var lines = errors.Split('\n');
        Assert.True(lines.Length <= 200, $"{lines.Length} lines kept");
        Assert.Equal("line 1000", lines[^1]);
        Assert.Equal("line 801", lines[0]);
    }

    [LinuxFact]
    public async Task The_first_line_of_the_error_output_is_kept_apart_from_the_tail()
    {
        var runner = FakeFfmpeg("i=1; while [ $i -le 300 ]; do echo \"line $i\" >&2; i=$((i+1)); done; exit 1");

        var run = await runner.RunDetailedAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // The tail has dropped it by now, and it is the line that says why ffmpeg failed.
        Assert.Equal("line 1", run.FirstLine);
        var tail = run.Errors.Split('\n');
        Assert.Equal(200, tail.Length);
        Assert.Equal("line 101", tail[0]);
        Assert.Equal("line 300", tail[^1]);
    }

    [LinuxFact]
    public async Task Run_lowers_the_priority_of_ffmpeg_to_idle()
    {
        // The script waits a moment, so the priority has been set by the time it looks at its own.
        var runner = FakeFfmpeg(
            $"sleep 0.5; sed -E 's/^.*\\) //' /proc/$$/stat | cut -d' ' -f17 > '{Path("nice.txt")}'");

        await runner.RunAsync(new[] { "-i", "/media/a.mkv" }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("19", File.ReadAllText(Path("nice.txt")).Trim());
    }

    [LinuxFact]
    public async Task Run_stops_an_ffmpeg_that_shows_no_sign_of_life_and_everything_it_started()
    {
        // The script writes its process ids in its first milliseconds; the limit is two seconds, so it is long done.
        var runner = FakeFfmpeg($"echo $$ > '{Path("parent.pid")}'; sleep 120 & echo $! > '{Path("child.pid")}'; wait");

        var stopwatch = Stopwatch.StartNew();
        var (exit, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(stopwatch.Elapsed < Patience, "The stalled ffmpeg was not stopped in time.");
        Assert.Equal(-1, exit);
        Assert.Equal("ffmpeg showed no sign of life for 2 seconds, so it was stopped.", errors);

        // A pid file that was not written is not looked for (the script was killed before it got that far), but
        // the parent's is there in any machine that is not stuck.
        var parent = Assert.NotNull(TryReadPid("parent.pid"));
        var child = TryReadPid("child.pid");
        await WaitUntilAsync(() => !IsRunning(parent) && (child is not { } alive || !IsRunning(alive)));
    }

    [LinuxFact]
    public async Task Run_leaves_a_quiet_but_talking_ffmpeg_alone()
    {
        // It uses almost no processor time between lines, but each line is a sign of life: it runs for twice the
        // stall limit and is never more than a sixth of it without one.
        var runner = FakeFfmpeg("i=0; while [ $i -lt 12 ]; do echo \"tick $i\" >&2; sleep 0.25; i=$((i+1)); done");

        var (exit, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromMilliseconds(1500),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.EndsWith("tick 11", errors);
    }

    [LinuxFact]
    public async Task Run_counts_processor_time_as_a_sign_of_life()
    {
        // No output at all. It works for 6 s, three times the stall limit of 2 s, and then ends: it can only get
        // through if each use of the processor starts the clock again. It runs at idle priority, so on a busy host
        // it may go without the processor for a while: the limit leaves room for a gap of up to two seconds.
        var runner = FakeFfmpeg("end=$(( $(date +%s%N) + 6000000000 )); while [ $(date +%s%N) -lt $end ]; do :; done");

        var (exit, _) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [LinuxFact]
    public async Task Run_measures_silence_from_the_last_use_of_the_processor_and_not_from_the_start()
    {
        // It works for 4.5 s and then waits 2 s, 6.5 s in all and longer than the stall limit of 6 s, with nothing on
        // stderr. It gets through only because the 2 s of waiting are counted from the last time it used the
        // processor: counted from its start, 6 s of silence had passed before it ended. It runs at idle priority, so
        // while other tests keep the processor busy it may get none of it for a while: the limit leaves room for that.
        var runner = FakeFfmpeg(
            "end=$(( $(date +%s%N) + 4500000000 )); while [ $(date +%s%N) -lt $end ]; do :; done; sleep 2");

        var (exit, _) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(6),
            CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [LinuxFact]
    public async Task Run_stops_an_ffmpeg_that_uses_more_processor_time_than_its_budget()
    {
        // A spinning ffmpeg is never silent for the stall rule, because it uses the processor. It is finite here, 30 s,
        // so that a runner that ignored the budget would end the test and not leave it spinning.
        var runner = FakeFfmpeg(
            $"echo $$ > '{Path("parent.pid")}'; end=$(( $(date +%s) + 30 )); "
            + "while [ $(date +%s) -lt $end ]; do :; done",
            Limits(processorTimeBudget: TimeSpan.FromMilliseconds(300)));

        var (exit, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(-1, exit);
        Assert.Equal("ffmpeg used 0.3 seconds of processor time without finishing and was stopped.", errors);
        var parent = Assert.NotNull(TryReadPid("parent.pid"));
        await WaitUntilAsync(() => !IsRunning(parent));
    }

    [LinuxFact]
    public async Task The_processor_time_budget_counts_work_not_waiting()
    {
        // Waiting for 1.5 s is five times the budget on the clock and nothing in processor time: a render that is
        // starved at idle priority on a busy host must not be stopped for it.
        var runner = FakeFfmpeg("sleep 1.5", Limits(processorTimeBudget: TimeSpan.FromMilliseconds(300)));

        var (exit, _) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void Every_run_has_a_budget_of_fifteen_minutes_of_processor_time_unless_a_test_says_otherwise()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), FfmpegRunner.DefaultProcessorTimeBudget);
        Assert.Equal(FfmpegRunner.DefaultProcessorTimeBudget, RunnerLimits.Default.ProcessorTimeBudget);
    }

    [LinuxFact]
    public async Task Cancelling_a_run_stops_ffmpeg_and_everything_it_started()
    {
        var runner = FakeFfmpeg($"echo $$ > '{Path("parent.pid")}'; sleep 120 & echo $! > '{Path("child.pid")}'; wait");
        using var cancel = new CancellationTokenSource();

        var run = runner.RunAsync(new[] { "-i", "/media/a.mkv" }, TimeSpan.FromMinutes(5), cancel.Token);
        await WaitUntilAsync(
            () => File.Exists(Path("child.pid")) && File.ReadAllText(Path("child.pid")).EndsWith('\n'));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await WaitUntilAsync(() => !IsRunning(ReadPid("parent.pid")) && !IsRunning(ReadPid("child.pid")));
    }

    [LinuxFact]
    public async Task Run_reports_an_ffmpeg_that_cannot_be_started_instead_of_throwing()
    {
        var (exit, errors) = await RunnerAt(Path("no-such-ffmpeg")).RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(-1, exit);
        Assert.Contains("could not be started", errors);
    }

    [LinuxFact]
    public async Task Run_reports_a_missing_ffmpeg_path()
    {
        var (exit, errors) = await RunnerAt(string.Empty).RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(-1, exit);
        Assert.Contains("no ffmpeg", errors);
    }

    [LinuxFact]
    public async Task Run_refuses_a_stall_limit_that_is_not_positive() =>
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () =>
                RunnerAt(Path("whatever")).RunAsync(
                    new[] { "-i", "/media/a.mkv" },
                    TimeSpan.Zero,
                    CancellationToken.None));

    [LinuxFact]
    public async Task Run_reads_a_flood_of_error_output_while_ffmpeg_runs()
    {
        // 8192 lines of about 70 bytes are 570 KB: nine times what a pipe holds. A runner that read only once ffmpeg
        // had ended would leave it blocked on a full pipe, showing no sign of life, and fail at the two-second limit.
        var runner = FakeFfmpeg(
            "yes 'a line of error output that is long enough to fill a pipe in a hurry' | head -n 8192 >&2");

        var (exit, errors) = await runner.RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(200, errors.Split('\n').Length);
    }

    [LinuxFact]
    public async Task The_command_line_is_logged_only_when_debug_logging_is_enabled()
    {
        var quiet = new RecordingLogger(debug: false);
        var loud = new RecordingLogger(debug: true);

        await FakeFfmpeg("exit 0", log: quiet).RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await FakeFfmpeg("exit 0", log: loud).RunAsync(
            new[] { "-i", "/media/a.mkv" },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // Joining the arguments costs something on every run, so it is not done for a message nobody reads.
        Assert.DoesNotContain(quiet.Messages, message => message.Contains("running ffmpeg", StringComparison.Ordinal));
        Assert.Contains(
            loud.Messages,
            message =>
                message.Contains(
                    "running ffmpeg -hide_banner -nostdin -y -loglevel error -i /media/a.mkv",
                    StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task ReadFrame_returns_what_ffmpeg_writes_to_its_output()
    {
        var runner = FakeFfmpeg(RecordArguments() + "; printf 'PNGBYTES'");

        var frame = await runner.ReadFrameAsync("/media/Film (2020).mkv", 61.5, 160, CancellationToken.None);

        Assert.Equal("PNGBYTES"u8.ToArray(), frame);

        // The seek comes before the input, so ffmpeg jumps to the moment instead of decoding up to it.
        var args = RecordedArguments().ToList();
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("61.5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("/media/Film (2020).mkv", args[args.IndexOf("-i") + 1]);
        Assert.Equal("1", args[args.IndexOf("-frames:v") + 1]);

        // The height follows the shape of the picture as it is shown, not as its pixels are stored.
        Assert.Equal("scale=160:-2:reset_sar=1", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("rgb24", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("png", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("pipe:1", args[^1]);

        // Decoder, encoder and filter threads are all capped.
        Assert.Equal(2, args.Count(arg => arg == "-threads"));
        Assert.Equal("2", args[args.IndexOf("-threads") + 1]);
        Assert.Equal("2", args[args.LastIndexOf("-threads") + 1]);
        Assert.Equal("2", args[args.IndexOf("-filter_threads") + 1]);
    }

    [LinuxFact]
    public async Task ReadFrame_reads_a_large_output_while_ffmpeg_runs()
    {
        // A megabyte on stdout is sixteen times what a pipe holds. A runner that read it only after ffmpeg ended
        // would leave ffmpeg blocked, with no sign of life, and fail at the two-second limit of this test.
        var runner = FakeFfmpeg("head -c 1048576 /dev/zero", Limits(frameStall: TimeSpan.FromSeconds(2)));

        var frame = await runner.ReadFrameAsync("/media/Film.mkv", 10, 160, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(1_048_576, frame.Length);
    }

    [LinuxFact]
    public async Task ReadFrame_returns_null_when_a_child_keeps_the_pipes_open_after_ffmpeg_has_ended()
    {
        // ffmpeg is gone, but something it started still holds its output: what was read may be half a picture.
        var runner = FakeFfmpeg(
            $"sleep 30 & echo $! > '{Path("child.pid")}'; printf 'PNGBYTES'",
            Limits(stopGrace: TimeSpan.FromMilliseconds(300)));
        try
        {
            Assert.Null(await runner.ReadFrameAsync("/media/Film.mkv", 10, 160, CancellationToken.None));
        }
        finally
        {
            KillIfRunning(TryReadPid("child.pid"));
        }
    }

    [LinuxFact]
    public async Task ReadFrame_returns_null_when_ffmpeg_fails()
    {
        var runner = FakeFfmpeg("echo 'Invalid data found' >&2; exit 1");

        Assert.Null(await runner.ReadFrameAsync("/media/Film.mkv", 10, 160, CancellationToken.None));
    }

    [LinuxFact]
    public async Task ReadFrame_returns_null_when_ffmpeg_writes_nothing()
    {
        // A moment past the end of the file: ffmpeg is happy and there is no frame.
        var runner = FakeFfmpeg("exit 0");

        Assert.Null(await runner.ReadFrameAsync("/media/Film.mkv", 99999, 160, CancellationToken.None));
    }

    [LinuxFact]
    public async Task ReadFrame_refuses_what_is_not_a_frame_request()
    {
        var runner = FakeFfmpeg("exit 0");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("Film.mkv", 10, 160, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("-i", 10, 160, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("/media/Film.mkv", -1, 160, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("/media/Film.mkv", double.NaN, 160, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("/media/Film.mkv", 10, 0, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => runner.ReadFrameAsync("/media/Film.mkv", 10, 100000, CancellationToken.None));
    }

    [LinuxFact]
    public async Task Render_moves_both_files_to_their_final_paths_after_ffmpeg_succeeds()
    {
        var temp = Path("tmp");
        var runner = FakeFfmpeg($"{RecordArguments()}; {Writes(CopyWebp(OnTime))}");
        var plan = PlanIn(temp);

        await CoverRenderer.RenderAsync(runner, plan, CancellationToken.None);

        // The folders for the final files were made by the renderer, and the temp folder holds nothing.
        Assert.Equal(WebpBuilder.Animated(OnTime), File.ReadAllBytes(plan.AnimatedOut));
        Assert.Equal("JFIFfake", File.ReadAllText(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));

        // ffmpeg was told to write into the temp folder, never to the final paths.
        var args = RecordedArguments();
        Assert.DoesNotContain(plan.AnimatedOut, args);
        Assert.DoesNotContain(plan.StillOut, args);
        Assert.Equal(temp, System.IO.Path.GetDirectoryName(args[^1]));
        Assert.Equal(CoverRenderer.BuildArguments(plan), args.Skip(5).ToArray());
    }

    [LinuxFact]
    public async Task Render_without_a_temp_folder_writes_beside_the_final_files_and_leaves_only_them()
    {
        var runner = FakeFfmpeg(Writes(CopyWebp(OnTime)));
        var plan = PlanIn(null);

        await CoverRenderer.RenderAsync(runner, plan, CancellationToken.None);

        Assert.Equal(
            new[] { plan.AnimatedOut, plan.StillOut }.Order(),
            Directory.GetFiles(System.IO.Path.GetDirectoryName(plan.AnimatedOut)!).Order());
    }

    [LinuxFact]
    public async Task Render_passes_a_cover_whose_length_is_the_planned_one_to_the_frame()
    {
        // 18 frames of 12 fps are 1500 ms; one frame is 83.3 ms. 1583 is within one frame, and 1584 is not.
        var cases = new[] { (1500, true), (1417, true), (1583, true), (1584, false), (1416, false) };
        foreach (var (milliseconds, passes) in cases)
        {
            var plan = PlanIn(Path($"tmp-{milliseconds}")) with
            {
                AnimatedOut = Path($"covers/{milliseconds}.webp"),
                StillOut = Path($"covers/{milliseconds}.jpg"),
            };
            var runner = FakeFfmpeg(Writes(CopyWebp(1000, milliseconds - 1000)));

            if (passes)
            {
                await CoverRenderer.RenderAsync(runner, plan, CancellationToken.None);
                Assert.True(File.Exists(plan.AnimatedOut), $"{milliseconds} ms should pass");
            }
            else
            {
                await Assert.ThrowsAsync<CoverRenderException>(
                    () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));
                Assert.False(File.Exists(plan.AnimatedOut), $"{milliseconds} ms should not pass");
            }
        }
    }

    [LinuxFact]
    public async Task Render_adds_up_the_planned_length_of_slots_that_fade_into_one_another()
    {
        // Three slots of 27, 26 and 27 frames fade for four frames twice: 72 frames, 6000 ms, not the 80 frames of the
        // slots.
        var passing = ThreeSlotsIn(Path("tmp"));
        await CoverRenderer.RenderAsync(
            FakeFfmpeg(Writes(CopyWebp(Enumerable.Repeat(83, 72).ToArray()))),
            passing,
            CancellationToken.None);
        Assert.True(File.Exists(passing.AnimatedOut));

        var tooLong = ThreeSlotsIn(Path("tmp")) with
        {
            AnimatedOut = Path("covers/long.webp"),
            StillOut = Path("covers/long.jpg"),
        };
        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () =>
                CoverRenderer.RenderAsync(
                    FakeFfmpeg(Writes(CopyWebp(Enumerable.Repeat(83, 80).ToArray()))),
                    tooLong,
                    CancellationToken.None));
        Assert.Contains("6.6 s long instead of 6 s", failure.Message);
    }

    [LinuxFact]
    public async Task Render_refuses_a_cover_that_came_out_shorter_than_planned_and_publishes_nothing()
    {
        // What a clip that starts past the end of its file leaves: ffmpeg exits 0 with no message, and the slots after
        // it are gone.
        var temp = Path("tmp");
        var plan = ThreeSlotsIn(temp);
        var runner = FakeFfmpeg(
            Writes(CopyWebp(Enumerable.Repeat(83, 28).ToArray())) + "; echo 'File ended prematurely' >&2");

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Equal(
            "The cover came out 2.3 s long instead of 6 s: a clip gave fewer frames than planned.",
            failure.Message);
        Assert.Equal(0, failure.ExitCode);
        Assert.Equal("File ended prematurely", failure.Errors);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task Render_refuses_a_cover_that_came_out_longer_than_planned_and_publishes_nothing()
    {
        // What an animated picture in a picture slot leaves: a whole zoom for each of its frames.
        var temp = Path("tmp");
        var plan = ThreeSlotsIn(temp);
        var runner = FakeFfmpeg(Writes(CopyWebp(Enumerable.Repeat(83, 315).ToArray())));

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Equal(
            "The cover came out 26.1 s long instead of 6 s: a picture or clip gave more frames than planned.",
            failure.Message);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxTheory]
    [InlineData("shorter than a header")]
    [InlineData("not a riff file")]
    [InlineData("not a webp file")]
    [InlineData("riff size beyond the end")]
    [InlineData("chunk size beyond the end")]
    [InlineData("chunk size of four billion")]
    [InlineData("frame chunk too small")]
    [InlineData("cut in the middle of a frame")]
    [InlineData("no image at all")]
    public async Task Render_refuses_a_webp_that_cannot_be_read_with_a_render_exception_and_publishes_nothing(
        string problem)
    {
        // Whatever ffmpeg left in the file, the failure is a CoverRenderException: never another type.
        var temp = Path("tmp");
        var plan = PlanIn(temp);
        var runner = FakeFfmpeg(Writes(CopyWebp(WebpBuilder.Damaged(problem))));

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.StartsWith("ffmpeg wrote an animated WebP that cannot be used: it ", failure.Message);
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task Render_accepts_a_still_webp_for_a_single_picture()
    {
        // The name-only cover is one flat colour: its frames are all the same, so libwebp writes one still image.
        // Measured with jellyfin-ffmpeg 8.1.3: a single VP8 chunk of 2876 bytes, no ANMF, no timeline to check.
        var plan = PlanIn(Path("tmp"));
        var runner = FakeFfmpeg(Writes(CopyWebp(WebpBuilder.Still())));

        await CoverRenderer.RenderAsync(runner, plan, CancellationToken.None);

        Assert.Equal(WebpBuilder.Still(), File.ReadAllBytes(plan.AnimatedOut));
        Assert.True(File.Exists(plan.StillOut));
    }

    [LinuxFact]
    public async Task Render_refuses_a_still_webp_for_several_slots()
    {
        // Several slots that all come out the same, or all but one gone: either way it is not the cover that was asked
        // for.
        var temp = Path("tmp");
        var plan = ThreeSlotsIn(temp);
        var runner = FakeFfmpeg(Writes(CopyWebp(WebpBuilder.Still())));

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Equal(
            "The cover came out as a still picture instead of an animation: all but one slot gave no frames, or every "
            + "frame was the same.",
            failure.Message);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxTheory]
    [InlineData("a file at the animated path")]
    [InlineData("a file at the still path")]
    [InlineData("a folder at the animated path")]
    [InlineData("a folder at the still path")]
    public async Task Render_refuses_final_paths_that_already_exist_before_anything_is_created_or_started(
        string existing)
    {
        // A cover is both its files or neither. With a file already there, a failure to move the second one could not
        // be undone: the first would have replaced a cover's file and the second would be missing.
        var temp = Path("tmp");
        var plan = PlanIn(temp);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(plan.AnimatedOut)!);
        var occupied = existing.Contains("animated", StringComparison.Ordinal) ? plan.AnimatedOut : plan.StillOut;
        if (existing.StartsWith("a folder", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(occupied);
        }
        else
        {
            File.WriteAllText(occupied, "somebody's cover");
        }

        var runner = FakeFfmpeg(RecordArguments());

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Contains(occupied, refused.Message);
        Assert.Contains("already exists", refused.Message);
        Assert.False(File.Exists(Path("arguments.txt")), "ffmpeg was started");
        Assert.False(Directory.Exists(temp), "the temp folder was created");
        Assert.True(File.Exists(occupied) || Directory.Exists(occupied), "what was there is gone");
        if (!existing.StartsWith("a folder", StringComparison.Ordinal))
        {
            Assert.Equal("somebody's cover", File.ReadAllText(occupied));
        }
    }

    [LinuxFact]
    public async Task Render_does_not_replace_a_file_that_appeared_at_a_final_path_while_ffmpeg_ran()
    {
        var temp = Path("tmp");
        var plan = PlanIn(temp);

        // Someone puts a file where the still has to go while ffmpeg runs: the animated file has already moved by then.
        var runner = FakeFfmpeg(
            $"{Writes(CopyWebp(OnTime))}; mkdir -p '{System.IO.Path.GetDirectoryName(plan.StillOut)}'; printf "
            + $"'somebody else' > '{plan.StillOut}'");

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Contains("could not be stored", failure.Message);
        Assert.IsAssignableFrom<IOException>(failure.InnerException);
        Assert.False(File.Exists(plan.AnimatedOut), "the animated file was left on its own");
        Assert.Equal("somebody else", File.ReadAllText(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task Render_does_not_delete_a_file_that_appeared_at_the_animated_path_while_ffmpeg_ran()
    {
        var temp = Path("tmp");
        var plan = PlanIn(temp);

        // Someone puts a file where the animated WebP has to go while ffmpeg runs: nothing of ours was placed there,
        // so there is nothing to take away again, and the file is theirs.
        var runner = FakeFfmpeg(
            $"{Writes(CopyWebp(OnTime))}; mkdir -p '{System.IO.Path.GetDirectoryName(plan.AnimatedOut)}'; printf "
            + $"'somebody else' > '{plan.AnimatedOut}'");

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Contains("could not be stored", failure.Message);
        Assert.Equal("somebody else", File.ReadAllText(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task Render_reports_a_temp_folder_that_cannot_be_created_in_its_own_words()
    {
        File.WriteAllText(Path("blocker"), "a file where a folder is needed");
        var plan = PlanIn(Path("blocker/tmp"));
        var runner = FakeFfmpeg(RecordArguments());

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.StartsWith("The folder for the render in progress could not be created: ", failure.Message);
        Assert.DoesNotContain("stored", failure.Message);
        Assert.Equal(FfmpegRunner.NotCompleted, failure.ExitCode);
        Assert.IsAssignableFrom<IOException>(failure.InnerException);
        Assert.False(File.Exists(Path("arguments.txt")), "ffmpeg was started");
    }

    [LinuxFact]
    public async Task Failed_render_throws_with_the_last_20_lines_and_leaves_no_file_behind()
    {
        var temp = Path("tmp");
        var runner = FakeFfmpeg(
            $"{FindOutputs}; printf 'RIFFhalf' > \"$webp\"; i=1; while [ $i -le 30 ]; do echo \"error line $i\" >&2; "
            + "i=$((i+1)); done; exit 1");
        var plan = PlanIn(temp);

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Equal(1, failure.ExitCode);
        Assert.Equal(string.Join('\n', Enumerable.Range(11, 20).Select(n => $"error line {n}")), failure.Errors);
        Assert.Equal("ffmpeg failed with exit code 1: error line 1", failure.Message);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task The_reason_is_the_first_line_even_when_more_than_200_lines_followed_it()
    {
        // The runner keeps the last 200 lines; the line that says why is the first of them all.
        var runner = FakeFfmpeg(
            "echo '[in#0 @ 0x55d0c0] Error opening input: No such file or directory' >&2; i=1; while [ $i -le 400 ]; "
            + "do echo \"consequence $i\" >&2; i=$((i+1)); done; exit 254");

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, PlanIn(Path("tmp")), CancellationToken.None));

        Assert.Equal(
            "ffmpeg failed with exit code 254: Error opening input: No such file or directory",
            failure.Message);
        Assert.Equal(string.Join('\n', Enumerable.Range(381, 20).Select(n => $"consequence {n}")), failure.Errors);
    }

    [LinuxFact]
    public async Task Failed_render_names_the_cause_ffmpeg_printed_first_without_its_address()
    {
        // What ffmpeg 8.1.3 says about a file that is not there, and about a damaged one: the cause comes first, the
        // consequences after it, and every line starts with the component and its address.
        var missing = FakeFfmpeg(
            "echo '[in#1 @ 0x7bcac740e280] Error opening input: No such file or directory' >&2; "
            + "echo 'Error opening input file /media/x.jpg.' >&2; "
            + "echo 'Error opening input files: No such file or directory' >&2; exit 254");
        var damaged = FakeFfmpeg(
            "echo '[vist#1:0/mjpeg @ 0x788d44ab9480] [dec:mjpeg @ 0x788d44a0ec80] Error submitting packet to decoder: "
            + "Invalid data found when processing input' >&2; "
            + "echo '[out#1/image2 @ 0x788d44a77640] Nothing was written into output file, because at least one of its "
            + "streams received no packets.' >&2; exit 69");

        var first = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(missing, PlanIn(Path("tmp")), CancellationToken.None));
        var second = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(damaged, PlanIn(Path("tmp")), CancellationToken.None));

        Assert.Equal("ffmpeg failed with exit code 254: Error opening input: No such file or directory", first.Message);
        Assert.Equal(
            "ffmpeg failed with exit code 69: Error submitting packet to decoder: Invalid data found when processing "
            + "input",
            second.Message);

        // The lines themselves are kept as ffmpeg wrote them.
        Assert.StartsWith("[in#1 @ 0x7bcac740e280] Error opening input", first.Errors);
        Assert.Equal(3, first.Errors.Split('\n').Length);
    }

    [LinuxFact]
    public async Task Render_fails_when_ffmpeg_exits_cleanly_but_writes_no_file()
    {
        var temp = Path("tmp");
        var plan = PlanIn(temp);

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(FakeFfmpeg("exit 0"), plan, CancellationToken.None));

        Assert.Contains("did not write", failure.Message);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
    }

    [LinuxFact]
    public async Task Render_fails_when_one_file_is_empty_and_publishes_neither()
    {
        var temp = Path("tmp");
        var runner = FakeFfmpeg($"{FindOutputs}; {CopyWebp(OnTime)}; : > \"$jpg\"");
        var plan = PlanIn(temp);

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Contains("empty", failure.Message);
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
    }

    [LinuxFact]
    public async Task Render_fails_with_the_reason_when_ffmpeg_cannot_be_started()
    {
        var plan = PlanIn(Path("tmp"));

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(RunnerAt(Path("no-such-ffmpeg")), plan, CancellationToken.None));

        Assert.Equal(-1, failure.ExitCode);
        Assert.Contains("could not be started", failure.Message);
    }

    [LinuxFact]
    public async Task Render_reports_an_ffmpeg_that_was_stopped_for_its_processor_time_in_its_own_words()
    {
        var plan = PlanIn(Path("tmp"));

        // It said something before it was stopped: the reason is what the runner says about stopping it, which comes
        // last.
        var runner = FakeFfmpeg(
            "echo 'Stream mapping:' >&2; end=$(( $(date +%s) + 30 )); while [ $(date +%s) -lt $end ]; do :; done",
            Limits(processorTimeBudget: TimeSpan.FromMilliseconds(300)));

        var failure = await Assert.ThrowsAsync<CoverRenderException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.Equal("ffmpeg used 0.3 seconds of processor time without finishing and was stopped.", failure.Message);
        Assert.Equal(
            "Stream mapping:\nffmpeg used 0.3 seconds of processor time without finishing and was stopped.",
            failure.Errors);
        Assert.Equal(FfmpegRunner.NotCompleted, failure.ExitCode);
        Assert.False(File.Exists(plan.AnimatedOut));
    }

    [LinuxFact]
    public async Task Cancelled_render_stops_ffmpeg_and_removes_its_half_written_files()
    {
        var temp = Path("tmp");
        var runner = FakeFfmpeg(
            $"{FindOutputs}; printf 'RIFFhalf' > \"$webp\"; echo $$ > '{Path("parent.pid")}'; sleep 120 & wait");
        var plan = PlanIn(temp);
        using var cancel = new CancellationTokenSource();

        var render = CoverRenderer.RenderAsync(runner, plan, cancel.Token);
        await WaitUntilAsync(
            () => File.Exists(Path("parent.pid")) && File.ReadAllText(Path("parent.pid")).EndsWith('\n'));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
        await WaitUntilAsync(() => !IsRunning(ReadPid("parent.pid")));
        Assert.Empty(Directory.GetFileSystemEntries(temp));
        Assert.False(File.Exists(plan.AnimatedOut));
    }

    [LinuxFact]
    public async Task A_bad_plan_is_refused_before_anything_is_started_or_written()
    {
        var temp = Path("tmp");
        var runner = FakeFfmpeg(RecordArguments());
        var plan = PlanIn(temp) with { Slots = Array.Empty<SlotInput>() };

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => CoverRenderer.RenderAsync(runner, plan, CancellationToken.None));

        Assert.False(Directory.Exists(temp));
        Assert.False(File.Exists(Path("arguments.txt")));
    }
}
