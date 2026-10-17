using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Api;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Scheduling;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The rules that decide whether a library's cover is built, and the build itself: titles, slots, render and apply,
/// with Jellyfin, the renderer and SaveImage replaced by fakes. What only a real server can show (queries, the
/// episodes of a series, SaveImage) is checked by the end-to-end harness.
/// </summary>
[Collection(TestCollections.BaseItemStatics)]
public sealed class CoverRulesTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("6a1f3c2e-8b4d-4e5f-9a0b-1c2d3e4f5a6b");
    private static readonly Guid TitleA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TitleB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid TitleC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid TitleD = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid TitleE = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static readonly Guid Hidden = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private readonly string _root = Directory.CreateTempSubdirectory("cover-rules-tests-").FullName;
    private readonly CoverStore _store;
    private readonly FakeSource _source = new();
    private readonly FakeImages _images;
    private readonly FakeRenderer _renderer = new();
    private PluginConfiguration _config = new();
    private readonly CoverApplier _applier;
    private readonly CoverBuilder _builder;

    public CoverRulesTests()
    {
        _store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        _images = new FakeImages(Path.Combine(_root, "jellyfin", "Movies"));
        _applier = new CoverApplier(_images, _store, NullLogger<CoverApplier>.Instance, () => _config);
        _images.Applier = _applier;
        _builder = new CoverBuilder(
            _source,
            _store,
            _applier,
            new OverlayRenderer(),
            _renderer.RenderAsync,
            () => _config,
            () => new Random(7),
            NullLogger<CoverBuilder>.Instance);

        // A small cover keeps the overlay and the name-only picture quick to draw.
        _config.OutputWidth = 64;
        _config.OutputHeight = 36;
    }

    // Where the store keeps this library's state and covers.
    private string LibraryFolder => Path.Combine(_root, "data", "library-cover-maker", Library.ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- CoverRules ------------------------------------------------------------------------------------------------

    [Fact]
    public void Pinned_generated_cover_with_disallowed_title_must_be_replaced()
    {
        var current = Record(CoverOrigin.Generated, TitleA, Hidden);

        Assert.True(CoverRules.MustReplace(current, id => id != Hidden));
        Assert.False(CoverRules.MustReplace(current, _ => true));
        Assert.False(CoverRules.MustReplace(null, _ => false));

        var pinned = new LibraryCoverState { LibraryId = Library, Pinned = true };
        Assert.True(CoverRules.ShouldBuild(pinned, "movies", BuildReason.AudienceChanged, mustReplace: true));
    }

    [Fact]
    public void Uploaded_cover_is_never_replaced()
    {
        Assert.False(CoverRules.MustReplace(Record(CoverOrigin.Uploaded, Hidden), _ => false));
    }

    [Fact]
    public void Original_cover_is_never_replaced()
    {
        Assert.False(CoverRules.MustReplace(Record(CoverOrigin.Original, Hidden), _ => false));
    }

    [Fact]
    public void Disallowed_locked_titles_are_listed()
    {
        var slots = new[]
        {
            new CoverSlot { ItemId = TitleA },
            new CoverSlot { ItemId = Hidden, MomentSeconds = 12 },
            new CoverSlot(),
            new CoverSlot { ItemId = TitleB },
            new CoverSlot { ItemId = Hidden },
        };

        // In slot order, each title once; free slots and allowed titles are not listed.
        Assert.Equal(new[] { Hidden }, CoverRules.DisallowedLocks(slots, id => id != Hidden));
        Assert.Equal(new[] { TitleA, Hidden, TitleB }, CoverRules.DisallowedLocks(slots, _ => false));
        Assert.Empty(CoverRules.DisallowedLocks(slots, _ => true));
    }

    [Fact]
    public void Pinned_library_is_not_built_on_schedule()
    {
        var pinned = new LibraryCoverState { LibraryId = Library, Pinned = true };

        Assert.False(CoverRules.ShouldBuild(pinned, "movies", BuildReason.Scheduled, mustReplace: false));
        Assert.False(CoverRules.ShouldBuild(pinned, "movies", BuildReason.Scheduled, mustReplace: true));
        Assert.False(CoverRules.ShouldBuild(pinned, "movies", BuildReason.Manual, mustReplace: false));
        Assert.False(CoverRules.ShouldBuild(pinned, "movies", BuildReason.Slots, mustReplace: false));
        Assert.False(CoverRules.ShouldBuild(pinned, "movies", BuildReason.AudienceChanged, mustReplace: false));
        Assert.True(CoverRules.ShouldBuild(pinned, "movies", BuildReason.AudienceChanged, mustReplace: true));
    }

    [Fact]
    public void Manual_mode_builds_only_on_request()
    {
        var manual = new LibraryCoverState { LibraryId = Library, Mode = CoverMode.Manual };

        Assert.False(CoverRules.ShouldBuild(manual, "movies", BuildReason.Scheduled, mustReplace: false));
        Assert.False(CoverRules.ShouldBuild(manual, "movies", BuildReason.AudienceChanged, mustReplace: false));
        Assert.True(CoverRules.ShouldBuild(manual, "movies", BuildReason.Manual, mustReplace: false));
        Assert.True(CoverRules.ShouldBuild(manual, "movies", BuildReason.Slots, mustReplace: false));

        // A cover that shows a title some viewer may not see is replaced in every mode (decision 9).
        Assert.True(CoverRules.ShouldBuild(manual, "movies", BuildReason.AudienceChanged, mustReplace: true));
    }

    [Theory]
    [InlineData("music")]
    [InlineData("books")]
    [InlineData("photos")]
    [InlineData("boxsets")]
    [InlineData("playlists")]
    public void Non_video_library_is_never_built_automatically(string collectionType)
    {
        var state = new LibraryCoverState { LibraryId = Library };

        foreach (var reason in Enum.GetValues<BuildReason>())
        {
            Assert.False(CoverRules.ShouldBuild(state, collectionType, reason, mustReplace: true));
        }
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tvshows")]
    [InlineData("musicvideos")]
    [InlineData("homevideos")]
    [InlineData(null)]
    public void Video_library_in_automatic_mode_is_built_for_every_reason(string? collectionType)
    {
        var state = new LibraryCoverState { LibraryId = Library };

        foreach (var reason in Enum.GetValues<BuildReason>())
        {
            Assert.True(CoverRules.ShouldBuild(state, collectionType, reason, mustReplace: false));
        }
    }

    // ---- ArtworkSource ---------------------------------------------------------------------------------------------

    [Fact]
    public void Artwork_uses_the_chosen_backdrop_and_skips_missing_files()
    {
        var first = WriteFile("art/first.jpg", [1]);
        var second = WriteFile("art/second.jpg", [2]);
        var movie = new Movie
        {
            ImageInfos =
            [
                new ItemImageInfo { Type = ImageType.Primary, Path = WriteFile("art/poster.jpg", [3]) },
                new ItemImageInfo { Type = ImageType.Backdrop, Path = first },
                new ItemImageInfo { Type = ImageType.Backdrop, Path = Path.Combine(_root, "art", "gone.jpg") },
                new ItemImageInfo { Type = ImageType.Backdrop, Path = second },
            ],
        };
        var artwork = new ArtworkSource();

        var chosen = artwork.ForTitle(movie, backdropIndex: 2, seconds: 1.5, new Random(1));
        Assert.NotNull(chosen);
        Assert.Equal(new SlotInput(SlotKind.Image, second, 0, 1.5, false), chosen);

        // A chosen backdrop whose file is gone gives way to one of the others, never to the missing one.
        for (var seed = 0; seed < 20; seed++)
        {
            var other = artwork.ForTitle(movie, backdropIndex: 1, seconds: 1.5, new Random(seed));
            Assert.NotNull(other);
            Assert.Contains(other.Path, new[] { first, second });
        }
    }

    [Fact]
    public void Artwork_is_null_without_a_backdrop_on_disk()
    {
        var artwork = new ArtworkSource();
        var noBackdrop = new Movie
        {
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = WriteFile("art/poster.jpg", [3]) }],
        };
        var remoteOnly = new Movie
        {
            ImageInfos = [new ItemImageInfo { Type = ImageType.Backdrop, Path = "https://example.invalid/b.jpg" }],
        };
        var missing = new Movie
        {
            ImageInfos = [new ItemImageInfo { Type = ImageType.Backdrop, Path = Path.Combine(_root, "nothing.jpg") }],
        };

        Assert.Null(artwork.ForTitle(noBackdrop, null, 1, new Random(1)));
        Assert.Null(artwork.ForTitle(remoteOnly, null, 1, new Random(1)));
        Assert.Null(artwork.ForTitle(missing, 0, 1, new Random(1)));
    }

    // ---- Pieces of the Jellyfin side that need no server -----------------------------------------------------------

    [Fact]
    public void Series_scenes_prefer_the_first_allowed_episode_of_season_one()
    {
        var special = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 0, IndexNumber = 1 };
        var s2e1 = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 2, IndexNumber = 1 };
        var s1e2 = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 2 };
        var s1e1 = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1 };
        var episodes = new Video[] { special, s2e1, s1e2, s1e1 };

        Assert.Same(s1e1, JellyfinCoverSource.FirstEpisode(episodes, _ => true));
        Assert.Same(s1e2, JellyfinCoverSource.FirstEpisode(episodes, ep => ep != s1e1));
        Assert.Same(s2e1, JellyfinCoverSource.FirstEpisode(episodes, ep => ep.ParentIndexNumber != 1));
        Assert.Same(special, JellyfinCoverSource.FirstEpisode(episodes, ep => ep == special));
        Assert.Null(JellyfinCoverSource.FirstEpisode(episodes, _ => false));
    }

    [Fact]
    public void Hdr_comes_from_the_default_video_stream_range()
    {
        var pq = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
        };
        var sdr = new MediaStream { Type = MediaStreamType.Video, ColorTransfer = "bt709", ColorPrimaries = "bt709" };

        Assert.True(JellyfinCoverSource.IsHdr(pq));
        Assert.False(JellyfinCoverSource.IsHdr(sdr));
        Assert.False(JellyfinCoverSource.IsHdr(null));
    }

    [Theory]
    [InlineData("movies", "Movie")]
    [InlineData(null, "Movie")]
    [InlineData("tvshows", "Series")]
    [InlineData("musicvideos", "MusicVideo")]
    [InlineData("homevideos", "Video,Movie")]
    public void Each_library_type_queries_its_own_titles(string? collectionType, string kinds)
    {
        Assert.Equal(kinds, string.Join(",", JellyfinCoverSource.TitleKinds(collectionType)));
    }

    [Fact]
    public void Only_images_under_the_library_metadata_folder_are_automatic()
    {
        var metadata = Path.Combine(_root, "metadata", "library", "ab", "abcd");

        Assert.True(CoverApplier.IsUnder(metadata, Path.Combine(metadata, "poster.jpg")));
        Assert.True(CoverApplier.IsUnder(metadata + "/", Path.Combine(metadata, "sub", "poster.png")));
        Assert.False(CoverApplier.IsUnder(metadata, metadata + "ef/poster.jpg"));
        Assert.False(CoverApplier.IsUnder(metadata, Path.Combine(_root, "root", "default", "Movies", "poster.jpg")));
        Assert.False(CoverApplier.IsUnder(metadata, "https://example.invalid/poster.jpg"));
    }

    // ---- The build -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Build_applies_the_cover_and_records_it()
    {
        Titles(TitleA, TitleB, TitleC, TitleD, TitleE);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 5), result);
        var plan = Assert.Single(_renderer.Plans);
        Assert.Equal(_store.TempFolder, plan.TempFolder);
        Assert.Equal(12, plan.Fps);
        Assert.True(plan.Fade);
        Assert.Equal((64, 36), (plan.Width, plan.Height));
        var seconds = CoverRenderer.SlotSeconds(6, 5, fade: true, fps: 12);
        Assert.All(plan.Slots, slot => Assert.Equal(seconds, slot.DurationSeconds));

        var state = _store.Load(Library);
        var record = Assert.Single(state.History);
        Assert.Equal(CoverOrigin.Generated, record.Origin);
        Assert.Equal(CoverMode.Art, record.Mode);
        Assert.Equal(5, record.TitleIds.Distinct().Count());
        Assert.Equal(record.Id, state.CurrentCoverId);
        Assert.Equal(_store.AnimatedPath(Library, record.Id), plan.AnimatedOut);
        Assert.Equal(_store.StillPath(Library, record.Id), plan.StillOut);
        Assert.NotNull(state.LastRenderedAt);
        Assert.Null(state.LastError);
        Assert.Equal(_images.Current!.Path, state.AppliedPrimaryPath);
        Assert.Equal(_images.Current.Modified, state.AppliedPrimaryModified);

        // The animated version is the default Primary, and the overlay was a temporary file.
        var saved = Assert.Single(_images.Saved);
        Assert.Equal("image/webp", saved.MimeType);
        Assert.Equal(File.ReadAllBytes(plan.AnimatedOut), saved.Bytes);
        Assert.False(File.Exists(plan.OverlayPngPath));
        Assert.True(_renderer.OverlayExisted);
    }

    [Fact]
    public async Task Still_format_applies_the_jpeg()
    {
        Titles(TitleA);
        _config.PrimaryFormat = PrimaryFormat.Still;

        await _builder.BuildAsync(Library, BuildReason.Manual, CancellationToken.None);

        var saved = Assert.Single(_images.Saved);
        Assert.Equal("image/jpeg", saved.MimeType);
        Assert.Equal(File.ReadAllBytes(_renderer.Plans[0].StillOut), saved.Bytes);
    }

    [Fact]
    public async Task First_apply_keeps_the_library_image_as_original()
    {
        Titles(TitleA);
        var collage = _images.SetCurrent("collage.jpg", [9, 9, 9], automatic: true);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        // SaveImage deleted Jellyfin's file, so the store's copy is the only one left.
        Assert.False(File.Exists(collage));
        var state = _store.Load(Library);
        var original = Assert.IsType<CoverRecord>(state.Original);
        Assert.Equal(CoverOrigin.Original, original.Origin);
        Assert.Empty(original.TitleIds);
        var file = CoverApplier.CoverFile(_store, Library, original, PrimaryFormat.Animated);
        Assert.NotNull(file);
        Assert.Equal("image/jpeg", file.Value.MimeType);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(file.Value.Path));
        Assert.DoesNotContain(state.History, cover => cover.Id == original.Id);

        // The next cover replaces the plugin's own image: the original stays the one that was there first.
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var later = _store.Load(Library);
        Assert.Equal(original.Id, later.Original!.Id);
        Assert.Equal(2, later.History.Count);
    }

    [Fact]
    public async Task Original_is_marked_automatic_when_it_was_the_collage()
    {
        Titles(TitleA);
        _images.SetCurrent("collage.jpg", [9], automatic: true);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.True(_store.Load(Library).Original!.Automatic);
    }

    [Fact]
    public async Task Original_is_not_marked_automatic_when_an_admin_set_it()
    {
        Titles(TitleA);
        _images.SetCurrent("admin.jpg", [8], automatic: false);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(_store.Load(Library).Original!.Automatic);
    }

    [Fact]
    public void State_written_before_the_automatic_flag_still_loads()
    {
        _store.Update(Library, state => state.Original = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = CoverOrigin.Original,
            Automatic = true,
        });
        var path = Assert.Single(Directory.GetFiles(_root, "state.json", SearchOption.AllDirectories));
        var json = File.ReadAllText(path);
        Assert.Contains("\"Automatic\"", json);
        var node = JsonNode.Parse(json)!;
        Assert.True(node["Original"]!.AsObject().Remove("Automatic"));
        File.WriteAllText(path, node.ToJsonString());

        var original = Assert.IsType<CoverRecord>(_store.Load(Library).Original);
        Assert.False(original.Automatic);
    }

    [Fact]
    public void Registrator_registers_every_service_as_a_singleton_and_the_event_consumers_as_scoped()
    {
        var services = new ServiceCollection();
        PluginServiceRegistrator.AddServices(services);

        Type[] expected =
        [
            typeof(CoverStore), typeof(FfmpegRunner), typeof(OverlayRenderer), typeof(LibraryAudience),
            typeof(ArtworkSource), typeof(SceneFinder), typeof(CoverApplier), typeof(CoverBuilder),
            typeof(RenderQueue),
        ];
        foreach (var type in expected)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        // The event consumers are scoped, the way Jellyfin registers its own: it resolves them in a scope per event.
        Type[] consumers = [typeof(IEventConsumer<UserUpdatedEventArgs>), typeof(IEventConsumer<UserCreatedEventArgs>)];
        foreach (var type in consumers)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
            Assert.Equal(typeof(AudienceChangeConsumer), descriptor.ImplementationType);
        }

        Assert.All(
            services.Where(d => !consumers.Contains(d.ServiceType)),
            d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
        Assert.Equal(3, services.Count(d => d.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public void A_real_provider_builds_the_plugin_services_and_shares_the_queue()
    {
        var root = Directory.CreateTempSubdirectory("registrator-").FullName;
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IApplicationPaths>(new Paths(root));
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Fake<ILibraryManager>(
                call => call.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : null));
            services.AddSingleton(Fake<ISessionManager>(
                call => call.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : null));
            services.AddSingleton(Fake<ITrickplayManager>());
            services.AddSingleton(Fake<IMediaSegmentManager>());
            services.AddSingleton(Fake<IChapterManager>());
            services.AddSingleton(Fake<IMediaEncoder>());
            services.AddSingleton(Fake<IUserManager>());
            services.AddSingleton(Fake<IUserDataManager>());
            services.AddSingleton(Fake<IProviderManager>());
            services.AddSingleton(Fake<IFileSystem>());
            PluginServiceRegistrator.AddServices(services);

            using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            // The factories: a mistyped dependency in either fails here.
            Assert.NotNull(provider.GetRequiredService<SceneFinder>());
            var queue = provider.GetRequiredService<RenderQueue>();
            Assert.NotNull(provider.GetRequiredService<CoverBuilder>());
            Assert.Same(provider.GetRequiredService<CoverApplier>(), provider.GetRequiredService<CoverApplier>());
            Assert.NotNull(ActivatorUtilities.CreateInstance<RefreshCoversTask>(provider));
            Assert.NotNull(ActivatorUtilities.CreateInstance<CoversController>(provider));

            var hosted = provider.GetServices<IHostedService>().ToList();
            Assert.Equal(3, hosted.Count);
            Assert.Same(queue, hosted[0]);
            Assert.IsType<RenewalScheduler>(hosted[1]);
            Assert.IsType<PrimaryImageWatcher>(hosted[2]);

            using var scope = provider.CreateScope();
            Assert.IsType<AudienceChangeConsumer>(
                scope.ServiceProvider.GetRequiredService<IEventConsumer<UserUpdatedEventArgs>>());
            Assert.IsType<AudienceChangeConsumer>(
                scope.ServiceProvider.GetRequiredService<IEventConsumer<UserCreatedEventArgs>>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_library_is_gone_only_when_the_list_and_a_direct_lookup_both_say_so()
    {
        var id = Guid.NewGuid();
        List<VirtualFolderInfo> folders = new();
        BaseItem? item = null;
        var libraries = Fake<ILibraryManager>(call => call.Name switch
        {
            "GetVirtualFolders" => folders,
            "GetItemById" => item,
            _ => null,
        });

        Assert.False(PluginServiceRegistrator.LibraryExists(libraries, id));

        item = new Movie();
        Assert.True(PluginServiceRegistrator.LibraryExists(libraries, id));

        item = null;
        folders = [new VirtualFolderInfo { ItemId = id.ToString("N") }];
        Assert.True(PluginServiceRegistrator.LibraryExists(libraries, id));
    }

    // A stand-in for a Jellyfin interface: every call answers with the default of its return type, unless the handler
    // gives an answer.
    private static T Fake<T>(Func<MethodInfo, object?>? answer = null)
        where T : class =>
        Fake<T>((call, _) => answer?.Invoke(call));

    // The same, for a handler that also reads the arguments of the call.
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> answer)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Answer = answer;
        return proxy;
    }

    private class FakeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Answer { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var given = Answer?.Invoke(targetMethod!, args);
            if (given is not null)
            {
                return given;
            }

            var type = targetMethod!.ReturnType;
            return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }

    [Fact]
    public async Task Plugin_own_image_is_never_kept_as_original()
    {
        Titles(TitleA);
        var own = _images.SetCurrent("folder.webp", [1, 2], automatic: false);
        _store.Update(Library, state => state.AppliedPrimaryPath = own);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        var state = _store.Load(Library);
        Assert.Null(state.Original);
        Assert.Single(_images.Saved);
    }

    [Fact]
    public async Task Library_without_eligible_titles_gets_the_name_only_cover()
    {
        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 0), result);
        var slot = Assert.Single(Assert.Single(_renderer.Plans).Slots);
        Assert.Equal(SlotKind.Image, slot.Kind);
        Assert.Equal(6, slot.DurationSeconds, 9);
        Assert.Equal(new SKColor(0x1b, 0x1d, 0x24), _renderer.FirstSlotColor);
        Assert.False(File.Exists(slot.Path));
        Assert.Empty(_store.Load(Library).History[0].TitleIds);
    }

    [Fact]
    public async Task Failed_title_is_replaced_by_another()
    {
        Titles(TitleA, TitleB, TitleC);
        _store.Update(Library, state => state.Style.SlotCount = 1);
        _source.Fails = asked => _source.Asked.Count == 1; // the first title asked for cannot be read

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 1), result);
        Assert.Equal(2, _source.Asked.Count);
        var record = _store.Load(Library).History[0];
        Assert.Equal(_source.Asked[1].Title, Assert.Single(record.TitleIds));
        Assert.NotEqual(_source.Asked[0].Title, _source.Asked[1].Title);
    }

    [Fact]
    public async Task Each_slot_tries_two_titles_then_art_falls_back_to_scenes_and_then_to_the_name()
    {
        Titles(TitleA, TitleB, TitleC, TitleD);
        _store.Update(Library, state => state.Style.SlotCount = 1);
        _source.Fails = _ => true;

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 0), result);
        Assert.Equal(
            new[] { CoverMode.Art, CoverMode.Art, CoverMode.Scenes, CoverMode.Scenes },
            _source.Asked.Select(asked => asked.Mode));
        // Within a mode a slot never tries the same title twice; scenes may try a title whose artwork failed.
        Assert.Equal(2, _source.Asked.Take(2).Select(asked => asked.Title).Distinct().Count());
        Assert.Equal(2, _source.Asked.Skip(2).Select(asked => asked.Title).Distinct().Count());
        Assert.Equal(SlotKind.Image, Assert.Single(_renderer.Plans[0].Slots).Kind);
    }

    [Fact]
    public async Task Art_mode_without_artwork_falls_back_to_scenes()
    {
        _source.Titles.AddRange(new[] { TitleA, TitleB }.Select(id => Candidate(id, backdrop: false)));

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 2), result);
        Assert.All(_source.Asked, asked => Assert.Equal(CoverMode.Scenes, asked.Mode));
        Assert.All(_renderer.Plans[0].Slots, slot => Assert.Equal(SlotKind.Video, slot.Kind));

        // The record keeps the library's own mode.
        Assert.Equal(CoverMode.Art, _store.Load(Library).History[0].Mode);
    }

    [Fact]
    public async Task Fewer_slots_than_planned_still_fill_the_length()
    {
        Titles(TitleA, TitleB);
        _store.Update(Library, state => state.Style.SlotCount = 2);
        _source.Fails = asked => asked.Title == TitleA; // and no other title is left to replace it

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(true, null, 1), result);
        var slot = Assert.Single(_renderer.Plans[0].Slots);
        Assert.Equal(CoverRenderer.SlotSeconds(6, 1, fade: true, fps: 12), slot.DurationSeconds);

        // A picture only zooms for longer: its title is not asked for again.
        Assert.Equal(new[] { TitleA, TitleB }, _source.Asked.Select(asked => asked.Title));
    }

    [Fact]
    public async Task Clips_are_chosen_again_at_the_longer_length_when_slots_are_missing()
    {
        Titles(TitleA, TitleB, TitleC);
        _store.Update(Library, state =>
        {
            state.Mode = CoverMode.Scenes;
            state.Style.SlotCount = 3;
        });
        _source.Fails = asked => asked.Title == TitleA || (asked.Title == TitleC && asked.Seconds > 3);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        // Three slots planned, one title fails with no other to replace it: B and C were checked at the length of
        // three slots and are asked again at the length of two. C has no clip that long, so B is asked a third time,
        // at the length of one, and B alone is rendered, as long as it was checked.
        var three = CoverRenderer.SlotSeconds(6, 3, fade: true, fps: 12);
        var two = CoverRenderer.SlotSeconds(6, 2, fade: true, fps: 12);
        var one = CoverRenderer.SlotSeconds(6, 1, fade: true, fps: 12);
        Assert.Equal(new BuildResult(true, null, 1), result);
        IEnumerable<double> LengthsOf(Guid title) =>
            _source.Asked.Where(asked => asked.Title == title).Select(asked => asked.Seconds);
        Assert.Equal(new[] { three, two, one }, LengthsOf(TitleB));
        Assert.Equal(new[] { three, two }, LengthsOf(TitleC));
        var slot = Assert.Single(_renderer.Plans[0].Slots);
        Assert.Equal(one, slot.DurationSeconds);
        Assert.Equal(new[] { TitleB }, _store.Load(Library).History[0].TitleIds);
    }

    [Fact]
    public async Task Without_a_clip_at_the_longer_length_the_checked_set_is_kept_at_its_own_length()
    {
        Titles(TitleA, TitleB);
        _store.Update(Library, state =>
        {
            state.Mode = CoverMode.Scenes;
            state.Style.SlotCount = 2;
        });
        var two = CoverRenderer.SlotSeconds(6, 2, fade: true, fps: 12);
        _source.Fails = asked => asked.Title == TitleA || asked.Seconds > two;

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        // B was checked at the length of two slots and has no clip at the length of one: B is rendered as checked,
        // a shorter cover, rather than the name-only one.
        Assert.Equal(new BuildResult(true, null, 1), result);
        var slot = Assert.Single(_renderer.Plans[0].Slots);
        Assert.Equal(SlotKind.Video, slot.Kind);
        Assert.Equal(two, slot.DurationSeconds);
        Assert.Equal(new[] { TitleB }, _store.Load(Library).History[0].TitleIds);
    }

    [Fact]
    public void Every_started_item_counts_its_plays_finished_or_not()
    {
        var finished = new Movie { Id = Guid.NewGuid() };
        var started = new Movie { Id = Guid.NewGuid() };
        var never = new Movie { Id = Guid.NewGuid() };
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var data = new Dictionary<Guid, UserItemData>
        {
            [finished.Id] = new() { Key = "f", PlayCount = 2, Played = true },
            [started.Id] = new() { Key = "s", PlayCount = 1, Played = false, PlaybackPositionTicks = 600_000_000 },
            [never.Id] = new() { Key = "n", PlayCount = 0 },
            [episode.Id] = new() { Key = "e", PlayCount = 3, Played = false },
        };
        var plays = new Dictionary<Guid, int>();

        JellyfinCoverSource.AddPlays(plays, [finished, started, never, episode], data);
        JellyfinCoverSource.AddPlays(plays, [started], data); // a second viewer who started the same film

        Assert.Equal(2, plays[finished.Id]);
        Assert.Equal(2, plays[started.Id]);
        Assert.False(plays.ContainsKey(never.Id));
        Assert.Equal(3, plays[seriesId]);
        Assert.False(plays.ContainsKey(episode.Id));
    }

    [Fact]
    public async Task Locked_moment_is_clamped_to_the_video_and_gets_its_crop()
    {
        var video = new Movie { Path = "/media/film.mkv", RunTimeTicks = 100 * TimeSpan.TicksPerSecond };
        var crop = new CropRect(0, 90, 1280, 540);
        (double Start, double Seconds)? measured = null;

        var slot = await CoverBuilder.LockedClipAsync(
            video,
            moment: 99,
            seconds: 4,
            hdr: true,
            (_, start, seconds, _) =>
            {
                measured = (start, seconds);
                return Task.FromResult<CropRect?>(crop);
            },
            CancellationToken.None);

        // The clip is measured where it is rendered: moved back so that it ends with the video.
        Assert.Equal((96.0, 4.0), measured);
        Assert.Equal(new SlotInput(SlotKind.Video, "/media/film.mkv", 96, 4, true, crop), slot);
    }

    [Fact]
    public async Task The_episode_a_series_slot_shows_is_recorded_and_checked_later()
    {
        var episode = Guid.NewGuid();
        Titles(TitleA);
        _source.Episodes[TitleA] = episode;
        _store.Update(Library, state => state.Mode = CoverMode.Scenes);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var first = _store.Load(Library).History[0];
        Assert.Equal(new[] { TitleA, episode }, first.TitleIds);

        // The series stays allowed while its episode becomes hidden: the pinned cover breaks the rule.
        _store.Update(Library, state => state.Pinned = true);
        _source.Episodes[TitleA] = Guid.NewGuid();
        _source.HiddenItems.Add(episode);
        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.True(result.Applied);
        var state = _store.Load(Library);
        Assert.NotEqual(first.Id, state.CurrentCoverId);
        Assert.DoesNotContain(episode, state.History[0].TitleIds);
    }

    [Fact]
    public async Task Upload_during_a_pinned_replacement_discards_the_render()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
        });
        _renderer.During = () =>
        {
            using var image = new MemoryStream([1, 2, 3]);
            _applier.SaveUploadAsync(Library, image, "image/png", CancellationToken.None).GetAwaiter().GetResult();
        };

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 2), result);
        var state = _store.Load(Library);
        Assert.Equal(CoverOrigin.Uploaded, state.History.Single(cover => cover.Id == state.CurrentCoverId).Origin);
        Assert.Equal("image/png", Assert.Single(_images.Saved).MimeType);
        Assert.False(File.Exists(_renderer.Plans[0].AnimatedOut));
    }

    [Fact]
    public async Task Another_cover_put_on_the_library_during_the_render_wins()
    {
        Titles(TitleA);
        var other = Record(CoverOrigin.Uploaded);
        _renderer.During = () => _store.Update(Library, state =>
        {
            state.History.Insert(0, other);
            state.CurrentCoverId = other.Id;
        });

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 1), result);
        Assert.Equal(other.Id, _store.Load(Library).CurrentCoverId);
        Assert.Empty(_images.Saved);
    }

    [Fact]
    public async Task Pinned_cover_that_no_longer_breaks_the_rule_is_kept()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
        });
        _renderer.During = () => _source.HiddenItems.Remove(Hidden); // the viewer's limits were relaxed meanwhile

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 2), result);
        Assert.Equal(old.Id, _store.Load(Library).CurrentCoverId);
        Assert.Empty(_images.Saved);
    }

    [Fact]
    public async Task Title_hidden_during_the_render_fails_the_build_and_keeps_the_cover()
    {
        Titles(TitleA, TitleB);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var before = _store.Load(Library).CurrentCoverId;
        _renderer.During = () => _source.HiddenItems.Add(TitleA);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("hidden", result.Error, StringComparison.Ordinal);
        var state = _store.Load(Library);
        Assert.Equal(before, state.CurrentCoverId);
        Assert.Equal(result.Error, state.LastError);
        Assert.Single(_images.Saved);
        Assert.False(File.Exists(_renderer.Plans[1].AnimatedOut));
        Assert.Equal(4, _source.ViewerCalls); // the viewers are read again at each commit
    }

    [Fact]
    public async Task Title_gone_during_the_render_says_so()
    {
        Titles(TitleA);
        _renderer.During = () => _source.MissingItems.Add(TitleA);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("A title of the new cover is no longer in the library, so it was not applied.", result.Error);
        Assert.Equal(result.Error, _store.Load(Library).LastError);
        Assert.Empty(_images.Saved);
    }

    [Fact]
    public async Task Manual_cover_that_no_longer_breaks_the_rule_is_kept()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Mode = CoverMode.Manual;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
        });
        _renderer.During = () => _source.HiddenItems.Remove(Hidden);

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 2), result);
        Assert.Equal(old.Id, _store.Load(Library).CurrentCoverId);
        Assert.Empty(_images.Saved);

        // Asked for by an admin, the same manual library is built whatever its cover shows.
        _renderer.During = null;
        Assert.True((await _builder.BuildAsync(Library, BuildReason.Manual, CancellationToken.None)).Applied);
    }

    [Fact]
    public async Task The_primary_format_is_the_one_set_when_the_cover_is_applied()
    {
        Titles(TitleA);

        // Saving the plugin's settings gives Jellyfin a new configuration object, as here.
        _renderer.During = () => _config = new PluginConfiguration
        {
            PrimaryFormat = PrimaryFormat.Still,
            OutputWidth = _config.OutputWidth,
            OutputHeight = _config.OutputHeight,
        };

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal("image/jpeg", Assert.Single(_images.Saved).MimeType);
    }

    [Fact]
    public async Task One_build_looks_the_library_item_up_once()
    {
        Titles(TitleA);
        _images.SetCurrent("collage.jpg", [9], automatic: true);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(1, _images.FindCalls);
    }

    [Fact]
    public async Task Missing_locked_title_is_unlocked_with_its_own_notice()
    {
        var gone = Guid.NewGuid();
        Titles(TitleA);
        _source.MissingItems.Add(gone);
        _store.Update(Library, state => state.Slots[2] = new CoverSlot { ItemId = gone });

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        var state = _store.Load(Library);
        Assert.False(state.Slots[2].Locked);
        Assert.Equal("Unlocked a title that is no longer in this library.", state.Notice);
    }

    [Fact]
    public async Task Locked_title_comes_first_with_the_chosen_backdrop()
    {
        Titles(TitleA, TitleB, TitleC);
        _store.Update(Library, state => state.Slots[1] = new CoverSlot { ItemId = TitleC, BackdropIndex = 2 });

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(TitleC, _store.Load(Library).History[0].TitleIds[0]);
        var asked = _source.Asked.Single(call => call.Title == TitleC);
        Assert.Equal(2, asked.Locked?.BackdropIndex);
        Assert.All(_source.Asked.Where(call => call.Title != TitleC), call => Assert.Null(call.Locked));
    }

    [Fact]
    public async Task Failed_locked_title_is_replaced_by_a_free_pick_and_stays_locked()
    {
        Titles(TitleA, TitleB);
        _store.Update(Library, state =>
        {
            state.Style.SlotCount = 1;
            state.Slots[0] = new CoverSlot { ItemId = TitleA, BackdropIndex = 3 };
        });
        _source.Fails = asked => asked.Title == TitleA;

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(new[] { TitleA, TitleB }, _source.Asked.Select(asked => asked.Title));
        Assert.Equal(3, _source.Asked[0].Locked?.BackdropIndex);
        Assert.Null(_source.Asked[1].Locked); // the lock's backdrop belongs to the locked title, not to its stand-in
        Assert.Equal(TitleA, _store.Load(Library).Slots[0].ItemId);
    }

    [Fact]
    public async Task Disallowed_locks_are_freed_with_a_notice()
    {
        Titles(TitleA, TitleB);
        _source.Names[Hidden] = "Hidden Film";
        _source.HiddenItems.Add(Hidden);
        _store.Update(Library, state => state.Slots[0] = new CoverSlot { ItemId = Hidden, MomentSeconds = 30 });

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.True(result.Applied);
        var state = _store.Load(Library);
        Assert.False(state.Slots[0].Locked);
        Assert.Null(state.Slots[0].MomentSeconds);
        Assert.Contains("Hidden Film", state.Notice, StringComparison.Ordinal);
        Assert.NotNull(state.NoticeAt);
        Assert.DoesNotContain(Hidden, state.History[0].TitleIds);
        Assert.DoesNotContain(_source.Asked, asked => asked.Title == Hidden);
    }

    [Fact]
    public async Task Pinned_library_is_left_alone_on_schedule_without_asking_for_titles()
    {
        Titles(TitleA);
        _store.Update(Library, state => state.Pinned = true);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 0), result);
        Assert.Equal(0, _source.CandidateCalls);
        Assert.Empty(_renderer.Plans);
    }

    [Fact]
    public async Task Pinned_generated_cover_that_breaks_the_rule_is_replaced_and_stays_pinned()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
        });

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.True(result.Applied);
        var state = _store.Load(Library);
        Assert.True(state.Pinned);
        Assert.NotEqual(old.Id, state.CurrentCoverId);
        Assert.DoesNotContain(Hidden, state.History[0].TitleIds);
    }

    [Fact]
    public async Task Disabled_library_is_not_built()
    {
        Titles(TitleA);
        _source.Info = _source.Info! with { Enabled = false };

        var result = await _builder.BuildAsync(Library, BuildReason.Manual, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 0), result);
        Assert.Empty(_renderer.Plans);
        Assert.False(File.Exists(Path.Combine(LibraryFolder, "state.json")));
    }

    [Fact]
    public async Task Missing_library_is_a_failure()
    {
        _source.Info = null;

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Render_failure_keeps_the_current_cover_and_says_why()
    {
        Titles(TitleA, TitleB);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var before = _store.Load(Library);
        _renderer.Failure = new CoverRenderException("ffmpeg exited with code 1", 1, "No such filter: 'nope'");

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("ffmpeg exited with code 1", result.Error);
        Assert.Equal(2, result.SlotsUsed);
        var after = _store.Load(Library);
        Assert.Equal(before.CurrentCoverId, after.CurrentCoverId);
        Assert.Single(after.History);
        Assert.Equal("ffmpeg exited with code 1", after.LastError);
        Assert.NotNull(after.LastErrorAt);
        Assert.Single(_images.Saved);
        Assert.False(File.Exists(_renderer.Plans[1].OverlayPngPath));

        // The next good render clears the error.
        _renderer.Failure = null;
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        Assert.Null(_store.Load(Library).LastError);
        Assert.Null(_store.Load(Library).LastErrorAt);
    }

    [Fact]
    public async Task Pin_during_the_render_discards_it()
    {
        Titles(TitleA);
        _renderer.During = () => _store.Update(Library, state => state.Pinned = true);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Null(result.Error);
        Assert.Empty(_images.Saved);
        var plan = _renderer.Plans[0];
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.StillOut));
        var state = _store.Load(Library);
        Assert.True(state.Pinned);
        Assert.Null(state.CurrentCoverId);
        Assert.Empty(state.History);
    }

    [Fact]
    public async Task Pin_during_a_replacement_applies_it_and_keeps_the_pin()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
            state.Notice = CoverRules.QueuedNotice("kid may not see \"Hidden Film\"");
        });
        _renderer.During = () => _store.Update(Library, state => state.Pinned = true);

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        // The pinned cover still shows the hidden title, so the admin's pin cannot keep it (decision 9).
        Assert.True(result.Applied);
        var state = _store.Load(Library);
        Assert.True(state.Pinned);
        Assert.NotEqual(old.Id, state.CurrentCoverId);
        Assert.DoesNotContain(Hidden, state.History[0].TitleIds);
        Assert.Equal("Replaced the cover because kid may not see \"Hidden Film\".", state.Notice);
        Assert.Equal(state.LastRenderedAt, state.NoticeAt);
    }

    [Fact]
    public async Task Switch_to_manual_during_a_replacement_applies_it_and_keeps_the_mode()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
        });
        _renderer.During = () => _store.Update(Library, state => state.Mode = CoverMode.Manual);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.True(result.Applied);
        var state = _store.Load(Library);
        Assert.Equal(CoverMode.Manual, state.Mode);
        Assert.NotEqual(old.Id, state.CurrentCoverId);
        Assert.StartsWith("Replaced the cover because a title on it may not be seen", state.Notice);
    }

    [Fact]
    public async Task Replacement_that_fails_says_so_and_keeps_the_reason()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
            state.Notice = CoverRules.QueuedNotice("kid may not see \"Hidden Film\"");
        });
        _renderer.Failure = new CoverRenderException("ffmpeg exited with code 1", 1, "boom");

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.False(result.Applied);
        var state = _store.Load(Library);
        Assert.Equal(old.Id, state.CurrentCoverId);
        Assert.Equal("ffmpeg exited with code 1", state.LastError);
        Assert.Equal(
            "Could not replace the cover yet, so it is tried again later. It was queued because kid may not see "
            + "\"Hidden Film\".",
            state.Notice);
        Assert.Equal(state.LastErrorAt, state.NoticeAt);

        // The retry that goes through says what happened, still naming the user.
        _renderer.Failure = null;
        Assert.True((await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None)).Applied);
        Assert.Equal("Replaced the cover because kid may not see \"Hidden Film\".", _store.Load(Library).Notice);
    }

    [Fact]
    public async Task Audit_replacement_that_also_frees_locks_keeps_the_unlock_text()
    {
        Titles(TitleA, TitleB);
        _source.Names[Hidden] = "Hidden Film";
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
            state.Slots[0] = new CoverSlot { ItemId = Hidden, MomentSeconds = 30 };
        });

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.True(result.Applied);
        var notice = _store.Load(Library).Notice;
        Assert.StartsWith("Replaced the cover because a title on it may not be seen", notice);
        Assert.EndsWith(
            "Unlocked \"Hidden Film\" because some viewers of this library may not see it.",
            notice,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queued_replacement_that_is_no_longer_needed_says_so()
    {
        Titles(TitleA, TitleB);
        var old = Record(CoverOrigin.Generated, Hidden); // Hidden is allowed again: the viewer's limits were relaxed
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
            state.Notice = CoverRules.QueuedNotice("kid may not see \"Hidden Film\"");
        });

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.Equal(new BuildResult(false, null, 0), result);
        var state = _store.Load(Library);
        Assert.Equal(old.Id, state.CurrentCoverId);
        Assert.Equal(CoverRules.SettledNotice(), state.Notice);
        Assert.NotNull(state.NoticeAt);
    }

    [Fact]
    public async Task Queued_replacement_dropped_at_commit_says_it_was_not_needed()
    {
        Titles(TitleA, TitleB);
        _source.HiddenItems.Add(Hidden);
        var old = Record(CoverOrigin.Generated, Hidden);
        _store.Update(Library, state =>
        {
            state.Pinned = true;
            state.History.Add(old);
            state.CurrentCoverId = old.Id;
            state.Notice = CoverRules.QueuedNotice("kid may not see \"Hidden Film\"");
        });
        _renderer.During = () => _source.HiddenItems.Remove(Hidden);

        var result = await _builder.BuildAsync(Library, BuildReason.AudienceChanged, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(CoverRules.SettledNotice(), _store.Load(Library).Notice);
    }

    [Fact]
    public async Task Mode_change_during_the_render_discards_it()
    {
        Titles(TitleA);
        _renderer.During = () => _store.Update(Library, state => state.Mode = CoverMode.Scenes);

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Empty(_images.Saved);
        Assert.Empty(_store.Load(Library).History);
    }

    [Fact]
    public async Task Cancelled_render_changes_nothing()
    {
        Titles(TitleA);
        using var cancel = new CancellationTokenSource();
        _renderer.During = () => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _builder.BuildAsync(Library, BuildReason.Scheduled, cancel.Token));

        Assert.Empty(_images.Saved);
        var plan = _renderer.Plans[0];
        Assert.False(File.Exists(plan.AnimatedOut));
        Assert.False(File.Exists(plan.OverlayPngPath));
        var state = _store.Load(Library);
        Assert.Null(state.LastError);
        Assert.Empty(state.History);
    }

    [Fact]
    public async Task Failed_apply_keeps_the_current_cover_and_says_why()
    {
        Titles(TitleA);
        _images.SaveFailure = new IOException("disk full");

        var result = await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("disk full", result.Error, StringComparison.Ordinal);
        var state = _store.Load(Library);
        Assert.Null(state.CurrentCoverId);
        Assert.Empty(state.History);
        Assert.Contains("disk full", state.LastError, StringComparison.Ordinal);
        Assert.False(File.Exists(_renderer.Plans[0].AnimatedOut));
    }

    [Fact]
    public async Task Every_build_renders_to_new_files()
    {
        Titles(TitleA);

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.NotEqual(_renderer.Plans[0].AnimatedOut, _renderer.Plans[1].AnimatedOut);
        Assert.NotEqual(_renderer.Plans[0].OverlayPngPath, _renderer.Plans[1].OverlayPngPath);
    }

    // ---- The applier -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_applying_mark_lasts_until_the_state_is_saved()
    {
        var record = Generated();
        bool? markedInChange = null;

        using (var run = _applier.StartApply(Library))
        {
            await _store.UpdateAsync(Library, async (state, token) =>
            {
                await run.ApplyAsync(state, record, PrimaryFormat.Animated, token);
                markedInChange = _applier.IsApplying(Library);
            });
            run.Saved();

            // Saved, not yet disposed: still marked, so an event raised by the save is never taken for an upload.
            Assert.True(_applier.IsApplying(Library));
        }

        Assert.True(markedInChange);
        Assert.False(_applier.IsApplying(Library));
        Assert.Equal(record.Id, _store.Load(Library).CurrentCoverId);
    }

    [Fact]
    public void Dropped_covers_keep_their_files_until_the_save()
    {
        _config.HistorySize = 1;
        var state = new LibraryCoverState { LibraryId = Library };
        var first = Generated();
        var second = Generated();
        state.CurrentCoverId = first.Id;
        Assert.Empty(_applier.AddToHistory(state, first));
        state.CurrentCoverId = second.Id;

        var dropped = _applier.AddToHistory(state, second);

        Assert.Equal(new[] { first.Id }, dropped);
        Assert.Equal(new[] { second.Id }, state.History.Select(cover => cover.Id));
        Assert.True(File.Exists(_store.AnimatedPath(Library, first.Id)));
        _applier.DeleteCovers(Library, dropped);
        Assert.False(File.Exists(_store.AnimatedPath(Library, first.Id)));
        Assert.False(File.Exists(_store.StillPath(Library, first.Id)));
        Assert.True(File.Exists(_store.AnimatedPath(Library, second.Id)));
    }

    [Fact]
    public async Task Own_cover_is_not_kept_as_original_even_when_the_state_lost_its_path()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        _store.Update(Library, state =>
        {
            state.Original = null;
            state.AppliedPrimaryPath = null;
        });

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        Assert.Null(_store.Load(Library).Original);
    }

    [Fact]
    public async Task Reapply_skips_libraries_that_are_gone()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        _images.Missing = true;
        var log = new CountingLogger();
        var applier = new CoverApplier(_images, _store, log, () => _config);

        await applier.ReapplyAllAsync(PrimaryFormat.Still, CancellationToken.None);

        Assert.Single(_images.Saved);
        Assert.Equal("image/webp", _images.Saved[0].MimeType);
        Assert.Equal(0, log.Warnings); // a library Jellyfin no longer has is not a failure to report
    }

    [Fact]
    public async Task Applier_marks_the_library_while_it_applies_and_records_the_image_first()
    {
        var record = Generated();
        var state = new LibraryCoverState { LibraryId = Library };
        string? recordedAtPersist = null;
        _images.OnPersist = () => recordedAtPersist = state.AppliedPrimaryPath;

        Assert.False(_applier.IsApplying(Library));
        await _applier.ApplyAsync(state, record, PrimaryFormat.Animated, CancellationToken.None);

        Assert.True(_images.ApplyingDuringSave);
        Assert.True(_images.ApplyingDuringPersist);
        Assert.False(_applier.IsApplying(Library));
        Assert.Equal(_images.Current!.Path, recordedAtPersist);
        Assert.Equal(record.Id, state.CurrentCoverId);
    }

    [Fact]
    public async Task Cancelled_apply_does_not_touch_the_library()
    {
        var state = new LibraryCoverState { LibraryId = Library };
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _applier.ApplyAsync(state, Generated(), PrimaryFormat.Animated, cancel.Token));

        Assert.Empty(_images.Saved);
        Assert.Null(state.CurrentCoverId);
        Assert.False(_applier.IsApplying(Library));
    }

    [Fact]
    public async Task Upload_is_applied_pinned_and_kept_in_history()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        using (var image = new MemoryStream(png))
        {
            await _applier.SaveUploadAsync(Library, image, "image/png", CancellationToken.None);
        }

        var state = _store.Load(Library);
        Assert.True(state.Pinned);
        var record = Assert.Single(state.History);
        Assert.Equal(CoverOrigin.Uploaded, record.Origin);
        Assert.Equal(record.Id, state.CurrentCoverId);
        var file = CoverApplier.CoverFile(_store, Library, record, PrimaryFormat.Still);
        Assert.NotNull(file);
        Assert.Equal("image/png", file.Value.MimeType);
        Assert.Equal(png, File.ReadAllBytes(file.Value.Path));
        var saved = Assert.Single(_images.Saved);
        Assert.Equal("image/png", saved.MimeType);
        Assert.Equal(png, saved.Bytes);
        Assert.True(_images.ApplyingDuringSave);
        Assert.False(_applier.IsApplying(Library));
    }

    [Fact]
    public async Task Upload_of_an_unknown_type_is_refused()
    {
        using var image = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _applier.SaveUploadAsync(Library, image, "image/svg+xml", CancellationToken.None));

        Assert.Empty(_images.Saved);
    }

    [Fact]
    public async Task Failed_upload_leaves_no_file_and_no_state()
    {
        _images.SaveFailure = new IOException("disk full");
        using var image = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<IOException>(
            () => _applier.SaveUploadAsync(Library, image, "image/gif", CancellationToken.None));

        var covers = Path.Combine(LibraryFolder, "covers");
        Assert.True(!Directory.Exists(covers) || Directory.GetFiles(covers).Length == 0);
        Assert.Empty(_store.Load(Library).History);
    }

    [Fact]
    public async Task Dropped_uploads_lose_their_files()
    {
        _config.HistorySize = 1;
        for (var i = 0; i < 3; i++)
        {
            using var image = new MemoryStream([1, 2, (byte)i]);
            await _applier.SaveUploadAsync(Library, image, "image/gif", CancellationToken.None);
        }

        var covers = Path.Combine(LibraryFolder, "covers");
        var kept = Assert.Single(_store.Load(Library).History);
        Assert.Equal(new[] { kept.Id.ToString("N") + ".gif" }, Directory.GetFiles(covers).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Reapply_to_configured_reads_the_switch_as_each_library_is_applied_so_the_last_change_wins()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        _config.PrimaryFormat = PrimaryFormat.Still;
        Task? second = null;

        // The admin flips the switch back while the first run is applying the library: the second run has to wait for
        // the first and then leave the library in the format the configuration has now.
        _images.OnPersist = () =>
        {
            if (second is null)
            {
                _config.PrimaryFormat = PrimaryFormat.Animated;
                second = _applier.ReapplyAllToConfiguredAsync(CancellationToken.None);
            }
        };
        await _applier.ReapplyAllToConfiguredAsync(CancellationToken.None);
        await second!;

        Assert.Equal(["image/webp", "image/jpeg", "image/webp"], _images.Saved.Select(saved => saved.MimeType));
    }

    [Fact]
    public async Task Reapply_switches_generated_covers_to_the_other_version()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var current = _store.Load(Library).CurrentCoverId;

        await _applier.ReapplyAllAsync(PrimaryFormat.Still, CancellationToken.None);

        Assert.Equal(2, _images.Saved.Count);
        Assert.Equal("image/jpeg", _images.Saved[1].MimeType);
        Assert.Equal(File.ReadAllBytes(_store.StillPath(Library, current!.Value)), _images.Saved[1].Bytes);
        Assert.Equal(current, _store.Load(Library).CurrentCoverId);
    }

    [Fact]
    public async Task Reapply_leaves_uploaded_covers_alone()
    {
        using (var image = new MemoryStream([1, 2, 3]))
        {
            await _applier.SaveUploadAsync(Library, image, "image/webp", CancellationToken.None);
        }

        await _applier.ReapplyAllAsync(PrimaryFormat.Still, CancellationToken.None);

        Assert.Single(_images.Saved);
    }

    // ---- Reactions to users and to Jellyfin's Edit images dialog ---------------------------------------------------

    [Fact]
    public async Task Tightening_a_policy_queues_a_pinned_generated_cover_at_once()
    {
        var kid = Viewer("kid");
        var access = new FakeAccess { Viewers = [kid] };
        access.HiddenFrom[kid.Id] = [Hidden];
        access.Names[Hidden] = "Film 2";
        var current = Record(CoverOrigin.Generated, TitleA, Hidden);
        Seed(state =>
        {
            state.Pinned = true;
            state.History.Add(current);
            state.CurrentCoverId = current.Id;
        });
        var (consumer, queued) = Consumer(access);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        Assert.Equal(new[] { (Library, BuildReason.AudienceChanged) }, queued);
        var state = _store.Load(Library);
        Assert.Equal("Queued for a new cover because kid may not see \"Film 2\".", state.Notice);
        Assert.NotNull(state.NoticeAt);
        Assert.Equal(current.Id, state.CurrentCoverId); // the build replaces it, not the check
        Assert.True(state.Pinned);
    }

    [Fact]
    public async Task A_new_user_who_may_not_see_a_locked_title_unlocks_it_and_leaves_an_upload_alone()
    {
        var kid = Viewer("kid");
        var access = new FakeAccess { Viewers = [Viewer("admin"), kid] };
        access.HiddenFrom[kid.Id] = [Hidden];
        access.Names[Hidden] = "Film 2";
        var upload = Record(CoverOrigin.Uploaded, Hidden);
        Seed(state =>
        {
            state.Pinned = true;
            state.History.Add(upload);
            state.CurrentCoverId = upload.Id;
            state.Slots[0].ItemId = TitleA;
            state.Slots[1].ItemId = Hidden;
            state.Slots[1].MomentSeconds = 12;
        });
        var (consumer, queued) = Consumer(access);

        await consumer.OnEvent(new UserCreatedEventArgs(kid));

        var state = _store.Load(Library);
        Assert.Equal(TitleA, state.Slots[0].ItemId);
        Assert.Null(state.Slots[1].ItemId);
        Assert.Null(state.Slots[1].MomentSeconds);
        Assert.Equal("Unlocked \"Film 2\" because kid may not see it.", state.Notice);
        Assert.Equal(upload.Id, state.CurrentCoverId);
        Assert.Single(state.History);
        Assert.Equal(new[] { (Library, BuildReason.AudienceChanged) }, queued);
    }

    [Fact]
    public async Task A_title_hidden_from_another_viewer_or_gone_is_named_without_blaming_the_user()
    {
        var kid = Viewer("kid");
        var teen = Viewer("teen");
        var access = new FakeAccess { Viewers = [kid, teen] };
        access.HiddenFrom[teen.Id] = [Hidden];
        access.Missing.Add(TitleB);
        access.Names[Hidden] = "Film 2";
        var current = Record(CoverOrigin.Generated, Hidden, TitleB);
        Seed(state =>
        {
            state.History.Add(current);
            state.CurrentCoverId = current.Id;
        });
        var (consumer, queued) = Consumer(access);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        Assert.Single(queued);
        Assert.Equal(
            "Queued for a new cover because some viewers of this library may not see \"Film 2\" and a title is no "
            + "longer in the library.",
            _store.Load(Library).Notice);
    }

    [Fact]
    public async Task A_library_the_user_does_not_see_or_a_cover_that_keeps_the_rule_is_left_alone()
    {
        var kid = Viewer("kid");
        var access = new FakeAccess { Viewers = [Viewer("admin")] };
        access.HiddenFrom[kid.Id] = [Hidden];
        var current = Record(CoverOrigin.Generated, Hidden);
        Seed(state =>
        {
            state.History.Add(current);
            state.CurrentCoverId = current.Id;
        });
        var (consumer, queued) = Consumer(access);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));
        access.Viewers = [kid];
        access.HiddenFrom[kid.Id] = [];
        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        Assert.Empty(queued);
        Assert.Null(_store.Load(Library).Notice);
    }

    [Fact]
    public async Task A_library_Jellyfin_no_longer_has_is_not_checked()
    {
        var kid = Viewer("kid");
        var access = new FakeAccess { Viewers = [kid], Exists = false };
        access.HiddenFrom[kid.Id] = [Hidden];
        var current = Record(CoverOrigin.Generated, Hidden);
        Seed(state =>
        {
            state.History.Add(current);
            state.CurrentCoverId = current.Id;
        });
        var (consumer, queued) = Consumer(access);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        Assert.Empty(queued);
    }

    [Fact]
    public async Task A_failing_check_is_logged_never_thrown_at_Jellyfin_and_the_library_queued()
    {
        var kid = Viewer("kid");
        var access = new FakeAccess { Viewers = [kid], Failure = new InvalidOperationException("database busy") };
        Seed(_ => { });
        var log = new CountingLogger();
        var (consumer, queued) = Consumer(access, log);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        Assert.Equal(new[] { (Library, BuildReason.AudienceChanged) }, queued);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public async Task A_library_whose_check_fails_is_queued_and_the_others_are_still_checked()
    {
        var kid = Viewer("kid");
        var other = Guid.Parse("7b2e4d3f-9c5e-4f60-8b1c-2d3e4f5a6b7c");
        var access = new FakeAccess { Viewers = [kid] };
        access.HiddenFrom[kid.Id] = [Hidden];
        access.Names[Hidden] = "Film 2";
        access.FailFor.Add(Library);
        foreach (var id in new[] { Library, other })
        {
            var current = Record(CoverOrigin.Generated, Hidden);
            _store.Update(id, state =>
            {
                state.History.Add(current);
                state.CurrentCoverId = current.Id;
            });
        }

        var log = new CountingLogger();
        var (consumer, queued) = Consumer(access, log);

        await consumer.OnEvent(new UserUpdatedEventArgs(kid));

        // Whatever order the store lists them in.
        var expected = new[] { (Library, BuildReason.AudienceChanged), (other, BuildReason.AudienceChanged) };
        Assert.Equal(expected.OrderBy(q => q.Item1), queued.OrderBy(q => q.Item1));
        Assert.Equal(1, log.Warnings);
        Assert.Null(_store.Load(Library).Notice);
        Assert.Equal("Queued for a new cover because kid may not see \"Film 2\".", _store.Load(other).Notice);
    }

    [Fact]
    public async Task Edit_images_upload_is_kept_as_an_uploaded_cover_and_pins_the_library()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var generated = _store.Load(Library).CurrentCoverId;
        var watcher = Watcher();

        var path = _images.SetCurrent("folder.png", [9, 8, 7], automatic: false);
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));
        await watcher.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var state = _store.Load(Library);
        Assert.True(state.Pinned);
        var current = state.History.Single(cover => cover.Id == state.CurrentCoverId);
        Assert.Equal(CoverOrigin.Uploaded, current.Origin);
        Assert.Equal(generated, state.History[1].Id);
        var file = CoverApplier.CoverFile(_store, Library, current, PrimaryFormat.Animated);
        Assert.NotNull(file);
        Assert.Equal(Path.Combine(LibraryFolder, "covers", current.Id.ToString("N") + ".png"), file.Value.Path);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(file.Value.Path));
        Assert.Equal(path, state.AppliedPrimaryPath);
        Assert.Equal(_images.Current!.Modified, state.AppliedPrimaryModified);
        Assert.Equal(PrimaryImageWatcher.EditImagesNotice, state.Notice);
        Assert.NotNull(state.NoticeAt);
        Assert.Single(_images.Saved); // nothing applied again: the image is already the Primary
    }

    [Fact]
    public async Task Edit_images_upload_on_an_untouched_library_is_never_kept_as_its_original()
    {
        Titles(TitleA);
        var watcher = Watcher();
        _images.SetCurrent("folder.jpg", [5, 5], automatic: false);
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));
        await watcher.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_store.Load(Library).Pinned);

        _store.Update(Library, state => state.Pinned = false);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);

        var state = _store.Load(Library);
        Assert.Null(state.Original);
        Assert.Equal(CoverOrigin.Generated, state.History[0].Origin);
        Assert.Equal(CoverOrigin.Uploaded, state.History[1].Origin);
    }

    [Fact]
    public async Task The_plugin_own_apply_is_not_taken_for_an_upload_and_does_not_wait_for_the_store()
    {
        Titles(TitleA);
        var watcher = Watcher();
        _images.OnPersist = () => watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));

        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        _images.OnPersist = null;

        // A later event about the same image (a refresh of the library) changes nothing either.
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataImport));
        await watcher.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var state = _store.Load(Library);
        Assert.False(state.Pinned);
        Assert.Null(state.Notice);
        Assert.Equal(CoverOrigin.Generated, Assert.Single(state.History).Origin);
    }

    [Fact]
    public async Task Automatic_images_other_updates_and_old_images_are_not_uploads()
    {
        var watcher = Watcher(() => DateTime.UtcNow.AddHours(1));

        _images.SetCurrent("collage.jpg", [1], automatic: true);
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));
        _images.SetCurrent("folder.jpg", [2], automatic: false);
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.MetadataImport));

        // An image an admin set long before, seen again when the Logo changes: the library never had a plugin cover,
        // so only an image written just now counts.
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));
        watcher.OnItemUpdated(null, new ItemChangeEventArgs { Item = new Movie { Id = Library } });
        await watcher.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_store.KnownLibraries());
    }

    [Theory]
    [InlineData("folder.webp", 0.5, false)]
    [InlineData("folder.webp", -0.5, false)]
    [InlineData("folder.webp", 2, true)]
    [InlineData("folder.png", 0, true)]
    public void An_image_is_an_outside_change_when_its_path_or_time_differs(string name, double seconds, bool outside)
    {
        var applied = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        var state = new LibraryCoverState
        {
            LibraryId = Library,
            AppliedPrimaryPath = "/config/root/default/Movies/folder.webp",
            AppliedPrimaryModified = applied,
        };
        var image = new PrimaryImage(
            "/config/root/default/Movies/" + name, applied.AddSeconds(seconds), IsLocalFile: true, Automatic: false);

        // Long after the apply: the window for untouched libraries plays no part here.
        Assert.Equal(outside, PrimaryImageWatcher.IsOutsideChange(state, image, applied.AddDays(3)));
    }

    [Fact]
    public async Task A_Logo_upload_on_a_library_with_a_plugin_cover_does_not_pin_it()
    {
        Titles(TitleA);
        await _builder.BuildAsync(Library, BuildReason.Scheduled, CancellationToken.None);
        var before = _store.Load(Library);
        var watcher = Watcher();

        // The dialog saved a Logo: Jellyfin raises the same ImageUpdate, and the Primary is the plugin's cover still.
        watcher.OnItemUpdated(null, Updated(ItemUpdateType.ImageUpdate));
        await watcher.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var state = _store.Load(Library);
        Assert.False(state.Pinned);
        Assert.Null(state.Notice);
        Assert.Equal(before.CurrentCoverId, state.CurrentCoverId);
        Assert.Single(state.History);
    }

    [Fact]
    public async Task Watcher_follows_the_library_manager_from_start_to_stop()
    {
        var handlers = new List<string>();
        var libraries = Fake<ILibraryManager>((call, args) =>
        {
            if (call.Name is "add_ItemUpdated" or "remove_ItemUpdated" && args?[0] is EventHandler<ItemChangeEventArgs>)
            {
                handlers.Add(call.Name);
            }

            return null;
        });
        var watcher = new PrimaryImageWatcher(
            libraries, _applier, NullLogger<PrimaryImageWatcher>.Instance, () => DateTime.UtcNow);

        await watcher.StartAsync(CancellationToken.None);
        Assert.Equal(new[] { "add_ItemUpdated" }, handlers);
        await watcher.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { "add_ItemUpdated", "remove_ItemUpdated" }, handlers);
    }

    private static User Viewer(string name) => new(name, "Default", "Default") { Id = Guid.NewGuid() };

    private void Seed(Action<LibraryCoverState> change) => _store.Update(Library, change);

    private (AudienceChangeConsumer Consumer, List<(Guid, BuildReason)> Queued) Consumer(
        FakeAccess access,
        Microsoft.Extensions.Logging.ILogger? log = null)
    {
        var queued = new List<(Guid, BuildReason)>();
        var consumer = new AudienceChangeConsumer(
            access,
            _store,
            (id, reason) => queued.Add((id, reason)),
            log ?? NullLogger.Instance,
            () => _config);
        return (consumer, queued);
    }

    private PrimaryImageWatcher Watcher(Func<DateTime>? utcNow = null) => new(
        Fake<ILibraryManager>(),
        _applier,
        NullLogger<PrimaryImageWatcher>.Instance,
        utcNow ?? (() => DateTime.UtcNow));

    private static ItemChangeEventArgs Updated(ItemUpdateType reason) =>
        new() { Item = new CollectionFolder { Id = Library }, UpdateReason = reason };

    /// <summary>Who sees the library and what each of them may see, without a server.</summary>
    private sealed class FakeAccess : IViewerAccess
    {
        public IReadOnlyList<User> Viewers { get; set; } = [];

        public Dictionary<Guid, Guid[]> HiddenFrom { get; } = new();

        public HashSet<Guid> Missing { get; } = new();

        public Dictionary<Guid, string> Names { get; } = new();

        public bool Exists { get; set; } = true;

        public Exception? Failure { get; set; }

        // Libraries whose check fails, whatever Failure says.
        public HashSet<Guid> FailFor { get; } = new();

        public bool LibraryExists(Guid libraryId) => Exists;

        public IReadOnlyList<User> ViewersOf(Guid libraryId) =>
            Failure is not null ? throw Failure
            : FailFor.Contains(libraryId) ? throw new IOException("state.json is locked")
            : Viewers;

        public bool MaySee(Guid itemId, IReadOnlyList<User> users) =>
            !Missing.Contains(itemId)
            && users.All(user => !HiddenFrom.TryGetValue(user.Id, out var hidden) || !hidden.Contains(itemId));

        public string? TitleName(Guid itemId) =>
            Missing.Contains(itemId) ? null : Names.TryGetValue(itemId, out var name) ? name : itemId.ToString();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static CoverRecord Record(CoverOrigin origin, params Guid[] titles) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        Origin = origin,
        TitleIds = titles.ToList(),
    };

    private static TitleCandidate Candidate(Guid id, bool backdrop = true) =>
        new(id, 0, CommunityRating: null, new DateTime(2026, 1, 1), HasBackdrop: backdrop, HasVideo: true);

    // A generated cover with both of its files in the store.
    private CoverRecord Generated()
    {
        var record = Record(CoverOrigin.Generated, TitleA);
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AnimatedPath(Library, record.Id))!);
        File.WriteAllBytes(_store.AnimatedPath(Library, record.Id), [1]);
        File.WriteAllBytes(_store.StillPath(Library, record.Id), [2]);
        return record;
    }

    private void Titles(params Guid[] ids) => _source.Titles.AddRange(ids.Select(id => Candidate(id)));

    private string WriteFile(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The library, its titles and their slots, without a server.</summary>
    private sealed class FakeSource : ICoverSource
    {
        private readonly string _media = Directory.CreateTempSubdirectory("cover-rules-media-").FullName;

        public LibraryInfo? Info { get; set; } = new(CoverRulesTests.Library, "Movies", "movies", Enabled: true);

        public List<TitleCandidate> Titles { get; } = new();

        public Dictionary<Guid, string> Names { get; } = new();

        // Items some viewer may not see, and items no longer in the library. Everything else is allowed.
        public HashSet<Guid> HiddenItems { get; } = new();

        public HashSet<Guid> MissingItems { get; } = new();

        // The episode a series title shows in a scenes slot.
        public Dictionary<Guid, Guid> Episodes { get; } = new();

        public List<(Guid Title, CoverMode Mode, CoverSlot? Locked, double Seconds)> Asked { get; } = new();

        public Func<(Guid Title, CoverMode Mode, CoverSlot? Locked, double Seconds), bool> Fails { get; set; } =
            _ => false;

        public int CandidateCalls { get; private set; }

        public int ViewerCalls { get; private set; }

        public LibraryInfo? Library(Guid libraryId) => Info is { } info && info.Id == libraryId ? info : null;

        public IReadOnlyList<User> Viewers(LibraryInfo library)
        {
            ViewerCalls++;
            return [];
        }

        public IReadOnlyList<TitleCandidate> Candidates(LibraryInfo library, IReadOnlyList<User> viewers)
        {
            CandidateCalls++;
            return Titles.Where(title => Access(library, title.Id, viewers) == TitleAccess.Allowed).ToList();
        }

        public TitleAccess Access(LibraryInfo library, Guid itemId, IReadOnlyList<User> viewers) =>
            MissingItems.Contains(itemId) ? TitleAccess.Missing
            : HiddenItems.Contains(itemId) ? TitleAccess.Hidden
            : TitleAccess.Allowed;

        public string TitleName(Guid titleId) => Names.TryGetValue(titleId, out var name) ? name : titleId.ToString();

        public Task<SlotChoice?> SlotAsync(
            LibraryInfo library,
            IReadOnlyList<User> viewers,
            Guid titleId,
            CoverMode mode,
            CoverSlot? locked,
            double seconds,
            Random random,
            CancellationToken ct)
        {
            var asked = (titleId, mode, locked, seconds);
            Asked.Add(asked);
            if (Fails(asked))
            {
                return Task.FromResult<SlotChoice?>(null);
            }

            var path = Path.Combine(_media, titleId.ToString("N") + (mode == CoverMode.Scenes ? ".mkv" : ".jpg"));
            var kind = mode == CoverMode.Scenes ? SlotKind.Video : SlotKind.Image;
            Guid? shown = mode == CoverMode.Scenes && Episodes.TryGetValue(titleId, out var episode) ? episode : null;
            return Task.FromResult<SlotChoice?>(new SlotChoice(new SlotInput(kind, path, 0, seconds, false), shown));
        }
    }

    /// <summary>The library's Primary image, kept the way Jellyfin keeps it: SaveImage replaces the file.</summary>
    private sealed class FakeImages(string folder) : ILibraryImages, ILibraryImage
    {
        private int _saves;

        public CoverApplier? Applier { get; set; }

        public PrimaryImage? Current { get; private set; }

        public List<(string MimeType, byte[] Bytes)> Saved { get; } = new();

        public Exception? SaveFailure { get; set; }

        public Action? OnPersist { get; set; }

        public bool Missing { get; set; }

        public int FindCalls { get; private set; }

        public bool ApplyingDuringSave { get; private set; }

        public bool ApplyingDuringPersist { get; private set; }

        public string SetCurrent(string name, byte[] bytes, bool automatic)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            Current = new PrimaryImage(path, File.GetLastWriteTimeUtc(path), IsLocalFile: true, Automatic: automatic);
            return path;
        }

        public ILibraryImage? Find(Guid libraryId)
        {
            FindCalls++;
            return Missing ? null : this;
        }

        public PrimaryImage? Primary() => Current;

        public async Task SaveAsync(Stream image, string mimeType, CancellationToken ct)
        {
            ApplyingDuringSave = Applier!.IsApplying(Library);
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            using var copy = new MemoryStream();
            await image.CopyToAsync(copy, ct);
            Saved.Add((mimeType, copy.ToArray()));
            if (Current is not null)
            {
                File.Delete(Current.Path);
            }

            var extension = mimeType switch
            {
                "image/webp" => ".webp",
                "image/png" => ".png",
                "image/gif" => ".gif",
                _ => ".jpg",
            };
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "folder" + extension);
            File.WriteAllBytes(path, copy.ToArray());
            Current = new PrimaryImage(path, new DateTime(2026, 10, 16, 3, 0, ++_saves, DateTimeKind.Utc), true, false);
        }

        public Task PersistAsync()
        {
            ApplyingDuringPersist = Applier!.IsApplying(Library);
            OnPersist?.Invoke();
            return Task.CompletedTask;
        }

        // The tests that use this fake never return a library to the collage (CoversApiTests does).
        public Task ResetToAutomaticAsync() => throw new NotSupportedException();
    }

    /// <summary>Counts what is logged at warning level or above.</summary>
    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public int Warnings { get; private set; }

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
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    /// <summary>Stands in for ffmpeg: writes small files where the plan says, or fails as told.</summary>
    private sealed class FakeRenderer
    {
        public List<RenderPlan> Plans { get; } = new();

        public CoverRenderException? Failure { get; set; }

        public Action? During { get; set; }

        public bool OverlayExisted { get; private set; }

        public SKColor? FirstSlotColor { get; private set; }

        public Task RenderAsync(RenderPlan plan, CancellationToken ct)
        {
            Plans.Add(plan);
            OverlayExisted = File.Exists(plan.OverlayPngPath);
            if (plan.Slots[0].Kind == SlotKind.Image && File.Exists(plan.Slots[0].Path))
            {
                using var bitmap = SKBitmap.Decode(plan.Slots[0].Path);
                FirstSlotColor = bitmap?.GetPixel(0, 0);
            }

            During?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(plan.AnimatedOut)!);
            File.WriteAllBytes(plan.AnimatedOut, [(byte)'W', (byte)Plans.Count]);
            File.WriteAllBytes(plan.StillOut, [(byte)'J', (byte)Plans.Count]);
            return Task.CompletedTask;
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
