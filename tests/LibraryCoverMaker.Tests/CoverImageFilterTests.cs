using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker;
using Jellyfin.Plugin.LibraryCoverMaker.Web;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LibraryCoverMaker.Tests;

/// <summary>
/// The MVC filter that answers Jellyfin's item image action itself for a library's animated Primary, so Jellyfin's
/// image processor never tries to resize it. Each request runs through the filter with the arguments Jellyfin's model
/// binding would give the action and a stub library manager; the file result it sets is then executed by ASP.NET's
/// own executor, which is what answers 304, HEAD and Range. Whether MVC really runs the filter in front
/// of Jellyfin's action, behind all of Jellyfin's middleware, is checked on a server.
/// </summary>
public sealed class CoverImageFilterTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("6a1f3c2e-8b4d-4e5f-9a0b-1c2d3e4f5a6b");
    private static readonly Guid Film = Guid.Parse("1b2c3d4e-5f60-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly DateTime Written = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    // How long the filter waits for a Primary that Jellyfin is writing: short, so that giving up is quick to test, but
    // far longer than the tests' own pauses, which a busy host can stretch to several times their length.
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(2);

    private readonly string _root = Directory.CreateTempSubdirectory("cover-filter-tests-").FullName;
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly CoverImageFilter _filter;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddMvcCore().Services
        .BuildServiceProvider();

    private int _passedThrough;
    private bool _lookupFails;
    private bool _replacing;
    private readonly HashSet<Guid> _visible = new();
    private readonly CountingLogger _log = new();

    public CoverImageFilterTests()
    {
        _filter = new CoverImageFilter(Libraries(), _log, BusyWait, _ => _replacing);
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
        var path = PrimaryCover(bytes);

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
        PrimaryCover(bytes);

        var response = await Send(new Call { Action = action, Index = index, Tag = tag });

        Assert.Equal(0, _passedThrough);
        Assert.Equal(bytes, Body(response));
    }

    [Fact]
    public async Task Tagged_request_is_immutable_with_the_tag_as_etag()
    {
        PrimaryCover(AnimatedWebp(3));

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
        PrimaryCover(AnimatedWebp(4));
        var untagged = await Send(new Call());

        var response = await Send(new Call { Tag = tag });

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl.ToString());
        Assert.Equal(untagged.Headers.ETag.ToString(), response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Untagged_request_matches_jellyfin_cache_header()
    {
        PrimaryCover(AnimatedWebp(5));

        var response = await Send(new Call());

        Assert.Equal("public", response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Head_request_has_headers_and_no_body()
    {
        var bytes = AnimatedWebp(6);
        PrimaryCover(bytes);

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
        var path = PrimaryCover(AnimatedWebp(7));
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
        PrimaryCover(bytes);

        var response = await Send(new Call { Range = "bytes=0-9" });

        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal(bytes.Take(10).ToArray(), Body(response));
    }

    [Fact]
    public async Task File_replaced_on_disk_is_reread()
    {
        var path = PrimaryCover(AnimatedWebp(9));
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
    public async Task New_primary_is_served_at_once_after_an_apply()
    {
        var old = PrimaryCover(AnimatedWebp(12));
        await Send(new Call());

        // A new cover at another path, before the plugin saved any state that names it (the apply race): the item
        // says what the library shows, and the old file is never served again.
        var replacement = AnimatedWebp(13, extra: 7);
        var path = WriteCover("folder2.webp", replacement, Written.AddHours(1));
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));
        var response = await Send(new Call());

        Assert.Equal(0, _passedThrough);
        Assert.True(File.Exists(old));
        Assert.Equal(replacement, Body(response));
    }

    [Fact]
    public async Task Animated_primary_from_an_upload_is_served_too()
    {
        // Whatever wrote it (Edit images, or a file put in the library folder): an animated WebP is never Jellyfin's
        // to resize. The item's image time need not match the file's either.
        var bytes = AnimatedWebp(11);
        var path = WriteCover("folder.webp", bytes);
        _items[Library] = LibraryWith(path, Written.AddMinutes(-30));

        var response = await Send(new Call());

        Assert.Equal(0, _passedThrough);
        Assert.Equal(bytes, Body(response));
    }

    [Fact]
    public async Task Primary_being_written_is_served_once_jellyfin_is_done()
    {
        var path = PrimaryCover(AnimatedWebp(20));
        await Send(new Call());

        // Jellyfin's image saver writes the file in place with FileShare.None, which on Linux is an exclusive lock.
        var replacement = AnimatedWebp(21, extra: 64);
        Task<HttpResponse> request;
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writer.Write(replacement, 0, 10);
            request = Send(new Call());
            await Task.Delay(150);
            Assert.False(request.IsCompleted);
            writer.Write(replacement, 10, replacement.Length - 10);
        }

        var response = await request;

        Assert.Equal(0, _passedThrough);
        Assert.Equal(replacement, Body(response));
        Assert.Equal(replacement.Length, response.ContentLength);
    }

    [Fact]
    public async Task Primary_moved_aside_by_the_applier_is_waited_for()
    {
        // The plugin renamed its cover out of the way of SaveImage, and Jellyfin has not created the new file yet.
        // Once the file is there the filter still waits: .NET creates a file before it takes the writer's lock, so
        // a file that exists may not be written yet. Only the end of the replacement says it is.
        var path = PrimaryCover(AnimatedWebp(24));
        File.Delete(path);
        _replacing = true;

        var request = Send(new Call());
        await Task.Delay(150);
        Assert.False(request.IsCompleted);
        var replacement = AnimatedWebp(25, extra: 30);
        File.WriteAllBytes(path, replacement);
        await Task.Delay(150);
        Assert.False(request.IsCompleted);
        _replacing = false;
        var response = await request;

        Assert.Equal(0, _passedThrough);
        Assert.Equal(replacement, Body(response));
    }

    [Fact]
    public async Task Primary_written_for_too_long_is_left_to_jellyfin_and_said_once()
    {
        var path = PrimaryCover(AnimatedWebp(22));

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await Send(new Call());

            // The same file, unchanged: no second wait and no second warning.
            var again = Stopwatch.StartNew();
            await Send(new Call());
            Assert.True(again.Elapsed < BusyWait / 2, $"the second request waited {again.Elapsed}");
        }

        Assert.Equal(2, _passedThrough);
        Assert.Equal(1, _log.Warnings);

        // Once the file changes, it is looked at again.
        File.WriteAllBytes(path, AnimatedWebp(23, extra: 3));
        File.SetLastWriteTimeUtc(path, Written.AddMinutes(1));
        await Send(new Call());
        Assert.Equal(2, _passedThrough);
    }

    [Fact]
    public async Task Served_file_cannot_change_under_the_answer()
    {
        // The file the filter decided on is the one it sends: its handle is open until the answer is written, so a
        // new file moved over the path afterwards is not mixed into it.
        var bytes = AnimatedWebp(23);
        var path = PrimaryCover(bytes);
        var http = NewRequest(new Call());
        var executing = Executing(http, new Call());
        await _filter.OnActionExecutionAsync(executing, PassThrough(executing));

        var moved = Path.Combine(_root, "jellyfin", "new.webp");
        File.WriteAllBytes(moved, StillWebp());
        File.Move(moved, path, overwrite: true);
        await executing.Result!.ExecuteResultAsync(executing);

        Assert.Equal(bytes, Body(http.Response));
    }

    // ---- Passed through ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Still_primary_passes_through()
    {
        PrimaryCover(StillWebp());
        await Send(new Call());

        PrimaryCover([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'], "folder.jpg");
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
        PrimaryCover(AnimatedWebp(15));

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
        PrimaryCover(AnimatedWebp(16));

        await Send(new Call { Controller = controller, Action = action });

        Assert.Equal(1, _passedThrough);
    }

    [Fact]
    public async Task Signed_in_user_gets_only_the_libraries_they_may_see()
    {
        // Jellyfin's action answers 404 for a library the user may not see; the filter must not serve it instead.
        var bytes = AnimatedWebp(26);
        PrimaryCover(bytes);
        var allowed = Guid.NewGuid();
        _visible.Add(allowed);

        var seen = await Send(new Call { User = allowed.ToString("N") });
        await Send(new Call { User = Guid.NewGuid().ToString("N") });
        await Send(new Call { User = "not a user id" });
        await Send(new Call { User = string.Empty }); // authenticated, without the claim
        var anonymous = await Send(new Call());

        // An API key authenticates with an empty user id, and Jellyfin answers it for any library, as anonymously.
        var apiKey = await Send(new Call { User = Guid.Empty.ToString() });

        Assert.Equal(bytes, Body(seen));
        Assert.Equal(3, _passedThrough);
        Assert.Equal(bytes, Body(anonymous));
        Assert.Equal(bytes, Body(apiKey));
    }

    [Fact]
    public async Task Missing_file_and_remote_image_pass_through()
    {
        var path = PrimaryCover(AnimatedWebp(18));
        File.Delete(path);
        await Send(new Call());

        _items[Library] = LibraryWith("https://example.invalid/cover.webp", Written);
        await Send(new Call());

        Assert.Equal(2, _passedThrough);
    }

    [Fact]
    public async Task Failure_in_the_plugin_path_leaves_the_request_to_jellyfin()
    {
        PrimaryCover(AnimatedWebp(19));
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

    // Writes a cover file and makes it the library's Primary in Jellyfin's item.
    private string PrimaryCover(byte[] bytes, string name = "folder.webp")
    {
        var path = WriteCover(name, bytes);
        _items[Library] = LibraryWith(path, File.GetLastWriteTimeUtc(path));
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

    private static CollectionFolder LibraryWith(string path, DateTime modified) => new()
    {
        Id = Library,
        ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = path, DateModified = modified }],
    };

    // Runs the filter as MVC would before Jellyfin's action, then the result it set, as MVC would after it. The
    // action itself is the counter: it runs only when the filter calls next.
    private async Task<HttpResponse> Send(Call call)
    {
        var http = NewRequest(call);
        var executing = Executing(http, call);
        await _filter.OnActionExecutionAsync(executing, PassThrough(executing));

        if (executing.Result is { } result)
        {
            Assert.IsType<FileStreamResult>(result);
            await result.ExecuteResultAsync(executing);
        }

        return http.Response;
    }

    private DefaultHttpContext NewRequest(Call call)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        if (call.User is { } user)
        {
            Claim[] claims = user.Length == 0 ? [] : [new Claim("Jellyfin-UserId", user)];
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "CustomAuth"));
        }

        http.Request.Method = call.Method;
        http.Response.Body = new MemoryStream();
        foreach (var (name, value) in call.Headers())
        {
            http.Request.Headers[name] = value;
        }

        return http;
    }

    // The context MVC gives an action filter, with the arguments model binding would give the action.
    private static ActionExecutingContext Executing(HttpContext http, Call call)
    {
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

        return new ActionExecutingContext(action, [], arguments, new object());
    }

    // Jellyfin's action, as the filter's next: it only counts that it ran.
    private ActionExecutionDelegate PassThrough(ActionExecutingContext executing) => () =>
    {
        _passedThrough++;
        return Task.FromResult(new ActionExecutedContext(executing, executing.Filters, executing.Controller));
    };

    private static byte[] Body(HttpResponse response) => ((MemoryStream)response.Body).ToArray();

    // A library manager that knows only the test's items, or that fails when the test says so.
    // With a user (GetItemById<T>(id, userId)), only the items that user may see, as Jellyfin's lookup gives them.
    private ILibraryManager Libraries() => Proxy<ILibraryManager>((call, args) =>
    {
        if (call.Name != nameof(ILibraryManager.GetItemById)
            || (call.IsGenericMethod && (args!.Length != 2 || args[1] is not Guid)))
        {
            return null;
        }

        if (_lookupFails)
        {
            throw new InvalidOperationException("The library is not loaded yet.");
        }

        var item = _items.GetValueOrDefault((Guid)args![0]!);
        return call.IsGenericMethod && !_visible.Contains((Guid)args[1]!) ? null : item;
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

        /// <summary>The user id claim of an authenticated request: null when anonymous, empty for no claim.</summary>
        public string? User { get; init; }

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

    // Counts what is logged at warning level or above.
    private sealed class CountingLogger : ILogger<CoverImageFilter>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings++;
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
}
