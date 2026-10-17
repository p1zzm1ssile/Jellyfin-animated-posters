using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using Jellyfin.Plugin.LibraryCoverMaker.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The MVC filter that answers Jellyfin's item image action itself for a library's animated Primary, so Jellyfin's
/// image processor never tries to resize it. Each request runs through the filter with the arguments Jellyfin's model
/// binding would give the action, a real store and a stub library manager; the file result it sets is then executed
/// by ASP.NET's own executor, which is what answers 304, HEAD and Range. Whether MVC really runs the filter in front
/// of Jellyfin's action, behind all of Jellyfin's middleware, is checked on a server.
/// </summary>
public sealed class CoverImageFilterTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("6a1f3c2e-8b4d-4e5f-9a0b-1c2d3e4f5a6b");
    private static readonly Guid Film = Guid.Parse("1b2c3d4e-5f60-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly DateTime Written = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("cover-filter-tests-").FullName;
    private readonly CoverStore _store;
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly CoverImageFilter _filter;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddMvcCore().Services
        .BuildServiceProvider();

    private int _passedThrough;
    private bool _lookupFails;

    public CoverImageFilterTests()
    {
        _store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
        _filter = new CoverImageFilter(Libraries(), _store, NullLogger<CoverImageFilter>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // ---- Served ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Animated_library_primary_is_served_by_the_plugin()
    {
        var bytes = AnimatedWebp(1);
        var path = AppliedCover(bytes);

        var response = await Send(new Call());

        Assert.Equal(0, _passedThrough);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(bytes, Body(response));
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal(bytes.Length, response.ContentLength);
        Assert.Equal(File.GetLastWriteTimeUtc(path).ToString("R"), response.Headers.LastModified.ToString());
        Assert.Matches("^\"[0-9a-f]{32}\"$", response.Headers.ETag.ToString());
    }

    [Theory]
    [InlineData("GetItemImage", null, null)] // /Items/{id}/Images/Primary, also with a trailing slash
    [InlineData("GetItemImage", 0, null)] // ?imageIndex=0
    [InlineData("GetItemImageByIndex", 0, null)] // /Items/{id}/Images/Primary/0
    [InlineData("GetItemImage2", 0, "abc")] // /Items/{id}/Images/Primary/0/abc/Webp/300/300/0/0
    public async Task Every_route_of_the_item_image_action_is_served(string action, int? index, string? tag)
    {
        var bytes = AnimatedWebp(2);
        AppliedCover(bytes);

        var response = await Send(new Call { Action = action, Index = index, Tag = tag });

        Assert.Equal(0, _passedThrough);
        Assert.Equal(bytes, Body(response));
    }

    [Fact]
    public async Task Tagged_request_is_immutable_with_the_tag_as_etag()
    {
        AppliedCover(AnimatedWebp(3));

        var response = await Send(new Call { Tag = "abc123" });

        // As Jellyfin 12.2 answers: the tag is the ETag.
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl.ToString());
        Assert.Equal("\"abc123\"", response.Headers.ETag.ToString());
    }

    [Theory]
    [InlineData("\u00e9")]
    [InlineData("\u0001")]
    [InlineData("a\r\nX-Evil: 1")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    public async Task Tag_that_is_not_printable_ascii_gets_the_file_etag(string tag)
    {
        AppliedCover(AnimatedWebp(4));
        var untagged = await Send(new Call());

        var response = await Send(new Call { Tag = tag });

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl.ToString());
        Assert.Equal(untagged.Headers.ETag.ToString(), response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Untagged_request_matches_jellyfin_cache_header()
    {
        AppliedCover(AnimatedWebp(5));

        var response = await Send(new Call());

        Assert.Equal("public", response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Head_request_has_headers_and_no_body()
    {
        var bytes = AnimatedWebp(6);
        AppliedCover(bytes);

        var response = await Send(new Call { Method = "HEAD", Tag = "x" });

        Assert.Equal(0, _passedThrough);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal(bytes.Length, response.ContentLength);
        Assert.Equal("\"x\"", response.Headers.ETag.ToString());
        Assert.Empty(Body(response));
    }

    [Fact]
    public async Task Conditional_request_returns_304()
    {
        var path = AppliedCover(AnimatedWebp(7));
        var untagged = (await Send(new Call())).Headers.ETag.ToString();

        var byTag = await Send(new Call { Tag = "t", IfNoneMatch = "\"t\"" });
        var byList = await Send(new Call { IfNoneMatch = "\"x\", " + untagged });
        var byDate = await Send(new Call { IfModifiedSince = File.GetLastWriteTimeUtc(path).ToString("R") });
        var otherTag = await Send(new Call { Tag = "t", IfNoneMatch = "\"other\"" });
        var older = await Send(new Call { IfModifiedSince = Written.AddDays(-1).ToString("R") });

        Assert.Equal(0, _passedThrough);
        Assert.Equal(StatusCodes.Status304NotModified, byTag.StatusCode);
        Assert.Empty(Body(byTag));
        Assert.Equal("public, max-age=31536000, immutable", byTag.Headers.CacheControl.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, byList.StatusCode);
        Assert.Equal(StatusCodes.Status304NotModified, byDate.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, otherTag.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, older.StatusCode);
    }

    [Fact]
    public async Task Range_request_gets_part_of_the_file()
    {
        var bytes = AnimatedWebp(8);
        AppliedCover(bytes);

        var response = await Send(new Call { Range = "bytes=0-9" });

        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal(bytes.Take(10).ToArray(), Body(response));
    }

    [Fact]
    public async Task File_replaced_on_disk_is_reread()
    {
        var path = AppliedCover(AnimatedWebp(9));
        var first = await Send(new Call());

        var replaced = AnimatedWebp(10, extra: 40);
        File.WriteAllBytes(path, replaced);
        File.SetLastWriteTimeUtc(path, Written.AddMinutes(5));
        var second = await Send(new Call());

        Assert.Equal(replaced, Body(second));
        Assert.Equal(replaced.Length, second.ContentLength);
        Assert.NotEqual(first.Headers.ETag.ToString(), second.Headers.ETag.ToString());

        // Replaced by a still image: from then on it is Jellyfin's to resize.
        File.WriteAllBytes(path, StillWebp());
        File.SetLastWriteTimeUtc(path, Written.AddMinutes(10));
        await Send(new Call());
        Assert.Equal(1, _passedThrough);
    }

    [Fact]
    public async Task State_saved_after_the_first_request_is_seen()
    {
        // The apply race: Jellyfin's item already has the new image while the state that names it is not saved yet.
        var path = WriteCover("folder.webp", AnimatedWebp(11));
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));

        await Send(new Call());
        Assert.Equal(1, _passedThrough);

        Record(path);
        var response = await Send(new Call());

        Assert.Equal(1, _passedThrough);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
    }

    [Fact]
    public async Task New_primary_is_served_at_once_after_an_apply()
    {
        var old = AppliedCover(AnimatedWebp(12));
        await Send(new Call());

        // A new cover at another path, recorded by the apply: the old file is never served again.
        var replacement = AnimatedWebp(13, extra: 7);
        var path = WriteCover("folder2.webp", replacement, Written.AddHours(1));
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));
        Record(path);
        var response = await Send(new Call());

        Assert.True(File.Exists(old));
        Assert.Equal(replacement, Body(response));
    }

    // ---- Passed through ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Still_primary_passes_through()
    {
        AppliedCover(StillWebp());
        await Send(new Call());

        AppliedCover([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'], "folder.jpg");
        await Send(new Call());

        Assert.Equal(2, _passedThrough);
    }

    [Fact]
    public async Task Non_library_item_passes_through()
    {
        var path = WriteCover("poster.webp", AnimatedWebp(14));
        _items[Film] = new Movie
        {
            Id = Film,
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path, DateModified = Written }],
        };

        // Even a state that names the file does not make a film a library.
        _store.Update(Film, state =>
        {
            state.AppliedPrimaryPath = path;
            state.AppliedPrimaryModified = Written;
        });
        await Send(new Call { ItemId = Film });
        await Send(new Call { ItemId = Guid.NewGuid() });

        Assert.Equal(2, _passedThrough);
    }

    [Theory]
    [InlineData(ImageType.Backdrop, null)]
    [InlineData(ImageType.Logo, null)]
    [InlineData(ImageType.Primary, 1)]
    public async Task Other_image_types_and_indexes_pass_through(ImageType type, int? index)
    {
        AppliedCover(AnimatedWebp(15));

        await Send(new Call { Type = type, Index = index });
        await Send(new Call { Type = type, Index = index ?? 0, Action = "GetItemImageByIndex" });

        Assert.Equal(2, _passedThrough);
    }

    [Theory]
    [InlineData(CoverImageFilter.ImageControllerType, "DeleteItemImage")]
    [InlineData(CoverImageFilter.ImageControllerType, "SetItemImage")]
    [InlineData(CoverImageFilter.ImageControllerType, "GetItemImageInfos")]
    [InlineData("Jellyfin.Plugin.Other.ImageController", "GetItemImage")]
    public async Task Other_actions_pass_through(string controller, string action)
    {
        AppliedCover(AnimatedWebp(16));

        await Send(new Call { Controller = controller, Action = action });

        Assert.Equal(1, _passedThrough);
    }

    [Fact]
    public async Task Library_whose_state_names_another_image_passes_through()
    {
        // An animated image the plugin did not apply, or one changed since: the state says otherwise.
        var path = WriteCover("folder.webp", AnimatedWebp(17));
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));
        await Send(new Call()); // no state at all

        _store.Update(Library, state =>
        {
            state.AppliedPrimaryPath = path + ".old";
            state.AppliedPrimaryModified = Written;
        });
        await Send(new Call());

        _store.Update(Library, state =>
        {
            state.AppliedPrimaryPath = path;
            state.AppliedPrimaryModified = Written.AddMinutes(-30);
        });
        await Send(new Call());

        Assert.Equal(3, _passedThrough);
    }

    [Fact]
    public async Task Missing_file_and_remote_image_pass_through()
    {
        var path = AppliedCover(AnimatedWebp(18));
        File.Delete(path);
        await Send(new Call());

        _items[Library] = LibraryWith("https://example.invalid/cover.webp", Written);
        Record("https://example.invalid/cover.webp");
        await Send(new Call());

        Assert.Equal(2, _passedThrough);
    }

    [Fact]
    public async Task Failure_in_the_plugin_path_leaves_the_request_to_jellyfin()
    {
        AppliedCover(AnimatedWebp(19));
        _lookupFails = true;

        await Send(new Call());

        Assert.Equal(1, _passedThrough);
    }

    // ---- Parts -------------------------------------------------------------------------------------------------

    [Fact]
    public void Webp_header_tells_animations_apart()
    {
        Assert.True(CoverImageFilter.IsAnimatedWebp(AnimatedWebp(0)));
        Assert.False(CoverImageFilter.IsAnimatedWebp(StillWebp()));
        Assert.False(CoverImageFilter.IsAnimatedWebp("RIFF\0\0\0\0WEBPVP8 \0\0\0\0\0"u8));
        Assert.False(CoverImageFilter.IsAnimatedWebp(AnimatedWebp(0).AsSpan(0, 20)));
        Assert.False(CoverImageFilter.IsAnimatedWebp([0x47, 0x49, 0x46, 0x38, 0x39, 0x61]));
    }

    [Fact]
    public void Only_printable_ascii_tags_become_etags()
    {
        Assert.Equal("\"74813c4529ff5a08\"", CoverImageFilter.QuotedTag("74813c4529ff5a08"));
        Assert.Equal("\"a!#~\"", CoverImageFilter.QuotedTag("a!#~"));
        Assert.Null(CoverImageFilter.QuotedTag(string.Empty));
        Assert.Null(CoverImageFilter.QuotedTag("a b"));
        Assert.Null(CoverImageFilter.QuotedTag("a\"b"));
        Assert.Null(CoverImageFilter.QuotedTag("\u00e9"));
        Assert.Null(CoverImageFilter.QuotedTag("\u007f"));
    }

    [Fact]
    public void State_path_is_where_the_store_writes_the_state()
    {
        _store.Update(Library, state => state.Pinned = true);

        Assert.True(File.Exists(_store.StatePath(Library)));
    }

    [Fact]
    public void Registrator_adds_the_filter_to_mvc_and_no_startup_filter()
    {
        var services = new ServiceCollection();

        PluginServiceRegistrator.AddServices(services);

        // The host has the options services; the plugin adds only its setup of MvcOptions.
        var host = new ServiceCollection().AddOptions();
        foreach (var service in services)
        {
            host.Add(service);
        }

        using var provider = host.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        // One instance for every request, so its memo is shared.
        Assert.Contains(
            services,
            service => service.ServiceType == typeof(CoverImageFilter)
                && service.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            filters,
            filter => filter is ServiceFilterAttribute { ServiceType: var type } && type == typeof(CoverImageFilter));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IStartupFilter));
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    // RIFF, WEBP, then a VP8X chunk whose flags have the animation bit, then filler standing in for the frames.
    private static byte[] AnimatedWebp(byte seed, int extra = 16) => Webp(0x02, seed, extra);

    private static byte[] StillWebp() => Webp(0x00, 0, 16);

    private static byte[] Webp(byte flags, byte seed, int extra)
    {
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(4 + 18 + extra));
        bytes.AddRange("WEBPVP8X"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(10));
        bytes.AddRange([flags, 0, 0, 0, 0x7F, 0x02, 0, 0x67, 0x01, 0]);
        bytes.AddRange(Enumerable.Range(0, extra).Select(i => (byte)(seed + i)));
        return bytes.ToArray();
    }

    // Writes a cover file, makes it the library's Primary in Jellyfin's item, and records it as the plugin's apply.
    private string AppliedCover(byte[] bytes, string name = "folder.webp")
    {
        var path = WriteCover(name, bytes);
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));
        Record(path);
        return path;
    }

    private string WriteCover(string name, byte[] bytes, DateTime? at = null)
    {
        var path = Path.Combine(_root, "jellyfin", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, at ?? Written);
        return path;
    }

    private void Record(string path) => _store.Update(Library, state =>
    {
        state.AppliedPrimaryPath = path;
        state.AppliedPrimaryModified = _items[Library].GetImageInfo(ImageType.Primary, 0)!.DateModified;
    });

    private static CollectionFolder LibraryWith(string path, DateTime modified) => new()
    {
        Id = Library,
        ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path, DateModified = modified }],
    };

    // Runs the filter as MVC would before Jellyfin's action, then the result it set, as MVC would after it. The
    // action itself is the counter: it runs only when the filter calls next.
    private async Task<HttpResponse> Send(Call call)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        http.Request.Method = call.Method;
        http.Response.Body = new MemoryStream();
        foreach (var (name, value) in call.Headers())
        {
            http.Request.Headers[name] = value;
        }

        var descriptor = new ControllerActionDescriptor
        {
            ActionName = call.Action,
            ControllerName = "Image",
            ControllerTypeInfo = new NamedType(call.Controller),
        };
        var action = new ActionContext(http, new RouteData(), descriptor);
        var arguments = new Dictionary<string, object?>
        {
            ["itemId"] = call.ItemId,
            ["imageType"] = call.Type,
        };
        if (call.Index is { } index)
        {
            arguments["imageIndex"] = index;
        }

        if (call.Tag is { } tag)
        {
            arguments["tag"] = tag;
        }

        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var executing = new ActionExecutingContext(action, filters, arguments, controller);
        await _filter.OnActionExecutionAsync(executing, () =>
        {
            _passedThrough++;
            return Task.FromResult(new ActionExecutedContext(action, filters, controller));
        });

        if (executing.Result is { } result)
        {
            Assert.IsType<PhysicalFileResult>(result);
            await result.ExecuteResultAsync(action);
        }

        return http.Response;
    }

    private static byte[] Body(HttpResponse response) => ((MemoryStream)response.Body).ToArray();

    // A library manager that knows only the test's items, or that fails when the test says so.
    private ILibraryManager Libraries() => Proxy<ILibraryManager>((call, args) =>
    {
        if (call.Name != nameof(ILibraryManager.GetItemById) || call.IsGenericMethod)
        {
            return null;
        }

        return _lookupFails
            ? throw new InvalidOperationException("The library is not loaded yet.")
            : _items.GetValueOrDefault((Guid)args![0]!);
    });

    // A stand-in for an interface: each call gets what answer gives, or the default of its return type.
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> answer)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Answer = answer;
        return proxy;
    }

    /// <summary>One request to the item image action: its route values and query as model binding gives them.</summary>
    private sealed record Call
    {
        public Guid ItemId { get; init; } = Library;

        public ImageType Type { get; init; } = ImageType.Primary;

        public string Controller { get; init; } = CoverImageFilter.ImageControllerType;

        public string Action { get; init; } = "GetItemImage";

        public int? Index { get; init; }

        public string? Tag { get; init; }

        public string Method { get; init; } = "GET";

        public string? IfNoneMatch { get; init; }

        public string? IfModifiedSince { get; init; }

        public string? Range { get; init; }

        public IEnumerable<(string Name, string Value)> Headers()
        {
            if (IfNoneMatch is not null)
            {
                yield return ("If-None-Match", IfNoneMatch);
            }

            if (IfModifiedSince is not null)
            {
                yield return ("If-Modified-Since", IfModifiedSince);
            }

            if (Range is not null)
            {
                yield return ("Range", Range);
            }
        }
    }

    // The controller type as MVC describes it: only its full name matters to the filter.
    private sealed class NamedType(string fullName) : TypeDelegator(typeof(object))
    {
        public override string FullName => fullName;
    }

    public class FakeProxy : DispatchProxy
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
            return type == typeof(void) || !type.IsValueType || type.ContainsGenericParameters
                ? null
                : Activator.CreateInstance(type);
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
