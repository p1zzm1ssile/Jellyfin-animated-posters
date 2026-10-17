using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.LibraryCoverMaker.Web;

/// <summary>
/// Answers Jellyfin's item image action itself when the image asked for is a library's Primary and that image is an
/// animated WebP the plugin applied, so Jellyfin's image processor never sees it: it cannot resize an image with more
/// than one frame, logs an error for every size a client asks for, and then sends the original file anyway. This
/// sends the same file, without the error.
/// </summary>
/// <remarks>
/// <para>
/// An action filter of MVC, added to every action and acting on one: <c>ImageController</c>'s item image action, in
/// each of its routes (<c>GetItemImage</c> for <c>/Items/{id}/Images/{type}</c>, <c>GetItemImageByIndex</c> for
/// <c>.../{type}/{index}</c>, <c>GetItemImage2</c> for the legacy route with tag and format segments), GET and HEAD
/// alike. Being a filter, it runs behind all of Jellyfin's middleware (base URL, forwarded headers, the IP access
/// rules, CORS, HTTPS redirection, error handling) and after routing and authorization, exactly where Jellyfin's
/// own action would run. An action filter rather than a resource filter, because then Jellyfin's model binding has
/// already turned the route and query into the action's arguments, whichever route was taken: the item id in any
/// form, the image type, the index from the path or from <c>?imageIndex=</c>, the tag from the query or the path.
/// </para>
/// <para>
/// It serves only when every one of these holds, in this order, and otherwise lets the action run untouched: the
/// image type is Primary and the index is 0 or none; the item is a library (a <see cref="CollectionFolder"/>); its
/// Primary image is a file on this server; the library's state names that file as the one the plugin applied
/// (<c>AppliedPrimaryPath</c> and <c>AppliedPrimaryModified</c>, as <c>PrimaryImageWatcher</c> compares them); and
/// the file is an animated WebP. The file served is always the one Jellyfin's item names right now, never a path
/// from the request or a remembered one, so a cover replaced by a pin, an upload, a restore or a switch of the Primary
/// format is never served after the item moved on. Whatever goes wrong while deciding leaves the request to the action.
/// </para>
/// <para>
/// The home screen asks for many images at once, so nothing here reads the state or the image on every request.
/// Per library, it remembers what the state said together with the state file's modification time and length, and
/// what the image's first bytes said together with the image's path, modification time and length. A library request
/// costs a lookup of the item and two file stats; the state is read again only when its file changed, which every
/// save does (the store writes a new file and moves it over the old one), and the image's header only when the image
/// changed. The item lookup is the one Jellyfin's action makes next for the same id, from the same in-memory cache,
/// so for any item that exists it only moves Jellyfin's own read earlier.
/// </para>
/// <para>
/// The answer is a <see cref="PhysicalFileResult"/>, so ASP.NET sends the file (with the server's send-file path) and
/// answers 304, HEAD and Range itself. The headers are Jellyfin's own for an image it cannot resize, as 12.2 sends
/// them: <c>Cache-Control</c> of <c>public, max-age=31536000, immutable</c> and the <c>tag</c> as the <c>ETag</c> when
/// the request has a tag, <c>public</c> otherwise, and <c>Last-Modified</c>. An untagged answer, and one whose tag is
/// not printable ASCII, gets an <c>ETag</c> made from the file's path, time and length instead.
/// </para>
/// </remarks>
public sealed class CoverImageFilter : IAsyncActionFilter
{
    /// <summary>The controller whose action this filter answers.</summary>
    internal const string ImageControllerType = "Jellyfin.Api.Controllers.ImageController";

    // The item image action's methods (the HEAD routes share them; only their route names differ).
    private static readonly FrozenSet<string> ItemImageActions =
        FrozenSet.ToFrozenSet(["GetItemImage", "GetItemImageByIndex", "GetItemImage2"], StringComparer.Ordinal);

    // Jellyfin keeps the image's modification time in its database; a round trip through it must not make the
    // plugin's own image look changed. The same tolerance as PrimaryImageWatcher's.
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(1);

    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const string PublicCache = "public";
    private const string WebpType = "image/webp";

    // RIFF <size> WEBP VP8X <size> <flags>: the animation flag is bit 1 of the first byte after the chunk size.
    private const int WebpHeaderLength = 21;
    private const byte AnimationFlag = 0x02;

    private readonly ILibraryManager _libraries;
    private readonly CoverStore _store;
    private readonly ILogger<CoverImageFilter> _log;
    private readonly ConcurrentDictionary<Guid, StateSeen> _states = new();
    private readonly ConcurrentDictionary<Guid, ImageSeen> _images = new();

    public CoverImageFilter(ILibraryManager libraries, CoverStore store, ILogger<CoverImageFilter> log)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _libraries = libraries;
        _store = store;
        _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (PrimaryRequestItem(context) is { } itemId && Answer(context, itemId) is { } result)
        {
            context.Result = result;
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>Whether a file that starts with <paramref name="header"/> is an animated WebP.</summary>
    internal static bool IsAnimatedWebp(ReadOnlySpan<byte> header) =>
        header.Length >= WebpHeaderLength
        && header[..4].SequenceEqual("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8)
        && header.Slice(12, 4).SequenceEqual("VP8X"u8)
        && (header[20] & AnimationFlag) != 0;

    /// <summary>
    /// The tag as a quoted entity tag, or null when it cannot be one that a response header carries: only printable
    /// ASCII without spaces and quotes (RFC 9110 etagc, without the obsolete bytes above 0x7F). Jellyfin's tags are
    /// hex digits.
    /// </summary>
    internal static string? QuotedTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        foreach (var c in tag)
        {
            if (c is < '!' or > '~' or '"')
            {
                return null;
            }
        }

        return "\"" + tag + "\"";
    }

    /// <summary>
    /// Where the store keeps a library's state: <c>state.json</c> in the library's folder, the folder above its
    /// <c>covers</c> (the layout <see cref="CoverStore"/> documents). A test pins it.
    /// </summary>
    internal static string StatePathOf(CoverStore store, Guid libraryId)
    {
        var covers = Path.GetDirectoryName(store.AnimatedPath(libraryId, Guid.Empty))!;
        return Path.Combine(Path.GetDirectoryName(covers)!, "state.json");
    }

    // The item asked for when this is the item image action for a Primary image at index 0 or none, otherwise null.
    // Only the arguments model binding gave the action are read, so every route of the action is handled alike.
    private static Guid? PrimaryRequestItem(ActionExecutingContext context)
    {
        var arguments = context.ActionArguments;
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || !ItemImageActions.Contains(action.ActionName)
            || !string.Equals(action.ControllerTypeInfo.FullName, ImageControllerType, StringComparison.Ordinal)
            || !arguments.TryGetValue("imageType", out var type) || type is not ImageType.Primary
            || (arguments.TryGetValue("imageIndex", out var index) && index is not (null or 0))
            || !arguments.TryGetValue("itemId", out var id) || id is not Guid itemId || itemId == Guid.Empty)
        {
            return null;
        }

        return itemId;
    }

    // The file result for the library's animated cover, or null to let Jellyfin's action answer. Never throws.
    private PhysicalFileResult? Answer(ActionExecutingContext context, Guid libraryId)
    {
        try
        {
            if (Find(libraryId) is not { } cover)
            {
                return null;
            }

            var tag = context.ActionArguments.TryGetValue("tag", out var value) ? value as string : null;
            var etag = (tag is null ? null : QuotedTag(tag)) ?? cover.ETag;
            var result = new PhysicalFileResult(cover.Path, WebpType)
            {
                LastModified = new DateTimeOffset(cover.Stamp.Ticks, TimeSpan.Zero),
                EntityTag = new EntityTagHeaderValue(etag),
                EnableRangeProcessing = true,
            };

            // Last, so that nothing is left on the response when Jellyfin's action answers instead.
            var cache = string.IsNullOrEmpty(tag) ? PublicCache : ImmutableCache;
            context.HttpContext.Response.Headers.CacheControl = cache;
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Library Cover Maker: could not tell whether the Primary image of library {LibraryId} is an animated "
                + "cover, so Jellyfin answers the request",
                libraryId);
            return null;
        }
    }

    // The library's Primary image when it is the plugin's animated cover, or null.
    private Cover? Find(Guid libraryId)
    {
        if (_libraries.GetItemById(libraryId) is not CollectionFolder library
            || library.GetImageInfo(ImageType.Primary, 0) is not { } image
            || string.IsNullOrEmpty(image.Path)
            || !image.IsLocalFile)
        {
            return null;
        }

        var state = StateOf(libraryId);
        if (!string.Equals(state.AppliedPath, image.Path, StringComparison.Ordinal)
            || state.AppliedModified is not { } applied
            || (image.DateModified - applied).Duration() > SameTime)
        {
            return null;
        }

        var file = new FileInfo(image.Path);
        if (!file.Exists)
        {
            return null;
        }

        var stamp = new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length);
        if (!_images.TryGetValue(libraryId, out var seen)
            || !string.Equals(seen.Path, image.Path, StringComparison.Ordinal)
            || seen.Stamp != stamp)
        {
            seen = Look(image.Path, stamp);
            _images[libraryId] = seen;
        }

        return seen.Animated ? new Cover(seen.Path, seen.Stamp, seen.ETag) : null;
    }

    // What the library's state says the plugin applied, read again only when the state file changed. The stamp is
    // taken before the read: a save that lands in between leaves an older stamp with the newer state, and the next
    // request reads it once more, never the other way round.
    private StateSeen StateOf(Guid libraryId)
    {
        var file = new FileInfo(StatePathOf(_store, libraryId));
        var stamp = file.Exists ? new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length) : FileStamp.Missing;
        if (_states.TryGetValue(libraryId, out var known) && known.Stamp == stamp)
        {
            return known;
        }

        var state = _store.Load(libraryId);
        var seen = new StateSeen(stamp, state.AppliedPrimaryPath, state.AppliedPrimaryModified);
        _states[libraryId] = seen;
        return seen;
    }

    private static ImageSeen Look(string path, FileStamp stamp)
    {
        Span<byte> header = stackalloc byte[WebpHeaderLength];
        int read;
        var share = FileShare.ReadWrite | FileShare.Delete;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{path}\n{stamp.Ticks}\n{stamp.Length}")));
        var etag = "\"" + Convert.ToHexStringLower(hash.AsSpan(0, 16)) + "\"";
        return new ImageSeen(path, stamp, IsAnimatedWebp(header[..read]), etag);
    }

    private readonly record struct FileStamp(long Ticks, long Length)
    {
        public static FileStamp Missing { get; } = new(-1, -1);
    }

    private sealed record StateSeen(FileStamp Stamp, string? AppliedPath, DateTime? AppliedModified);

    private sealed record ImageSeen(string Path, FileStamp Stamp, bool Animated, string ETag);

    private sealed record Cover(string Path, FileStamp Stamp, string ETag);
}
