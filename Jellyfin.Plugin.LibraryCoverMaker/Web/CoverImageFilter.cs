using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.LibraryCoverMaker.Web;

/// <summary>
/// Answers Jellyfin's item image action itself when the image asked for is a library's Primary and that image is an
/// animated WebP, so Jellyfin's image processor never sees it: it cannot resize an image with more than one frame,
/// logs an error for every size a client asks for, and then sends the original file anyway. This sends the same file,
/// without the error.
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
/// It serves only when every one of these holds, in this order, and otherwise lets the action run untouched: the image
/// type is Primary and the index is 0 or none; the item is a library (a <see cref="CollectionFolder"/>) that the
/// request's user may see (any library for an anonymous request or an API key, as Jellyfin's action allows); its
/// Primary image is a file on this server; and the file is an animated WebP, whatever wrote it (the plugin, an upload
/// in Edit images, a file put in the library's folder). The plugin's state is not asked: while a cover is applied,
/// Jellyfin's item names the new file before the state that records it is saved, and a request in between must not
/// reach the image processor either. The file served is always the one Jellyfin's item names right now, never a path
/// from the request or a remembered one, so a cover replaced by a pin, an upload, a restore or a switch of the Primary
/// format is never served after the item moved on. Whatever goes wrong while deciding leaves the request to the action.
/// </para>
/// <para>
/// Jellyfin's image saver writes a Primary (a library's <c>folder.webp</c>) with <c>FileShare.None</c>, which .NET on
/// Linux makes an exclusive <c>flock</c>: the filter cannot open the file while it is written, and neither can the
/// image processor. A request that comes then waits for the write to end (polling, up to <see cref="DefaultBusyWait"/>)
/// and is answered with the finished file. So does one that comes while the plugin has moved its own cover out of the
/// way for the save (<see cref="CoverApplier.IsReplacingPrimary"/>), whether the new file is there yet or not, until
/// the save returns. The file is opened once, and the handle that told it is an animated WebP is the one the answer
/// sends, so the bytes sent are always one whole file even when another write follows at once. A file that stays
/// unreadable past the wait is logged once and left to Jellyfin, without waiting again, until it changes.
/// </para>
/// <para>
/// The home screen asks for many images at once, so nothing here reads more than the image's handle and stat on each
/// request. Per image file, it remembers what the file's first bytes said together with its modification time and
/// length, and reads the header again only when the file changed. The item lookup is the one Jellyfin's action makes
/// next for the same id, from the same in-memory cache, so for any item that exists it only moves Jellyfin's own read
/// earlier.
/// </para>
/// <para>
/// The answer is a <see cref="FileStreamResult"/> over that handle, so ASP.NET sends the file and answers 304, HEAD
/// and Range itself, and closes the handle when the answer is written. The headers are Jellyfin's own for an image it
/// cannot resize, as 12.2 sends them: <c>Cache-Control</c> of <c>public, max-age=31536000, immutable</c> and the
/// <c>tag</c> as the <c>ETag</c> when the request has a tag, <c>public</c> otherwise, and <c>Last-Modified</c>. An
/// untagged answer, and one whose tag is not printable ASCII, gets an <c>ETag</c> made from the file's path, time and
/// length instead.
/// </para>
/// </remarks>
public sealed class CoverImageFilter : IAsyncActionFilter
{
    /// <summary>The controller whose action this filter answers.</summary>
    internal const string ImageControllerType = "Jellyfin.Api.Controllers.ImageController";

    /// <summary>
    /// How long a request waits for Jellyfin to finish writing the Primary it asks for. Jellyfin writes a cover of at
    /// most a few megabytes from a local file, which takes milliseconds; past this, something else holds the file.
    /// </summary>
    internal static readonly TimeSpan DefaultBusyWait = TimeSpan.FromSeconds(2);

    // How often a request looks again at a Primary that is being written.
    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(20);

    // The item image action's methods (the HEAD routes share them; only their route names differ).
    private static readonly FrozenSet<string> ItemImageActions =
        FrozenSet.ToFrozenSet(["GetItemImage", "GetItemImageByIndex", "GetItemImage2"], StringComparer.Ordinal);

    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const string PublicCache = "public";
    private const string WebpType = "image/webp";

    // The claim Jellyfin's authentication handler gives the user id in (Jellyfin.Api's InternalClaimTypes.UserId).
    private const string UserIdClaim = "Jellyfin-UserId";

    // RIFF <size> WEBP VP8X <size> <flags>: the animation flag is bit 1 of the first byte after the chunk size.
    private const int WebpHeaderLength = 21;
    private const byte AnimationFlag = 0x02;

    private readonly ILibraryManager _libraries;
    private readonly ILogger<CoverImageFilter> _log;
    private readonly TimeSpan _busyWait;
    private readonly Func<Guid, bool> _replacing;
    private readonly ConcurrentDictionary<string, ImageSeen> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileStamp> _held = new(StringComparer.Ordinal);

    public CoverImageFilter(ILibraryManager libraries, CoverApplier applier, ILogger<CoverImageFilter> log)
        : this(libraries, log, DefaultBusyWait, (applier ?? throw new ArgumentNullException(nameof(applier)))
            .IsReplacingPrimary)
    {
    }

    // For the tests: a shorter wait for a Primary that is being written, and the applier's side replaced.
    internal CoverImageFilter(
        ILibraryManager libraries,
        ILogger<CoverImageFilter> log,
        TimeSpan busyWait,
        Func<Guid, bool> replacing)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(replacing);

        _libraries = libraries;
        _log = log;
        _busyWait = busyWait;
        _replacing = replacing;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (PrimaryRequestItem(context) is { } itemId
            && await AnswerAsync(context, itemId).ConfigureAwait(false) is { } result)
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

    // The file result for the library's animated Primary, or null to let Jellyfin's action answer. Never throws.
    private async Task<FileStreamResult?> AnswerAsync(ActionExecutingContext context, Guid libraryId)
    {
        var http = context.HttpContext;
        try
        {
            if (!TryRequestUser(http, out var user)
                || await FindAsync(libraryId, user, http.RequestAborted).ConfigureAwait(false) is not { } cover)
            {
                return null;
            }

            // The handle is closed when the answer is written, and with the request should MVC never write it.
            http.Response.RegisterForDisposeAsync(cover.File);
            var tag = context.ActionArguments.TryGetValue("tag", out var value) ? value as string : null;
            var etag = (tag is null ? null : QuotedTag(tag)) ?? cover.Seen.ETag;
            var result = new FileStreamResult(cover.File, WebpType)
            {
                LastModified = new DateTimeOffset(cover.Seen.Stamp.Ticks, TimeSpan.Zero),
                EntityTag = new EntityTagHeaderValue(etag),
                EnableRangeProcessing = true,
            };

            // Last, so that nothing is left on the response when Jellyfin's action answers instead.
            http.Response.Headers.CacheControl = string.IsNullOrEmpty(tag) ? PublicCache : ImmutableCache;
            return result;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client left while the file was being written; the action sees the same cancelled request.
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Jellyfin Animated Posters: could not tell whether the Primary image of library {LibraryId} is an "
                + "animated WebP, so Jellyfin answers the request",
                libraryId);
            return null;
        }
    }

    // Who the request is from, as Jellyfin's authentication put it in the claims: a user, or null for an anonymous
    // request and for an API key (authenticated with an empty user id), which Jellyfin's action answers for every
    // library alike. False, to leave the request to Jellyfin, when it is authenticated without a user id claim that
    // can be read.
    private static bool TryRequestUser(HttpContext http, out Guid? user)
    {
        user = null;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        if (!Guid.TryParse(http.User.FindFirst(UserIdClaim)?.Value, out var id))
        {
            return false;
        }

        user = id == Guid.Empty ? null : id;
        return true;
    }

    // The library's Primary image, opened, when it is an animated WebP, or null. Waits while Jellyfin writes it. With
    // a user, the library is looked up as Jellyfin's action looks it up for that user, so a library the user may not
    // see is left to Jellyfin (which answers 404); an anonymous request sees every library, as in Jellyfin.
    private async Task<Cover?> FindAsync(Guid libraryId, Guid? user, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(_busyWait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var item = user is { } id
                ? _libraries.GetItemById<BaseItem>(libraryId, id)
                : _libraries.GetItemById(libraryId);
            if (item is not CollectionFolder library
                || library.GetImageInfo(ImageType.Primary, 0) is not { } image
                || string.IsNullOrEmpty(image.Path)
                || !image.IsLocalFile)
            {
                return null;
            }

            // The plugin moved its cover out of the way and Jellyfin is saving the new one. Until the save returns,
            // a file at the path may exist and not be written yet (.NET creates it before the writer takes its lock),
            // and a reader that opened it then would make the save fail and go elsewhere.
            var waited = Stopwatch.GetTimestamp() < deadline;
            if (_replacing(libraryId) && waited)
            {
                await Task.Delay(BusyPoll, ct).ConfigureAwait(false);
                continue;
            }

            // Held past the wait before, and unchanged since: left to Jellyfin at once, without another wait.
            if (_held.TryGetValue(image.Path, out var held))
            {
                if (held == Stat(image.Path))
                {
                    return null;
                }

                _held.TryRemove(image.Path, out _);
            }

            try
            {
                return Open(image.Path);
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                if (waited)
                {
                    // Held by Jellyfin's image saver (a sharing violation): look again, at the item too, once it is
                    // done.
                    await Task.Delay(BusyPoll, ct).ConfigureAwait(false);
                    continue;
                }

                // Something else holds it, or it cannot be read at all: said once for this file as it is now.
                if (_held.TryAdd(image.Path, Stat(image.Path)))
                {
                    _log.LogWarning(
                        ex,
                        "Jellyfin Animated Posters: the Primary image of library {LibraryId} ({Path}) could not be "
                        + "read for {Seconds} s, so Jellyfin answers the requests for it until the file changes",
                        libraryId,
                        image.Path,
                        _busyWait.TotalSeconds);
                }

                return null;
            }
        }
    }

    // The file's time and length as a stat gives them, or Missing.
    private static FileStamp Stat(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length) : FileStamp.Missing;
    }

    // The image opened for the answer when it is an animated WebP, or null (also when it does not exist). The header
    // is read only when the file changed since it was last read, judged by the open handle's own stat.
    private Cover? Open(string path)
    {
        FileStream file;
        try
        {
            file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            var stamp = new FileStamp(File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks, file.Length);
            if (!_images.TryGetValue(path, out var seen) || seen.Stamp != stamp)
            {
                seen = Look(file, path, stamp);
                _images[path] = seen;
            }

            if (seen.Animated)
            {
                file.Position = 0;
                return new Cover(file, seen);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }

        file.Dispose();
        return null;
    }

    private static ImageSeen Look(FileStream file, string path, FileStamp stamp)
    {
        Span<byte> header = stackalloc byte[WebpHeaderLength];
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{path}\n{stamp.Ticks}\n{stamp.Length}")));
        var etag = "\"" + Convert.ToHexStringLower(hash.AsSpan(0, 16)) + "\"";
        return new ImageSeen(stamp, IsAnimatedWebp(header[..read]), etag);
    }

    private readonly record struct FileStamp(long Ticks, long Length)
    {
        public static FileStamp Missing { get; } = new(-1, -1);
    }

    private sealed record ImageSeen(FileStamp Stamp, bool Animated, string ETag);

    private sealed record Cover(FileStream File, ImageSeen Seen);
}
