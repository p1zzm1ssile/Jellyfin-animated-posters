using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Scheduling;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.LibraryCoverMaker.Api;

/// <summary>
/// The admin API of the Dashboard page. Every endpoint needs an administrator: Jellyfin's own
/// <c>RequiresElevation</c> policy, which accepts only the <c>Authorization: MediaBrowser Token="..."</c> header.
/// </summary>
/// <remarks>
/// A library's state changes only through <see cref="CoverStore.Update"/> and <see cref="CoverStore.UpdateAsync"/>.
/// Anything that applies a cover goes through the applier's own run, so that the change counts as the plugin's and
/// not as an upload in Jellyfin's Edit images dialog.
/// </remarks>
[ApiController]
[Route("LibraryCovers")]
[Authorize(Policy = "RequiresElevation")]
public class CoversController : ControllerBase
{
    // Titles a search gives back.
    private const int MaxTitles = 50;
    private const int MaxSearchLength = 100;

    // The width of a frame preview, in pixels, and how many of them ffmpeg reads at once.
    private const int FrameWidth = 320;
    private const int FrameJpegQuality = 85;
    private static readonly SemaphoreSlim FrameReads = new(2, 2);

    // Multipart framing and form fields on top of the file itself.
    private const long UploadOverhead = 1024 * 1024;

    // Builds an admin asked for, shared by every request (the controller itself lives for one request only).
    private static BuildCoalescer? _builds;

    // The configuration is one shared object: two settings changes must not interleave.
    private static readonly Lock SettingsGate = new();

    private readonly ILibraryManager _library;
    private readonly CoverStore _store;
    private readonly CoverApplier _applier;
    private readonly RenderQueue _queue;
    private readonly LibraryAudience _audience;
    private readonly FfmpegRunner _ffmpeg;
    private readonly ILogger<CoversController> _log;

    public CoversController(
        ILibraryManager library,
        CoverStore store,
        CoverApplier applier,
        RenderQueue queue,
        LibraryAudience audience,
        FfmpegRunner ffmpeg,
        ILogger<CoversController> log)
    {
        _library = library;
        _store = store;
        _applier = applier;
        _queue = queue;
        _audience = audience;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    // ---- Libraries --------------------------------------------------------------------------------------------------

    /// <summary>Every library with what its card shows.</summary>
    [HttpGet("Libraries")]
    public ActionResult<IReadOnlyList<LibraryCardDto>> GetLibraries()
    {
        var mode = CoverApplier.Settings().DefaultMode;
        var cards = new List<LibraryCardDto>();
        foreach (var info in _library.GetVirtualFolders())
        {
            if (Guid.TryParse(info.ItemId, out var id) && Folder(id) is { } folder)
            {
                cards.Add(Card(folder, _store.Load(id, mode)));
            }
        }

        return cards;
    }

    /// <summary>Everything the plugin keeps about one library's cover.</summary>
    [HttpGet("Libraries/{libraryId}")]
    public ActionResult<LibraryStateDto> GetLibrary([FromRoute] Guid libraryId) =>
        Folder(libraryId) is null
            ? NotFound(Error(Missing(libraryId)))
            : LibraryStateDto.From(_store.Load(libraryId, DefaultMode()));

    /// <summary>
    /// Changes a library's mode, style, slots or renewal days. A part left out stays as it is; a value outside its
    /// range is refused and nothing is saved. When the slots changed, the cover is rebuilt from them.
    /// </summary>
    [HttpPut("Libraries/{libraryId}")]
    public async Task<ActionResult<LibraryStateDto>> UpdateLibrary(
        [FromRoute] Guid libraryId,
        [FromBody] LibraryUpdateDto update,
        CancellationToken cancellationToken)
    {
        if (Folder(libraryId) is not { } folder)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        var errors = new List<string>();
        if (update.Mode is { } mode && !CoverRequestRules.IsDefined(mode))
        {
            errors.Add("Mode must be Art, Scenes or Manual.");
        }

        if (update.Style is not null)
        {
            errors.AddRange(CoverRequestRules.CheckStyle(update.Style));
        }

        if (CoverRequestRules.CheckRenewal(update.RenewEveryDays) is { } renewal)
        {
            errors.Add(renewal);
        }

        if (errors.Count > 0)
        {
            return Rejected(errors);
        }

        var slotsChanged = false;
        LibraryCoverState state;
        try
        {
            state = await _store.UpdateAsync(
                libraryId,
                (current, token) =>
                {
                    token.ThrowIfCancellationRequested();

                    // Inside the library's gate, so that what was checked is what is saved: the locks are judged
                    // against the audience as it is now.
                    if (update.Slots is not null)
                    {
                        var viewers = _audience.UsersWhoSee(libraryId);
                        var problems = CoverRequestRules.CheckSlots(
                            update.Slots,
                            slot => LockProblem(folder, viewers, slot));
                        if (problems.Count > 0)
                        {
                            throw new RejectedException(problems);
                        }
                    }

                    current.Mode = update.Mode ?? current.Mode;
                    if (update.Style is not null)
                    {
                        CoverRequestRules.ApplyStyle(update.Style, current.Style);
                    }

                    if (update.RenewEveryDays is { } days)
                    {
                        current.RenewEveryDays = days == 0 ? null : days;
                    }

                    if (update.Slots is not null)
                    {
                        var before = SlotKey(current.Slots);
                        CoverRequestRules.ApplySlots(update.Slots, current.Slots);
                        slotsChanged = before != SlotKey(current.Slots);
                    }

                    return Task.CompletedTask;
                },
                DefaultMode(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RejectedException ex)
        {
            return Rejected(ex.Errors);
        }

        // The slots are the one change that asks for a new cover by itself (spec 7.4); the style and the mode wait
        // for "Generate now".
        if (slotsChanged
            && Enabled(folder)
            && CoverRules.ShouldBuild(state, CollectionTypeOf(folder), BuildReason.Slots, mustReplace: false))
        {
            Builds().Request(libraryId, BuildReason.Slots);
        }

        return LibraryStateDto.From(state);
    }

    /// <summary>
    /// Starts building a library's cover now and answers at once; the card shows the result. A press while a build
    /// of the library is running asks for one more build after it.
    /// </summary>
    [HttpPost("Libraries/{libraryId}/Generate")]
    public ActionResult<GenerateAcceptedDto> Generate([FromRoute] Guid libraryId)
    {
        if (Folder(libraryId) is not { } folder)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        if (!CoverRules.IsVideoLibrary(CollectionTypeOf(folder)))
        {
            return BadRequest(Error("The plugin makes covers for video libraries only. Upload one for this library."));
        }

        if (!Enabled(folder))
        {
            return Conflict(Error("This library is switched off, so it has no cover to build."));
        }

        var state = _store.Load(libraryId, DefaultMode());
        if (!CoverRules.ShouldBuild(state, CollectionTypeOf(folder), BuildReason.Manual, mustReplace: false))
        {
            return Conflict(Error("This library is pinned. Choose Back to automatic first."));
        }

        var queued = Builds().Request(libraryId, BuildReason.Manual) ? 1 : 0;
        return Accepted(new GenerateAcceptedDto { Queued = queued });
    }

    /// <summary>
    /// Starts building the cover of every library the plugin renders and that is not pinned. <c>Queued</c> counts the
    /// libraries for which a build was started or is now due after the one running.
    /// </summary>
    [HttpPost("GenerateAll")]
    public ActionResult<GenerateAcceptedDto> GenerateAll()
    {
        var mode = DefaultMode();
        var queued = 0;
        foreach (var info in _library.GetVirtualFolders())
        {
            if (!Guid.TryParse(info.ItemId, out var id) || Folder(id) is not { } folder || !Enabled(folder))
            {
                continue;
            }

            if (CoverRules.ShouldBuild(_store.Load(id, mode), CollectionTypeOf(folder), BuildReason.Manual, false))
            {
                if (Builds().Request(id, BuildReason.Manual))
                {
                    queued++;
                }
            }
        }

        return Accepted(new GenerateAcceptedDto { Queued = queued });
    }

    /// <summary>
    /// Makes an uploaded WebP, PNG, JPEG or GIF of up to 20 MB the library's Primary image and pins the library. The
    /// file's own bytes decide what it is, not the type the client sent.
    /// </summary>
    [HttpPost("Libraries/{libraryId}/Upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CoverRequestRules.MaxUploadBytes + UploadOverhead)]
    [RequestFormLimits(MultipartBodyLengthLimit = CoverRequestRules.MaxUploadBytes + UploadOverhead)]
    public async Task<ActionResult<LibraryStateDto>> Upload(
        [FromRoute] Guid libraryId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (Folder(libraryId) is null)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        if (file.Length == 0)
        {
            return BadRequest(Error("The file is empty."));
        }

        if (file.Length > CoverRequestRules.MaxUploadBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, Error("A cover can be at most 20 MB."));
        }

        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            var header = new byte[CoverRequestRules.HeaderLength];
            var read = await stream
                .ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            if (CoverRequestRules.DetectImageType(header.AsSpan(0, read)) is not { } mimeType)
            {
                return StatusCode(
                    StatusCodes.Status415UnsupportedMediaType,
                    Error("The file is not a WebP, PNG, JPEG or GIF image."));
            }

            // The form's file stream is a buffered one, so it can be rewound.
            if (CoverRequestRules.CheckImage(stream, mimeType) is { } unreadable)
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, Error(unreadable));
            }

            try
            {
                // It pins in the same change of the state, so the library is never unpinned in between.
                await _applier.SaveUploadAsync(libraryId, stream, mimeType, cancellationToken).ConfigureAwait(false);
            }
            catch (LibraryGoneException)
            {
                return NotFound(Error(Missing(libraryId)));
            }
        }

        return LibraryStateDto.From(_store.Load(libraryId, DefaultMode()));
    }

    /// <summary>Keeps the current cover and takes the library out of the rotation.</summary>
    [HttpPost("Libraries/{libraryId}/Pin")]
    public async Task<ActionResult<LibraryStateDto>> Pin(
        [FromRoute] Guid libraryId,
        CancellationToken cancellationToken)
    {
        if (Folder(libraryId) is null)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        var state = await _store.UpdateAsync(
            libraryId,
            (current, _) =>
            {
                current.Pinned = true;
                return Task.CompletedTask;
            },
            DefaultMode(),
            cancellationToken).ConfigureAwait(false);
        return LibraryStateDto.From(state);
    }

    /// <summary>Back to automatic: the library follows the rules again and its notice is cleared.</summary>
    [HttpPost("Libraries/{libraryId}/Unpin")]
    public async Task<ActionResult<LibraryStateDto>> Unpin(
        [FromRoute] Guid libraryId,
        CancellationToken cancellationToken)
    {
        if (Folder(libraryId) is null)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        var state = await _store.UpdateAsync(
            libraryId,
            (current, _) =>
            {
                current.Pinned = false;
                current.Notice = null;
                current.NoticeAt = null;
                return Task.CompletedTask;
            },
            DefaultMode(),
            cancellationToken).ConfigureAwait(false);
        return LibraryStateDto.From(state);
    }

    /// <summary>
    /// Makes a cover of the library's history, or its original, the Primary image again, and pins the library. An
    /// original that was Jellyfin's automatic collage gives the library back to the collage instead of putting a
    /// copy of it in place. A cover the plugin composed is refused with 409 when one of its titles is hidden from a
    /// viewer of the library now, or is no longer in it (decision 9); an upload and the original are the admin's call.
    /// </summary>
    [HttpPost("Libraries/{libraryId}/Restore/{coverId}")]
    public async Task<ActionResult<LibraryStateDto>> Restore(
        [FromRoute] Guid libraryId,
        [FromRoute] Guid coverId,
        CancellationToken cancellationToken)
    {
        if (Folder(libraryId) is null)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        using var run = _applier.StartApply(libraryId);
        try
        {
            var state = await _store.UpdateAsync(
                libraryId,
                async (current, token) =>
                {
                    var record = FindCover(current, coverId) ?? throw new CoverMissingException(coverId);

                    // Under the library's gate, so the answer holds for the cover that is applied.
                    if (record.Origin == CoverOrigin.Generated && RestoreProblem(libraryId, record) is { } problem)
                    {
                        throw new RestoreRefusedException(problem);
                    }

                    if (record is { Origin: CoverOrigin.Original, Automatic: true })
                    {
                        await run.ReturnToAutomaticAsync(current, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await run.ApplyAsync(current, record, CoverApplier.Settings().PrimaryFormat, token)
                            .ConfigureAwait(false);
                    }

                    current.Pinned = true;
                },
                DefaultMode(),
                cancellationToken).ConfigureAwait(false);
            run.Saved();
            return LibraryStateDto.From(state);
        }
        catch (CoverMissingException ex)
        {
            run.Unsaved(ex);
            return NotFound(Error($"The library has no cover {coverId:N}."));
        }
        catch (RestoreRefusedException ex)
        {
            run.Unsaved(ex);
            return Conflict(Error(ex.Message));
        }
        catch (LibraryGoneException ex)
        {
            run.Unsaved(ex);
            return NotFound(Error(Missing(libraryId)));
        }
        catch (FileNotFoundException ex)
        {
            // The message names a server path: it goes to the log, not to the client.
            run.Unsaved(ex);
            _log.LogWarning(
                ex,
                "Library Cover Maker: cover {CoverId} of library {LibraryId} has no file",
                coverId,
                libraryId);
            return NotFound(Error($"The file of cover {coverId:N} is missing."));
        }
        catch (Exception ex)
        {
            run.Unsaved(ex);
            throw;
        }
    }

    /// <summary>A cover's file: the animated version, or the still JPEG with <c>still=true</c>.</summary>
    [HttpGet("Libraries/{libraryId}/Covers/{coverId}")]
    public ActionResult GetCover(
        [FromRoute] Guid libraryId,
        [FromRoute] Guid coverId,
        [FromQuery] bool still = false)
    {
        if (Folder(libraryId) is null)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        var state = _store.Load(libraryId, DefaultMode());
        var file = FindCover(state, coverId) is { } record
            ? CoverApplier.CoverFile(_store, libraryId, record, still ? PrimaryFormat.Still : PrimaryFormat.Animated)
            : null;
        if (file is null)
        {
            return NotFound(Error($"The library has no file for cover {coverId:N}."));
        }

        // A cover's files never change: a new cover gets a new id.
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return PhysicalFile(file.Value.Path, file.Value.MimeType);
    }

    // ---- Titles and pictures ----------------------------------------------------------------------------------------

    /// <summary>Titles every viewer of the library may see that match <paramref name="search"/>, 50 at most.</summary>
    [HttpGet("Libraries/{libraryId}/Titles")]
    public ActionResult<IReadOnlyList<TitleDto>> GetTitles(
        [FromRoute] Guid libraryId,
        [FromQuery] string? search = null)
    {
        if (Folder(libraryId) is not { } folder)
        {
            return NotFound(Error(Missing(libraryId)));
        }

        var term = search?.Trim();
        if (term is { Length: > MaxSearchLength })
        {
            return BadRequest(Error($"The search can be at most {MaxSearchLength} characters."));
        }

        var type = CollectionTypeOf(folder);
        if (!CoverRules.IsVideoLibrary(type))
        {
            return new List<TitleDto>();
        }

        var viewers = _audience.UsersWhoSee(libraryId);
        var query = new InternalItemsQuery
        {
            AncestorIds = [libraryId],
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = JellyfinCoverSource.TitleKinds(type),
            SearchTerm = string.IsNullOrEmpty(term) ? null : term,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        };
        return _library.GetItemList(query)
            .Where(item => _audience.AllowedForAll(item, viewers))
            .Take(MaxTitles)
            .Select(item => new TitleDto
            {
                Id = item.Id,
                Name = item.Name,
                Year = item.ProductionYear,
                HasBackdrop = item.GetImages(ImageType.Backdrop).Any(image => image.IsLocalFile),
            })
            .ToList();
    }

    /// <summary>
    /// A JPEG of the title's video at <paramref name="at"/> seconds, 320 pixels wide, for the slot's slider. For a
    /// series, the video is its first usable episode, the one a scenes cover takes its clip from.
    /// </summary>
    [HttpGet("Items/{itemId}/Frame")]
    public async Task<ActionResult> GetFrame(
        [FromRoute] Guid itemId,
        [FromQuery, BindRequired] double at,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(at) || at < 0)
        {
            return BadRequest(Error("at must be a time in seconds from the start of the video."));
        }

        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(Error($"There is no item {itemId:N}."));
        }

        var video = item switch
        {
            Video single => single,
            MediaBrowser.Controller.Entities.TV.Series series => EpisodeOf(series, ViewersOfLibraryOf(series)),
            _ => null,
        };
        if (video is null || !IsOnDisk(video))
        {
            return NotFound(Error("The item has no video on this server."));
        }

        if (video.RunTimeTicks is > 0 && at > video.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond)
        {
            return BadRequest(Error("at is past the end of the video."));
        }

        byte[]? png;
        await FrameReads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            png = await _ffmpeg.ReadFrameAsync(video.Path, at, FrameWidth, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FrameReads.Release();
        }

        if (png is null)
        {
            return NotFound(Error("The video has no frame at that moment."));
        }

        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return NotFound(Error("The frame could not be read."));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, FrameJpegQuality);
        return File(jpeg.ToArray(), MediaTypeNames.Image.Jpeg);
    }

    /// <summary>The title's backdrop images: the number a locked slot stores and where Jellyfin serves each.</summary>
    [HttpGet("Items/{itemId}/Backdrops")]
    public ActionResult<IReadOnlyList<BackdropDto>> GetBackdrops([FromRoute] Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(Error($"There is no item {itemId:N}."));
        }

        return item.GetImages(ImageType.Backdrop)
            .Select((image, index) => new BackdropDto
            {
                Index = index,
                Url = $"Items/{itemId:N}/Images/Backdrop/{index}?maxWidth=480",
                Usable = image.IsLocalFile && !string.IsNullOrEmpty(image.Path) && System.IO.File.Exists(image.Path),
            })
            .ToList();
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------

    /// <summary>The settings that apply to every library.</summary>
    [HttpGet("Settings")]
    public ActionResult<SettingsDto> GetSettings() => ToDto(CoverApplier.Settings());

    /// <summary>
    /// Changes the settings. Changing <c>PrimaryFormat</c> answers 202: the other version of every library's current
    /// cover is applied in the background, without rendering again, and the cards show it when it is done.
    /// </summary>
    [HttpPut("Settings")]
    public ActionResult<SettingsDto> UpdateSettings([FromBody] SettingsUpdateDto update)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Error("The plugin is not loaded yet."));
        }

        var reapply = false;
        SettingsDto saved;
        lock (SettingsGate)
        {
            var current = plugin.Configuration;
            var errors = CoverRequestRules.CheckSettings(update, current);
            if (errors.Count > 0)
            {
                return Rejected(errors);
            }

            reapply = update.PrimaryFormat is { } format && format != current.PrimaryFormat;

            // A copy is saved first and becomes the live configuration only when the save went through, so a failed
            // save leaves nothing half applied.
            var changed = new PluginConfiguration
            {
                PrimaryFormat = update.PrimaryFormat ?? current.PrimaryFormat,
                DefaultMode = update.DefaultMode ?? current.DefaultMode,
                OutputWidth = update.OutputWidth ?? current.OutputWidth,
                OutputHeight = update.OutputHeight ?? current.OutputHeight,
                HistorySize = current.HistorySize,
            };
            plugin.SaveConfiguration(changed);
            plugin.UpdateConfiguration(changed);
            saved = ToDto(changed);
        }

        if (!reapply)
        {
            return saved;
        }

        // Not tied to the request: a library must not be left half way because the page was closed. Each library is
        // applied in the format the configuration has by then, and the runs go one at a time, so the last change wins.
        _ = Task.Run(async () =>
        {
            try
            {
                await _applier.ReapplyAllToConfiguredAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Library Cover Maker: applying the other version of the covers failed");
            }
        });
        return Accepted(saved);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static PluginConfiguration Configuration() => CoverApplier.Settings();

    private static CoverMode DefaultMode() => Configuration().DefaultMode;

    private static SettingsDto ToDto(PluginConfiguration config) => new()
    {
        PrimaryFormat = config.PrimaryFormat,
        DefaultMode = config.DefaultMode,
        OutputWidth = config.OutputWidth,
        OutputHeight = config.OutputHeight,
        Fonts = FontCatalog.Families,
    };

    private static ApiError Error(string detail) => new(detail);

    private static string Missing(Guid libraryId) => $"There is no library {libraryId:N}.";

    private ActionResult Rejected(IReadOnlyList<string> errors) =>
        BadRequest(Error(string.Join(" ", errors)));

    private CollectionFolder? Folder(Guid libraryId) => _library.GetItemById(libraryId) as CollectionFolder;

    // The collection types are named in lowercase (movies, tvshows), the way the rules compare them.
    private static string? CollectionTypeOf(CollectionFolder folder) => folder.CollectionType?.ToString();

    private static bool Enabled(CollectionFolder folder) => folder.GetLibraryOptions().Enabled;

    private static CoverRecord? FindCover(LibraryCoverState state, Guid coverId) =>
        state.History.Find(cover => cover.Id == coverId)
        ?? (state.Original is { } original && original.Id == coverId ? original : null);

    private static string SlotKey(CoverSlot[] slots) =>
        string.Join(
            "|",
            slots.Select(slot =>
                FormattableString.Invariant($"{slot.ItemId:N}/{slot.MomentSeconds}/{slot.BackdropIndex}")));

    private static LibraryCardDto Card(CollectionFolder folder, LibraryCoverState state)
    {
        var type = CollectionTypeOf(folder);
        var current = FindCover(state, state.CurrentCoverId ?? Guid.Empty);
        var canRender = CoverRules.IsVideoLibrary(type);
        var own = state.RenewEveryDays is > 0;
        var rotating = canRender && !state.Pinned && state.Mode != CoverMode.Manual;
        return new LibraryCardDto
        {
            Id = folder.Id,
            Name = folder.Name,
            CollectionType = type,
            Enabled = Enabled(folder),
            CanRender = canRender,
            Mode = state.Mode,
            Pinned = state.Pinned,
            CreatedAt = current?.CreatedAt ?? state.LastRenderedAt,
            NextRenewal = rotating && own
                ? (state.LastRenderedAt ?? DateTimeOffset.UtcNow).AddDays(state.RenewEveryDays!.Value)
                : null,
            Schedule = !rotating ? "off" : own ? "own" : "weekly",
            RenewEveryDays = state.RenewEveryDays,
            LastError = state.LastError,
            Notice = state.Notice,
            NoticeAt = state.NoticeAt,
            CurrentCoverId = state.CurrentCoverId,
        };
    }

    // Why a locked slot is not acceptable, or null: the title must be in this library, be one of its titles, be
    // something every viewer of the library may see, and have the moment or the backdrop the slot asks for.
    private string? LockProblem(CollectionFolder folder, IReadOnlyList<User> viewers, SlotUpdateDto slot)
    {
        var item = _library.GetItemById(slot.ItemId!.Value);
        if (item is null || !_library.GetCollectionFolders(item).Exists(parent => parent.Id == folder.Id))
        {
            return "that title is not in this library.";
        }

        if (!JellyfinCoverSource.TitleKinds(CollectionTypeOf(folder)).Contains(item.GetBaseItemKind()))
        {
            return "that item is not one of this library's titles.";
        }

        if (!_audience.AllowedForAll(item, viewers))
        {
            return "that title may not be shown, because someone who can see this library may not see it.";
        }

        // A series' clip comes from its first usable episode, so the moment is judged against that episode.
        var video = item switch
        {
            Video single => single,
            MediaBrowser.Controller.Entities.TV.Series series => EpisodeOf(series, viewers),
            _ => null,
        };
        if (slot.MomentSeconds is { } moment
            && video?.RunTimeTicks is > 0
            && moment > video.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond)
        {
            return "the moment is past the end of the video.";
        }

        // The index counts all the title's backdrops, as the Backdrops endpoint does; the one chosen must be a file.
        if (slot.BackdropIndex is { } index)
        {
            var backdrop = item.GetImages(ImageType.Backdrop).ElementAtOrDefault(index);
            if (backdrop is not { IsLocalFile: true } || !System.IO.File.Exists(backdrop.Path))
            {
                return "that title has no such backdrop on this server.";
            }
        }

        return null;
    }

    // Why a cover the plugin composed may not be restored now, or null: every title it shows (the series and the
    // episode of a series slot) must still be in the library and be something every viewer may see.
    private string? RestoreProblem(Guid libraryId, CoverRecord record)
    {
        var viewers = _audience.UsersWhoSee(libraryId);
        var refused = record.TitleIds
            .Select(id => JellyfinCoverSource.AccessOf(_library, _audience, libraryId, id, viewers))
            .Where(access => access != TitleAccess.Allowed)
            .ToList();
        if (refused.Count == 0)
        {
            return null;
        }

        return refused.Contains(TitleAccess.Hidden)
            ? "That cover shows a title some viewers of this library may not see now, so it cannot be restored."
            : "That cover shows a title that is no longer in this library, so it cannot be restored.";
    }

    // The episode the build takes a series' clip from, for the same viewers, so the slider shows what is rendered.
    private Video? EpisodeOf(MediaBrowser.Controller.Entities.TV.Series series, IReadOnlyList<User> viewers) =>
        JellyfinCoverSource.EpisodeFor(_library, _audience, series, viewers);

    // The viewers of the library a series is in; nobody (so every episode) when Jellyfin knows of no library.
    private IReadOnlyList<User> ViewersOfLibraryOf(BaseItem item) =>
        _library.GetCollectionFolders(item).FirstOrDefault() is { } library ? _audience.UsersWhoSee(library.Id) : [];

    private static bool IsOnDisk(Video video) =>
        !video.IsPlaceHolder
        && !string.IsNullOrEmpty(video.Path)
        && Path.IsPathRooted(video.Path)
        && System.IO.File.Exists(video.Path);

    // The one coalescer all requests share. It is made from the first request's services, which are singletons.
    // Only the queue and the logger are captured, never the controller: it holds this request's HttpContext.
    private BuildCoalescer Builds()
    {
        var queue = _queue;
        var log = _log;
        return LazyInitializer.EnsureInitialized(
            ref _builds,
            () => new BuildCoalescer(
                // Not tied to the request: closing the page must not cancel a render the admin asked for.
                (id, reason) => queue.RunNowAsync(id, reason, CancellationToken.None),
                (id, reason, task) =>
                {
                    if (task.IsFaulted)
                    {
                        log.LogError(
                            task.Exception,
                            "Library Cover Maker: building library {LibraryId} failed",
                            id);
                    }
                    else if (task.IsCompletedSuccessfully)
                    {
                        log.LogInformation(
                            "Library Cover Maker: {Reason} build of library {LibraryId}: applied {Applied}, "
                            + "error {Error}",
                            reason,
                            id,
                            task.Result.Applied,
                            task.Result.Error ?? "none");
                    }
                }));
    }

    /// <summary>A cover the library's state does not list.</summary>
    private sealed class CoverMissingException(Guid coverId) : Exception($"The library has no cover {coverId:N}.");

    /// <summary>A restore the visibility rule refused: nothing was applied or saved.</summary>
    private sealed class RestoreRefusedException(string reason) : Exception(reason);

    /// <summary>A request the rules refused after the library's gate was taken: nothing was saved.</summary>
    private sealed class RejectedException(IReadOnlyList<string> errors) : Exception("The request was refused.")
    {
        public IReadOnlyList<string> Errors { get; } = errors;
    }
}

/// <summary>
/// The builds an admin asks for, one library at a time without losing a request. A request for a library with
/// nothing in flight starts a build. While one is in flight, a Manual request goes ahead of a waiting non-Manual job
/// (the queue runs Manual jobs first) when no Manual job is in flight; any other request is remembered, and one more
/// build, of the strongest reason asked for meanwhile (Manual over Slots), starts when the in-flight ones are done.
/// So a change made during a render is never left out of the cover that follows.
/// </summary>
internal sealed class BuildCoalescer(
    Func<Guid, BuildReason, Task<BuildResult>> run,
    Action<Guid, BuildReason, Task<BuildResult>>? finished = null)
{
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Asks for a build of the library. True when a build was started or is now due after the one in flight; false
    /// when an equal or stronger build was already due and nothing changed.
    /// </summary>
    public bool Request(Guid libraryId, BuildReason reason)
    {
        bool start;
        bool accepted;
        lock (_lock)
        {
            if (!_entries.TryGetValue(libraryId, out var entry))
            {
                entry = new Entry();
                _entries[libraryId] = entry;
                entry.InFlight.Add(reason);
                start = accepted = true;
            }
            else if (reason == BuildReason.Manual && !entry.InFlight.Contains(BuildReason.Manual))
            {
                entry.InFlight.Add(reason);
                start = accepted = true;
            }
            else
            {
                start = false;
                var stronger = Stronger(entry.Pending, reason);
                accepted = entry.Pending != stronger;
                entry.Pending = stronger;
            }
        }

        if (start)
        {
            Start(libraryId, reason);
        }

        return accepted;
    }

    private static BuildReason Stronger(BuildReason? pending, BuildReason reason) =>
        pending == BuildReason.Manual || reason == BuildReason.Manual ? BuildReason.Manual : reason;

    private void Start(Guid libraryId, BuildReason reason)
    {
        Task<BuildResult> task;
        try
        {
            task = run(libraryId, reason);
        }
        catch (Exception ex)
        {
            task = Task.FromException<BuildResult>(ex);
        }

        _ = task.ContinueWith(
            done => Finish(libraryId, reason, done),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void Finish(Guid libraryId, BuildReason reason, Task<BuildResult> done)
    {
        try
        {
            finished?.Invoke(libraryId, reason, done);
        }
        catch (Exception)
        {
            // Reporting must never stop the follow-up build.
        }

        BuildReason? next = null;
        lock (_lock)
        {
            var entry = _entries[libraryId];
            entry.InFlight.Remove(reason);
            if (entry.InFlight.Count == 0)
            {
                if (entry.Pending is { } pending)
                {
                    next = pending;
                    entry.Pending = null;
                    entry.InFlight.Add(pending);
                }
                else
                {
                    _entries.Remove(libraryId);
                }
            }
        }

        // A build that was cancelled because the server is stopping is not followed by another.
        if (next is { } follow && !done.IsCanceled)
        {
            Start(libraryId, follow);
        }
        else if (next is not null)
        {
            lock (_lock)
            {
                _entries.Remove(libraryId);
            }
        }
    }

    private sealed class Entry
    {
        public List<BuildReason> InFlight { get; } = new();

        public BuildReason? Pending { get; set; }
    }
}
