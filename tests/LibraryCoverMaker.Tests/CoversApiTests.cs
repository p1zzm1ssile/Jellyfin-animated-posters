using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Api;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// What the admin API decides without Jellyfin: which inputs it accepts, which uploads it believes, and the applier
/// call that returns a library to Jellyfin's collage. The endpoints themselves (authorization, routes, queries,
/// uploads) are checked against a real server by the end-to-end harness.
/// </summary>
public sealed class CoversApiTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("6a1f3c2e-8b4d-4e5f-9a0b-1c2d3e4f5a6b");
    private static readonly Guid TitleA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TitleB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private readonly string _root = Directory.CreateTempSubdirectory("covers-api-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- Style ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(StyleUpdateDto.TextSize), 39)]
    [InlineData(nameof(StyleUpdateDto.TextSize), 201)]
    [InlineData(nameof(StyleUpdateDto.Darken), -1)]
    [InlineData(nameof(StyleUpdateDto.Darken), 71)]
    [InlineData(nameof(StyleUpdateDto.LengthSeconds), 1)]
    [InlineData(nameof(StyleUpdateDto.LengthSeconds), 13)]
    [InlineData(nameof(StyleUpdateDto.SlotCount), 0)]
    [InlineData(nameof(StyleUpdateDto.SlotCount), 6)]
    public void Out_of_range_style_values_are_refused_by_name(string field, int value)
    {
        var style = new StyleUpdateDto();
        typeof(StyleUpdateDto).GetProperty(field)!.SetValue(style, value);

        var errors = CoverRequestRules.CheckStyle(style);

        var error = Assert.Single(errors);
        Assert.Contains(field, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(StyleUpdateDto.TextSize), 40)]
    [InlineData(nameof(StyleUpdateDto.TextSize), 200)]
    [InlineData(nameof(StyleUpdateDto.Darken), 0)]
    [InlineData(nameof(StyleUpdateDto.Darken), 70)]
    [InlineData(nameof(StyleUpdateDto.LengthSeconds), 2)]
    [InlineData(nameof(StyleUpdateDto.LengthSeconds), 12)]
    [InlineData(nameof(StyleUpdateDto.SlotCount), 1)]
    [InlineData(nameof(StyleUpdateDto.SlotCount), 5)]
    public void Style_values_at_the_edges_are_accepted(string field, int value)
    {
        var style = new StyleUpdateDto();
        typeof(StyleUpdateDto).GetProperty(field)!.SetValue(style, value);

        Assert.Empty(CoverRequestRules.CheckStyle(style));
    }

    [Fact]
    public void Font_must_be_one_of_the_bundled_families()
    {
        Assert.Empty(CoverRequestRules.CheckStyle(new StyleUpdateDto { Font = "Bebas Neue" }));
        Assert.Empty(CoverRequestRules.CheckStyle(new StyleUpdateDto { Font = "bebas neue" }));
        Assert.Single(CoverRequestRules.CheckStyle(new StyleUpdateDto { Font = "Comic Sans" }));
        Assert.Single(CoverRequestRules.CheckStyle(new StyleUpdateDto { Font = "" }));
    }

    [Fact]
    public void Title_is_limited_and_has_no_control_characters()
    {
        Assert.Empty(CoverRequestRules.CheckStyle(new StyleUpdateDto { Title = string.Empty }));
        Assert.Empty(CoverRequestRules.CheckStyle(new StyleUpdateDto { Title = new string('x', 100) }));
        Assert.Single(CoverRequestRules.CheckStyle(new StyleUpdateDto { Title = new string('x', 101) }));
        Assert.Single(CoverRequestRules.CheckStyle(new StyleUpdateDto { Title = "two\nlines" }));
    }

    [Fact]
    public void Every_wrong_field_is_reported_not_only_the_first()
    {
        var errors = CoverRequestRules.CheckStyle(new StyleUpdateDto { Darken = 99, TextSize = 1, Font = "x" });

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Applying_a_style_changes_only_the_fields_given_and_uses_the_catalog_spelling()
    {
        var target = new CoverStyle { Darken = 15, Title = "Keep", Frame = true };

        CoverRequestRules.ApplyStyle(new StyleUpdateDto { Darken = 40, Font = "oswald", Frame = false }, target);

        Assert.Equal(40, target.Darken);
        Assert.Equal("Oswald", target.Font);
        Assert.False(target.Frame);
        Assert.Equal("Keep", target.Title);
        Assert.Equal(120, target.TextSize);
    }

    // ---- Renewal ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void Renewal_days_are_zero_or_one_to_365(int days, bool accepted) =>
        Assert.Equal(accepted, CoverRequestRules.CheckRenewal(days) is null);

    [Fact]
    public void Renewal_left_out_is_accepted() => Assert.Null(CoverRequestRules.CheckRenewal(null));

    // ---- Slots ------------------------------------------------------------------------------------------------------

    private static SlotUpdateDto[] Free() => Enumerable.Range(0, 5).Select(_ => new SlotUpdateDto()).ToArray();

    [Fact]
    public void Five_free_slots_are_accepted() => Assert.Empty(CoverRequestRules.CheckSlots(Free(), _ => null));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Any_other_number_of_slots_is_refused(int count)
    {
        var slots = Enumerable.Range(0, count).Select(_ => new SlotUpdateDto()).ToArray();

        Assert.Single(CoverRequestRules.CheckSlots(slots, _ => null));
    }

    [Fact]
    public void A_title_locked_twice_is_refused()
    {
        var slots = Free();
        slots[0].ItemId = TitleA;
        slots[3].ItemId = TitleA;

        var error = Assert.Single(CoverRequestRules.CheckSlots(slots, _ => null));
        Assert.Contains("another slot", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_different_locked_titles_are_accepted()
    {
        var slots = Free();
        slots[0].ItemId = TitleA;
        slots[1].ItemId = TitleB;

        Assert.Empty(CoverRequestRules.CheckSlots(slots, _ => null));
    }

    [Fact]
    public void A_locked_title_the_caller_refuses_gives_its_reason()
    {
        var slots = Free();
        slots[2].ItemId = TitleA;

        var errors = CoverRequestRules.CheckSlots(slots, slot => slot.ItemId == TitleA ? "may not be seen" : null);

        var error = Assert.Single(errors);
        Assert.Contains("may not be seen", error, StringComparison.Ordinal);
        Assert.Contains("3", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_caller_is_asked_only_about_locked_slots()
    {
        var slots = Free();
        slots[1].ItemId = TitleA;
        var asked = 0;

        CoverRequestRules.CheckSlots(slots, _ =>
        {
            asked++;
            return null;
        });

        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_free_slot_cannot_carry_a_moment_or_a_backdrop()
    {
        var slots = Free();
        slots[0].MomentSeconds = 5;
        slots[1].BackdropIndex = 0;

        Assert.Equal(2, CoverRequestRules.CheckSlots(slots, _ => null).Count);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_locked_moment_is_a_finite_time_from_the_start(double moment)
    {
        var slots = Free();
        slots[0].ItemId = TitleA;
        slots[0].MomentSeconds = moment;

        Assert.Single(CoverRequestRules.CheckSlots(slots, _ => null));
    }

    [Fact]
    public void A_locked_slot_has_a_moment_or_a_backdrop_not_both_and_no_negative_index()
    {
        var both = Free();
        both[0].ItemId = TitleA;
        both[0].MomentSeconds = 10;
        both[0].BackdropIndex = 1;
        Assert.Single(CoverRequestRules.CheckSlots(both, _ => null));

        var negative = Free();
        negative[0].ItemId = TitleA;
        negative[0].BackdropIndex = -1;
        Assert.Single(CoverRequestRules.CheckSlots(negative, _ => null));

        var empty = Free();
        empty[0].ItemId = Guid.Empty;
        Assert.Single(CoverRequestRules.CheckSlots(empty, _ => null));
    }

    [Fact]
    public void Slots_are_written_to_the_state_in_order()
    {
        var state = new LibraryCoverState { LibraryId = Library };
        state.Slots[4].ItemId = TitleB;
        var slots = Free();
        slots[0].ItemId = TitleA;
        slots[0].MomentSeconds = 42.5;

        CoverRequestRules.ApplySlots(slots, state.Slots);

        Assert.Equal(TitleA, state.Slots[0].ItemId);
        Assert.Equal(42.5, state.Slots[0].MomentSeconds);
        Assert.Null(state.Slots[4].ItemId);
        Assert.Equal(5, state.Slots.Length);
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(640, 360, true)]
    [InlineData(960, 540, true)]
    [InlineData(320, 180, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(300, 168, false)]
    [InlineData(2048, 1152, false)]
    [InlineData(640, 480, false)]
    [InlineData(641, 361, false)]
    public void Output_size_keeps_the_16_by_9_shape_within_range(int width, int height, bool accepted)
    {
        var errors = CoverRequestRules.CheckSettings(
            new SettingsUpdateDto { OutputWidth = width, OutputHeight = height },
            new PluginConfiguration());

        Assert.Equal(accepted, errors.Count == 0);
    }

    [Fact]
    public void A_new_width_alone_is_checked_against_the_height_in_the_configuration()
    {
        var current = new PluginConfiguration { OutputWidth = 640, OutputHeight = 360 };

        Assert.Single(CoverRequestRules.CheckSettings(new SettingsUpdateDto { OutputWidth = 960 }, current));
        Assert.Empty(CoverRequestRules.CheckSettings(new SettingsUpdateDto(), current));
    }

    [Fact]
    public void Enums_outside_their_members_are_refused()
    {
        var errors = CoverRequestRules.CheckSettings(
            new SettingsUpdateDto { PrimaryFormat = (PrimaryFormat)7, DefaultMode = (CoverMode)9 },
            new PluginConfiguration());

        Assert.Equal(2, errors.Count);
        Assert.True(CoverRequestRules.IsDefined(CoverMode.Art));
        Assert.False(CoverRequestRules.IsDefined((CoverMode)3));
    }

    // ---- Uploads ----------------------------------------------------------------------------------------------------

    private static byte[] Header(params object[] parts)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case string text:
                    bytes.AddRange(Encoding.ASCII.GetBytes(text));
                    break;
                case byte[] raw:
                    bytes.AddRange(raw);
                    break;
                case int zeros:
                    bytes.AddRange(new byte[zeros]);
                    break;
            }
        }

        return bytes.ToArray();
    }

    [Fact]
    public void Upload_limit_is_20_megabytes() => Assert.Equal(20L * 1024 * 1024, CoverRequestRules.MaxUploadBytes);

    [Fact]
    public void The_four_allowed_types_are_recognized_by_their_signature()
    {
        var png = Header(new byte[] { 0x89 }, "PNG", new byte[] { 0x0d, 0x0a, 0x1a, 0x0a }, 8);
        var jpeg = Header(new byte[] { 0xff, 0xd8, 0xff, 0xe0 }, 12);
        var size = new byte[] { 1, 2, 3, 4 };
        Assert.Equal("image/png", CoverRequestRules.DetectImageType(png));
        Assert.Equal("image/jpeg", CoverRequestRules.DetectImageType(jpeg));
        Assert.Equal("image/gif", CoverRequestRules.DetectImageType(Header("GIF87a", 10)));
        Assert.Equal("image/gif", CoverRequestRules.DetectImageType(Header("GIF89a", 10)));
        Assert.Equal("image/webp", CoverRequestRules.DetectImageType(Header("RIFF", size, "WEBPVP8 ")));
        Assert.Equal("image/webp", CoverRequestRules.DetectImageType(Header("RIFF", size, "WEBPVP8L")));
        Assert.Equal("image/webp", CoverRequestRules.DetectImageType(Header("RIFF", size, "WEBPVP8X")));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("MZ\u0090\0\u0003\0\0\0\u0004\0\0\0\u00ff\u00ff\0\0")]
    [InlineData("GIF90a0123456789")]
    [InlineData("RIFF\u0001\u0002\u0003\u0004WAVEfmt ")]
    [InlineData("RIFF\u0001\u0002\u0003\u0004WEBPJUNK")]
    [InlineData("")]
    [InlineData("PNG")]
    public void Anything_else_is_not_an_image_whatever_its_name_or_type_claims(string content)
    {
        Assert.Null(CoverRequestRules.DetectImageType(Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void A_file_cut_inside_its_signature_is_refused()
    {
        Assert.Null(CoverRequestRules.DetectImageType(new byte[] { 0xff, 0xd8 }));
        Assert.Null(CoverRequestRules.DetectImageType(Header("RIFF", new byte[] { 1, 2, 3, 4 }, "WEBP")));
        Assert.Null(CoverRequestRules.DetectImageType(Header(new byte[] { 0x89 }, "PNG")));
    }

    // ---- Returning to Jellyfin's collage ---------------------------------------------------------------------------

    [Fact]
    public async Task Returning_to_automatic_records_the_new_collage_and_marks_the_apply()
    {
        var store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        var images = new CollageImages();
        var applier = NewApplier(images, store);
        var original = new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Original, Automatic = true };

        var state = await store.UpdateAsync(Library, async (current, token) =>
        {
            current.Original = original;
            current.CurrentCoverId = Guid.NewGuid();
            await applier.ReturnToAutomaticAsync(current, token);
        });

        Assert.Equal(original.Id, state.CurrentCoverId);
        Assert.Equal("/jellyfin/Movies/collage.jpg", state.AppliedPrimaryPath);
        Assert.Equal(CollageImages.CollageTime, state.AppliedPrimaryModified);
        Assert.True(images.ApplyingDuringReset);
        Assert.False(applier.IsApplying(Library));
        Assert.Equal(1, images.Resets);
    }

    [Fact]
    public async Task Returning_to_automatic_without_an_original_changes_nothing()
    {
        var store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        var images = new CollageImages();
        var applier = NewApplier(images, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(
            Library,
            (current, token) => applier.ReturnToAutomaticAsync(current, token)));

        Assert.Equal(0, images.Resets);
        Assert.False(applier.IsApplying(Library));
    }

    [Fact]
    public async Task Returning_to_automatic_for_a_library_that_is_gone_changes_nothing()
    {
        var store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        var images = new CollageImages { Missing = true };
        var applier = NewApplier(images, store);

        await Assert.ThrowsAsync<LibraryGoneException>(() => store.UpdateAsync(Library, (current, token) =>
        {
            current.Original = new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Original, Automatic = true };
            return applier.ReturnToAutomaticAsync(current, token);
        }));

        Assert.Equal(0, images.Resets);
    }

    [Fact]
    public async Task A_refresh_that_fails_after_the_delete_counts_as_applied_and_is_logged_as_an_error()
    {
        var store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        var images = new CollageImages { ResetFailure = new IOException("refresh failed") };
        var log = new ErrorCounter();
        var applier = new CoverApplier(images, store, log, () => new PluginConfiguration());
        images.Applier = applier;
        using var run = applier.StartApply(Library);

        var failure = await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(Library, (current, token) =>
        {
            current.Original = new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Original, Automatic = true };
            return run.ReturnToAutomaticAsync(current, token);
        }));
        run.Unsaved(failure);

        Assert.True(run.Applied);
        Assert.Equal(1, log.Errors);
    }

    [Fact]
    public async Task A_return_that_stops_before_touching_the_image_is_not_an_error_to_log()
    {
        var store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        var images = new CollageImages { Missing = true };
        var log = new ErrorCounter();
        var applier = new CoverApplier(images, store, log, () => new PluginConfiguration());
        images.Applier = applier;
        using var run = applier.StartApply(Library);

        var failure = await Assert.ThrowsAsync<LibraryGoneException>(
            () => store.UpdateAsync(Library, (current, token) =>
            {
                current.Original = new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Original };
                return run.ReturnToAutomaticAsync(current, token);
            }));
        run.Unsaved(failure);

        Assert.False(run.Applied);
        Assert.Equal(0, log.Errors);
    }

    private sealed class ErrorCounter : Microsoft.Extensions.Logging.ILogger
    {
        public int Errors { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Errors++;
            }
        }
    }

    // ---- Builds an admin asks for -----------------------------------------------------------------------------------

    private sealed class FakeBuilds
    {
        public List<(BuildReason Reason, TaskCompletionSource<BuildResult> Done)> Calls { get; } = new();

        public BuildCoalescer Coalescer { get; }

        public FakeBuilds()
        {
            Coalescer = new BuildCoalescer((_, reason) =>
            {
                var done = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (Calls)
                {
                    Calls.Add((reason, done));
                }

                return done.Task;
            });
        }

        public int Count
        {
            get
            {
                lock (Calls)
                {
                    return Calls.Count;
                }
            }
        }

        public void Complete(int index) => Calls[index].Done.SetResult(new BuildResult(true, null, 5));

        // The follow-up starts on a pool thread, after the build it waited for finished.
        public async Task WaitForCallsAsync(int count)
        {
            for (var i = 0; i < 500 && Count < count; i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(count, Count);
        }
    }

    [Fact]
    public async Task Generate_goes_ahead_of_a_slots_build_that_is_still_waiting_for_playback()
    {
        var builds = new FakeBuilds();
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Slots));

        // The slots job may wait for hours; the admin's press must not be swallowed by it.
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.Equal([BuildReason.Slots, BuildReason.Manual], builds.Calls.Select(call => call.Reason));

        // A second press while that build runs asks for one more build, once.
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.False(builds.Coalescer.Request(Library, BuildReason.Manual));
        builds.Complete(1);
        builds.Complete(0);
        await builds.WaitForCallsAsync(3);
        Assert.Equal(BuildReason.Manual, builds.Calls[2].Reason);
    }

    [Fact]
    public async Task A_slot_change_during_a_render_leads_to_a_build_after_it()
    {
        var builds = new FakeBuilds();
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));

        Assert.True(builds.Coalescer.Request(Library, BuildReason.Slots));
        Assert.False(builds.Coalescer.Request(Library, BuildReason.Slots));
        Assert.Equal(1, builds.Count);

        builds.Complete(0);
        await builds.WaitForCallsAsync(2);
        Assert.Equal(BuildReason.Slots, builds.Calls[1].Reason);

        // Nothing is left over: once that one is done, a request starts a fresh build.
        builds.Complete(1);
        await Task.Delay(50);
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.Equal(3, builds.Count);
    }

    [Fact]
    public async Task The_build_after_the_current_one_has_the_strongest_reason_asked_for_meanwhile()
    {
        var builds = new FakeBuilds();
        builds.Coalescer.Request(Library, BuildReason.Manual);
        builds.Coalescer.Request(Library, BuildReason.Slots);
        builds.Coalescer.Request(Library, BuildReason.Manual);
        builds.Coalescer.Request(Library, BuildReason.Slots);

        builds.Complete(0);
        await builds.WaitForCallsAsync(2);

        Assert.Equal(BuildReason.Manual, builds.Calls[1].Reason);
        builds.Complete(1);
        await Task.Delay(50);
        Assert.Equal(2, builds.Count);
    }

    [Fact]
    public async Task A_cancelled_build_is_not_followed_by_the_build_that_was_due_and_leaves_nothing_behind()
    {
        var builds = new FakeBuilds();
        builds.Coalescer.Request(Library, BuildReason.Manual);
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Slots));

        // The server is stopping: the running build is cancelled, so the follow-up must not start.
        builds.Calls[0].Done.SetCanceled();
        await Task.Delay(100);
        Assert.Equal(1, builds.Count);

        // And the library is not stuck as "in flight": the next request starts a build.
        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.Equal(2, builds.Count);
    }

    [Fact]
    public async Task A_failed_build_still_lets_the_next_request_through()
    {
        var builds = new FakeBuilds();
        builds.Coalescer.Request(Library, BuildReason.Manual);
        builds.Calls[0].Done.SetException(new InvalidOperationException("render failed"));
        await Task.Delay(50);

        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.Equal(2, builds.Count);
    }

    [Fact]
    public void Libraries_do_not_wait_for_each_other()
    {
        var builds = new FakeBuilds();

        Assert.True(builds.Coalescer.Request(Library, BuildReason.Manual));
        Assert.True(builds.Coalescer.Request(Guid.NewGuid(), BuildReason.Manual));
        Assert.Equal(2, builds.Count);
    }

    // ---- What an uploaded file contains -----------------------------------------------------------------------------

    private static byte[] Encoded(SKEncodedImageFormat format, int width = 8, int height = 8)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    // A 1 by 1 GIF.
    private static readonly byte[] TinyGif =
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff,
        0xff, 0x21, 0xf9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3b,
    ];

    [Fact]
    public void Real_images_of_the_four_types_pass_and_the_stream_is_rewound()
    {
        var files = new[]
        {
            ("image/png", Encoded(SKEncodedImageFormat.Png)),
            ("image/jpeg", Encoded(SKEncodedImageFormat.Jpeg)),
            ("image/webp", Encoded(SKEncodedImageFormat.Webp)),
            ("image/gif", TinyGif),
        };

        foreach (var (mime, bytes) in files)
        {
            Assert.Equal(mime, CoverRequestRules.DetectImageType(bytes.AsSpan(0, 16)));
            using var stream = new MemoryStream(bytes);
            stream.Position = 5;
            Assert.Null(CoverRequestRules.CheckImage(stream, mime));
            Assert.Equal(0, stream.Position);
        }
    }

    [Fact]
    public void A_png_cut_short_or_followed_by_garbage_is_refused()
    {
        var png = Encoded(SKEncodedImageFormat.Png);
        var cut = png.AsSpan(0, 20).ToArray();
        var garbage = png.AsSpan(0, 8).ToArray().Concat(Enumerable.Repeat((byte)0x41, 200)).ToArray();

        foreach (var bytes in new[] { cut, garbage })
        {
            Assert.Equal("image/png", CoverRequestRules.DetectImageType(bytes.AsSpan(0, 16)));
            using var stream = new MemoryStream(bytes);
            Assert.NotNull(CoverRequestRules.CheckImage(stream, "image/png"));
        }
    }

    [Fact]
    public void An_image_of_another_kind_than_its_signature_says_is_refused()
    {
        using var stream = new MemoryStream(Encoded(SKEncodedImageFormat.Png));

        Assert.NotNull(CoverRequestRules.CheckImage(stream, "image/jpeg"));
    }

    [Fact]
    public void A_side_over_4096_pixels_is_refused_and_4096_is_not()
    {
        using var wide = new MemoryStream(Encoded(SKEncodedImageFormat.Png, 4097, 1));
        using var tall = new MemoryStream(Encoded(SKEncodedImageFormat.Png, 1, 4097));
        using var edge = new MemoryStream(Encoded(SKEncodedImageFormat.Png, 4096, 1));

        Assert.Contains("4097", CoverRequestRules.CheckImage(wide, "image/png"), StringComparison.Ordinal);
        Assert.NotNull(CoverRequestRules.CheckImage(tall, "image/png"));
        Assert.Null(CoverRequestRules.CheckImage(edge, "image/png"));
    }

    private static CoverApplier NewApplier(CollageImages images, CoverStore store)
    {
        var applier = new CoverApplier(
            images, store, NullLogger<CoverApplier>.Instance, () => new PluginConfiguration());
        images.Applier = applier;
        return applier;
    }

    private sealed class CollageImages : ILibraryImages, ILibraryImage
    {
        public static readonly DateTime CollageTime = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        public CoverApplier? Applier { get; set; }

        public bool Missing { get; set; }

        public int Resets { get; private set; }

        public bool ApplyingDuringReset { get; private set; }

        public Exception? ResetFailure { get; set; }

        public ILibraryImage? Find(Guid libraryId) => Missing ? null : this;

        public PrimaryImage? Primary() =>
            Resets == 0 ? null : new PrimaryImage("/jellyfin/Movies/collage.jpg", CollageTime, true, true);

        public Task SaveAsync(Stream image, string mimeType, CancellationToken ct) => throw new NotSupportedException();

        public Task PersistAsync() => Task.CompletedTask;

        public Task ResetToAutomaticAsync()
        {
            ApplyingDuringReset = Applier!.IsApplying(Library);
            Resets++;
            return ResetFailure is null ? Task.CompletedTask : Task.FromException(ResetFailure);
        }
    }

    private sealed class Paths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;

        public string WebPath => Path.Combine(root, "web");

        public string ProgramSystemPath => Path.Combine(root, "system");

        public string DataPath => Path.Combine(root, "data");

        public string ImageCachePath => Path.Combine(root, "cache", "images");

        public string PluginsPath => Path.Combine(root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(root, "config");

        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

        public string CachePath => Path.Combine(root, "cache");

        public string TempDirectory => Path.Combine(root, "temp");

        public string VirtualDataPath => Path.Combine(root, "data");

        public string TrickplayPath => Path.Combine(root, "data", "trickplay");

        public string BackupPath => Path.Combine(root, "backups");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
