using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Api;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Scheduling;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The admin API's endpoints called directly, with Jellyfin's library and users replaced: what a history restore
/// accepts under the visibility rule (decision 9).
/// </summary>
public sealed class CoversControllerTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("7c2e4f60-1a3b-4d5c-9e8f-0a1b2c3d4e5f");
    private static readonly Guid Shown = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Earlier = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Restored = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private readonly string _root = Directory.CreateTempSubdirectory("covers-controller-tests-").FullName;
    private readonly CoverStore _store;
    private readonly Images _images;
    private readonly List<User> _users = new();
    private readonly Dictionary<Guid, BaseItem> _items = new();

    public CoversControllerTests()
    {
        _store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        _images = new Images(Path.Combine(_root, "jellyfin"));
        var folder = new CollectionFolder { Id = Library, Name = "Movies" };
        _items[Library] = folder;
        _items[Shown] = new Movie { Id = Shown, Name = "Unrated film" };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Restoring_a_generated_cover_with_a_title_a_viewer_may_not_see_is_refused()
    {
        _users.Add(NewUser("adult"));
        _users.Add(NewUser("kid", blocksUnratedFilms: true));
        History(Generated(Restored, Shown));

        var answer = await Controller().Restore(Library, Restored, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(answer.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var error = Assert.IsType<ApiError>(conflict.Value);
        Assert.Contains("may not see", error.Detail, StringComparison.Ordinal);
        NothingChanged();
    }

    [Fact]
    public async Task Restoring_a_generated_cover_with_a_title_no_longer_in_the_library_is_refused()
    {
        _users.Add(NewUser("adult"));
        History(Generated(Restored, Shown, Gone));

        var answer = await Controller().Restore(Library, Restored, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(answer.Result);
        Assert.Contains("no longer in this library", Assert.IsType<ApiError>(conflict.Value).Detail);
        NothingChanged();
    }

    [Fact]
    public async Task Restoring_a_generated_cover_every_viewer_may_see_applies_and_pins_it()
    {
        _users.Add(NewUser("adult"));
        History(Generated(Restored, Shown));

        var answer = await Controller().Restore(Library, Restored, CancellationToken.None);

        Assert.Null(answer.Result);
        var state = _store.Load(Library);
        Assert.Equal(Restored, state.CurrentCoverId);
        Assert.True(state.Pinned);
        Assert.Single(_images.Saved);
    }

    [Fact]
    public async Task An_uploaded_cover_stays_restorable_whoever_watches()
    {
        // The plugin cannot tell what an upload shows, so the rule does not apply to it (decision 9).
        _users.Add(NewUser("kid", blocksUnratedFilms: true));
        var upload = new CoverRecord
        {
            Id = Restored,
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = CoverOrigin.Uploaded,
            Mode = CoverMode.Manual,
        };
        var file = Path.ChangeExtension(_store.AnimatedPath(Library, Restored), ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3]);
        History(upload);

        var answer = await Controller().Restore(Library, Restored, CancellationToken.None);

        Assert.Null(answer.Result);
        Assert.Equal(Restored, _store.Load(Library).CurrentCoverId);
        Assert.True(_store.Load(Library).Pinned);
    }

    // The library shows Earlier, a cover every viewer may see; the cover to restore is the older one.
    private void History(CoverRecord toRestore)
    {
        var current = Generated(Earlier);
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AnimatedPath(Library, Earlier))!);
        if (toRestore.Origin == CoverOrigin.Generated)
        {
            File.WriteAllBytes(_store.AnimatedPath(Library, toRestore.Id), [1, 2, 3]);
            File.WriteAllBytes(_store.StillPath(Library, toRestore.Id), [4, 5, 6]);
        }

        _store.Update(Library, state =>
        {
            state.History = [current, toRestore];
            state.CurrentCoverId = Earlier;
        });
    }

    private void NothingChanged()
    {
        var state = _store.Load(Library);
        Assert.Equal(Earlier, state.CurrentCoverId);
        Assert.False(state.Pinned);
        Assert.Empty(_images.Saved);
    }

    private static CoverRecord Generated(Guid id, params Guid[] titles) => new()
    {
        Id = id,
        CreatedAt = DateTimeOffset.UtcNow,
        Origin = CoverOrigin.Generated,
        Mode = CoverMode.Art,
        TitleIds = titles.ToList(),
    };

    private static User NewUser(string name, bool blocksUnratedFilms = false)
    {
        var user = new User(name, "Default", "Default") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        user.SetPermission(PermissionKind.IsDisabled, false);
        user.SetPreference(PreferenceKind.EnabledFolders, Array.Empty<Guid>());
        user.SetPreference(PreferenceKind.BlockedMediaFolders, Array.Empty<Guid>());
        if (blocksUnratedFilms)
        {
            user.SetPreference(PreferenceKind.BlockUnratedItems, new[] { UnratedItem.Movie });
        }

        return user;
    }

    private CoversController Controller()
    {
        var library = SceneFinderTests.Stub<ILibraryManager>(new()
        {
            ["GetItemById"] = args => _items.GetValueOrDefault((Guid)args[0]!),
            ["GetCollectionFolders"] = args => args[0] is BaseItem item && item.Id != Library
                ? new List<Folder> { (Folder)_items[Library] }
                : new List<Folder>(),
        });
        var users = SceneFinderTests.Stub<IUserManager>(new() { ["GetUsers"] = _ => _users });
        var applier = new CoverApplier(_images, _store, NullLogger<CoverApplier>.Instance, () => new());
        _images.Applier = applier;
        var queue = new RenderQueue(
            (_, _, _) => Task.FromResult(new BuildResult(false, null, 0)),
            () => false,
            _ => true,
            _store,
            NullLogger<RenderQueue>.Instance);
        var ffmpeg = new FfmpegRunner(
            SceneFinderTests.Stub<IMediaEncoder>(new()),
            NullLogger<FfmpegRunner>.Instance);
        return new CoversController(
            library,
            _store,
            applier,
            queue,
            new LibraryAudience(users),
            ffmpeg,
            NullLogger<CoversController>.Instance);
    }

    /// <summary>The library's Primary image: saving one replaces the file, as Jellyfin's SaveImage does.</summary>
    private sealed class Images(string folder) : ILibraryImages, ILibraryImage
    {
        private PrimaryImage? _current;

        public CoverApplier? Applier { get; set; }

        public List<string> Saved { get; } = new();

        public ILibraryImage? Find(Guid libraryId) => this;

        public PrimaryImage? Primary() => _current;

        public async Task SaveAsync(Stream image, string mimeType, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "folder" + Saved.Count);
            await using (var file = File.Create(path))
            {
                await image.CopyToAsync(file, ct);
            }

            Saved.Add(mimeType);
            _current = new PrimaryImage(path, File.GetLastWriteTimeUtc(path), IsLocalFile: true, Automatic: false);
        }

        public Task PersistAsync() => Task.CompletedTask;

        public Task ResetToAutomaticAsync() => throw new NotSupportedException();
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
