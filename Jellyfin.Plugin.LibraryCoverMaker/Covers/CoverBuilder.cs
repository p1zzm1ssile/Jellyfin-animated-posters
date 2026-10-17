using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Rendering;
using Jellyfin.Plugin.LibraryCoverMaker.Selection;
using Jellyfin.Plugin.LibraryCoverMaker.Sources;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.LibraryCoverMaker.Covers;

/// <summary>
/// Builds one library's cover: chooses its titles, makes a slot of each, renders the cover and applies it as the
/// library's Primary image.
/// </summary>
/// <remarks>
/// <para>
/// A build reads the library's state, frees the locked titles that some viewer may not see (or that are no longer in
/// the library), and asks <see cref="CoverRules.ShouldBuild"/> whether it may go on. The titles come from
/// <see cref="TitlePicker"/>; each becomes a slot (art: a backdrop; scenes: a clip that <see cref="SceneFinder"/>
/// chose, from the first allowed episode of season one for a series; a locked moment gets its crop measured). A title
/// that gives no slot is replaced by another eligible title, two titles at most per slot. Art mode that ends with no
/// slot (homevideos and mixed libraries usually have no artwork) tries scenes, scenes mode that ends with no slot
/// tries artwork, and a cover with no slot at all is the library's name on a plain background. When fewer slots came
/// out than planned, each lasts longer, and every clip is chosen again at the longer length, so that what is rendered
/// is what was checked.
/// </para>
/// <para>
/// The new cover's record lists, in <see cref="CoverRecord.TitleIds"/>, the titles in slot order and after them the
/// episode each series slot shows, so that both the series and the episode are checked against the visibility rule
/// later. A title id is allowed when every viewer of the library may see that item (and it is still in the library).
/// </para>
/// <para>
/// The render happens outside any lock, into new files named by a new cover id. Then, inside the library's
/// <see cref="CoverStore.UpdateAsync"/>, the build throws the render away when an admin put another cover on the
/// library meanwhile, when an admin pinned it or switched its mode and its cover does not have to be replaced, or, for
/// a pinned library, when its cover no longer has to be replaced; it fails when a title of the new cover is now hidden
/// from a viewer. Otherwise it applies the cover in the Primary format configured at that moment, makes it the current
/// one and adds it to the history, keeping the library's pin and mode; the store saves the state. A cover that had to
/// be replaced leaves a notice on the card that says whether it was. A failed render keeps the current cover and
/// records why (<see cref="LibraryCoverState.LastError"/>, a warning in the log). A bad title never fails a build, and
/// cancellation leaves the state as it was.
/// </para>
/// </remarks>
public sealed class CoverBuilder
{
    // Every cover is animated at 12 frames a second (spec 7.2).
    private const int Fps = 12;

    // How many titles one slot may try before it is given up.
    private const int TriesPerSlot = 2;

    // The documented range of CoverStyle.LengthSeconds.
    private const int MinLength = 2;
    private const int MaxLength = 30;

    private const string HiddenAtCommit =
        "A title of the new cover became hidden from a viewer while it rendered, so it was not applied.";

    private const string GoneAtCommit =
        "A title of the new cover is no longer in the library, so it was not applied.";

    // The background of the name-only cover (decision 11).
    private static readonly SKColor NameOnlyBackground = new(0x1b, 0x1d, 0x24);

    private readonly ICoverSource _source;
    private readonly CoverStore _store;
    private readonly CoverApplier _applier;
    private readonly OverlayRenderer _overlay;
    private readonly Func<RenderPlan, CancellationToken, Task> _render;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<Random> _random;
    private readonly ILogger _log;

    /// <summary>Creates the builder.</summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="userData">Jellyfin's user data, for play counts.</param>
    /// <param name="audience">Who sees a library and what they may see.</param>
    /// <param name="artwork">Makes art mode slots.</param>
    /// <param name="scenes">
    /// Chooses the clips of scenes mode. Its scratch folder should be <see cref="CoverStore.TempFolder"/>.
    /// </param>
    /// <param name="ffmpeg">Runs Jellyfin's ffmpeg for the render.</param>
    /// <param name="overlay">Draws the overlay of a cover.</param>
    /// <param name="store">The covers and the libraries' states.</param>
    /// <param name="applier">Applies a cover as a library's Primary image.</param>
    /// <param name="log">The log.</param>
    public CoverBuilder(
        ILibraryManager library,
        IUserDataManager userData,
        LibraryAudience audience,
        ArtworkSource artwork,
        SceneFinder scenes,
        FfmpegRunner ffmpeg,
        OverlayRenderer overlay,
        CoverStore store,
        CoverApplier applier,
        ILogger<CoverBuilder> log)
        : this(
            new JellyfinCoverSource(library, userData, audience, artwork, scenes, log),
            store,
            applier,
            overlay,
            (plan, ct) => CoverRenderer.RenderAsync(ffmpeg, plan, ct),
            CoverApplier.Settings,
            () => Random.Shared,
            log)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
    }

    // For the tests: Jellyfin's side, the renderer, the configuration and the draws are replaced.
    internal CoverBuilder(
        ICoverSource source,
        CoverStore store,
        CoverApplier applier,
        OverlayRenderer overlay,
        Func<RenderPlan, CancellationToken, Task> render,
        Func<PluginConfiguration> config,
        Func<Random> random,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(log);

        _source = source;
        _store = store;
        _applier = applier;
        _overlay = overlay;
        _render = render;
        _config = config;
        _random = random;
        _log = log;
    }

    /// <summary>
    /// Builds and applies the cover of a library. Never throws for a bad title or a failed render: the result says
    /// what happened. Applied false with an error is a failure (the current cover stays); Applied false without one
    /// means there was nothing to do (the rules said no, the library is switched off, or an admin changed the library
    /// during the render).
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Cancelled. The state and the library's image are as they were.
    /// </exception>
    public async Task<BuildResult> BuildAsync(Guid libraryId, BuildReason reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = _config();
        var library = _source.Library(libraryId);
        if (library is null)
        {
            return new BuildResult(false, "The library no longer exists.", 0);
        }

        if (!library.Enabled)
        {
            _log.LogDebug(
                "Library Cover Maker: library {Library} is switched off, so its cover is not built",
                library.Name);
            return new BuildResult(false, null, 0);
        }

        // Nothing the titles could say lets this build go on: no need to ask Jellyfin for them.
        var state = _store.Load(libraryId, config.DefaultMode);
        if (!CoverRules.ShouldBuild(state, library.CollectionType, reason, mustReplace: true))
        {
            return new BuildResult(false, null, 0);
        }

        var viewers = _source.Viewers(library);
        var candidates = _source.Candidates(library, viewers);
        var candidateIds = candidates.Select(title => title.Id).ToHashSet();

        // A candidate is allowed by construction; anything else (an episode, a lock of another kind) is asked about.
        bool Allowed(Guid id) =>
            candidateIds.Contains(id) || _source.Access(library, id, viewers) == TitleAccess.Allowed;

        // What the card says about the locks this build freed, kept in whatever notice the build ends with.
        string? unlocked = null;
        if (CoverRules.DisallowedLocks(state.Slots, Allowed).Count > 0)
        {
            state = await _store.UpdateAsync(
                libraryId,
                (current, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    unlocked = FreeLocks(library, current, viewers, Allowed);
                    return Task.CompletedTask;
                },
                config.DefaultMode,
                ct).ConfigureAwait(false);
        }

        var mustReplace = CoverRules.MustReplace(CoverRules.Current(state), Allowed);
        if (!CoverRules.ShouldBuild(state, library.CollectionType, reason, mustReplace))
        {
            if (!mustReplace && CoverRules.IsPending(state.Notice))
            {
                await SettleAsync(libraryId, Allowed, unlocked, config, ct).ConfigureAwait(false);
            }

            return new BuildResult(false, null, 0);
        }

        var record = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = CoverOrigin.Generated,
            Mode = state.Mode,
        };
        var slots = await ComposeAsync(library, viewers, state, candidates, ct).ConfigureAwait(false);
        record.TitleIds = slots.Select(slot => slot.Title)
            .Concat(slots.Select(slot => slot.Choice.ShownId).OfType<Guid>())
            .Distinct()
            .ToList();

        // The overlay and the name-only picture live only as long as the render.
        var temporary = new List<string>();
        try
        {
            var inputs = slots.Select(slot => slot.Choice.Input).ToList();
            var plan = Plan(library, state.Style, config, record.Id, inputs, temporary);
            await _render(plan, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errors = ex is CoverRenderException render ? render.Errors : string.Empty;
            _log.LogWarning(
                ex,
                "Library Cover Maker: the cover of library {Library} could not be rendered, so it keeps its current "
                + "cover: {Error}. ffmpeg said: {Errors}",
                library.Name,
                ex.Message,
                errors);
            await RecordFailureAsync(libraryId, ex.Message, Allowed, unlocked, config, ct).ConfigureAwait(false);
            return new BuildResult(false, ex.Message, slots.Count);
        }
        finally
        {
            foreach (var path in temporary)
            {
                TryDelete(path);
            }
        }

        return await CommitAsync(library, reason, state, record, slots.Count, unlocked, config, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The slot of a locked scenes moment: the clip at the admin's moment, moved back when it would run past the end
    /// of the video, with the crop of its burned-in bars measured over exactly that clip.
    /// </summary>
    internal static async Task<SlotInput> LockedClipAsync(
        Video video,
        double moment,
        double seconds,
        bool hdr,
        Func<Video, double, double, CancellationToken, Task<CropRect?>> measureCrop,
        CancellationToken ct)
    {
        var start = Math.Max(0, moment);
        if (video.RunTimeTicks is > 0)
        {
            var runtime = video.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
            start = Math.Max(0, Math.Min(start, runtime - seconds));
        }

        var crop = await measureCrop(video, start, seconds, ct).ConfigureAwait(false);
        return new SlotInput(SlotKind.Video, video.Path, start, seconds, hdr, crop);
    }

    private static CoverStyle WithSlots(CoverStyle style, int slots) => new()
    {
        SlotCount = slots,
        UseMostWatched = style.UseMostWatched,
        UseTopRated = style.UseTopRated,
        UseRecent = style.UseRecent,
    };

    private static double LengthOf(CoverStyle style) => Math.Clamp(style.LengthSeconds, MinLength, MaxLength);

    private static double SlotLength(CoverStyle style, int slots) =>
        CoverRenderer.SlotSeconds(LengthOf(style), Math.Max(1, slots), style.Fade, Fps);

    // Width and height must be even for the encoder.
    private static int Even(int pixels) => Math.Max(2, pixels & ~1);

    private static byte[] NameOnlyPicture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException($"SkiaSharp could not create a {width} by {height} surface.");
        surface.Canvas.Clear(NameOnlyBackground);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp could not encode the name-only picture as PNG.");
        return png.ToArray();
    }

    // Unlocks the slots whose titles some viewer may not see or that left the library, and says so on the card, after
    // the reason of a replacement that is pending. Returns what it said, or null when nothing was unlocked.
    private string? FreeLocks(
        LibraryInfo library,
        LibraryCoverState state,
        IReadOnlyList<User> viewers,
        Func<Guid, bool> allowed)
    {
        var freed = CoverRules.DisallowedLocks(state.Slots, allowed);
        if (freed.Count == 0)
        {
            return null;
        }

        CoverRules.Unlock(state, freed);

        // Both count as not allowed, which is the safe side; the card says which it was. No user is to blame here.
        var gone = freed.Where(id => _source.Access(library, id, viewers) == TitleAccess.Missing).ToList();
        var hidden = freed.Except(gone).Select(id => "\"" + _source.TitleName(id) + "\"").ToList();
        var said = CoverRules.UnlockNotice(null, [], hidden, gone.Count);
        state.Notice = CoverRules.AddUnlocked(state.Notice, said);
        state.NoticeAt = DateTimeOffset.UtcNow;
        _log.LogInformation("Library Cover Maker: {Notice} (library {LibraryId})", said, state.LibraryId);
        return said;
    }

    // The slots of the cover, in order, each with the title it shows. Empty for the name-only cover.
    private async Task<List<Filled>> ComposeAsync(
        LibraryInfo library,
        IReadOnlyList<User> viewers,
        LibraryCoverState state,
        IReadOnlyList<TitleCandidate> candidates,
        CancellationToken ct)
    {
        var random = _random();
        var style = state.Style;
        var slotCount = Math.Clamp(style.SlotCount, 0, LibraryCoverState.MaxSlots);
        var locks = state.Slots.Take(slotCount).Where(slot => slot.Locked).ToList();
        var previous = CoverRules.Current(state)?.TitleIds ?? [];

        // The scene finder writes its result files there; the render queue deletes the folder at each start.
        Directory.CreateDirectory(_store.TempFolder);

        var slots = new List<Filled>();
        var tried = new HashSet<Guid>();
        if (state.Mode != CoverMode.Art || candidates.Any(title => title.HasBackdrop))
        {
            slots = await FillAsync(
                library, viewers, candidates, state.Mode, style, locks, previous, random, tried, ct)
                .ConfigureAwait(false);
        }

        if (slots.Count == 0 && state.Mode == CoverMode.Art)
        {
            slots = await FillAsync(
                library, viewers, candidates, CoverMode.Scenes, style, locks, previous, random, tried, ct)
                .ConfigureAwait(false);
        }
        else if (slots.Count == 0 && state.Mode == CoverMode.Scenes && candidates.Any(title => title.HasBackdrop))
        {
            // No title gave a clip (no file on disk, or only music videos without files): artwork is next.
            slots = await FillAsync(
                library, viewers, candidates, CoverMode.Art, style, locks, previous, random, tried, ct)
                .ConfigureAwait(false);
        }

        if (slots.Count == 0 && tried.Count > 0)
        {
            // Titles were there and none gave a slot: the name-only cover then hides a failure (all files gone, or
            // ffmpeg unable to read any clip), so it is said where an admin looks.
            _log.LogWarning(
                "Library Cover Maker: none of the {Count} titles tried for library {Library} gave a picture or a "
                + "clip, so it gets the name-only cover",
                tried.Count,
                library.Name);
        }

        // With fewer slots than planned each lasts longer. A picture just zooms for longer; a clip is chosen again at
        // the new length, since its check (no cut, no intro or credits) held for the shorter one. A clip that has no
        // choice at the new length leaves, which lengthens the others again. When no clip is left at a longer length,
        // the last set that passed its checks is kept at its own length: a shorter cover, not the name-only one.
        while (slots.Count > 0)
        {
            var seconds = SlotLength(style, slots.Count);
            var next = new List<Filled>(slots.Count);
            foreach (var slot in slots)
            {
                var input = slot.Choice.Input;
                if (input.DurationSeconds == seconds || input.Kind == SlotKind.Image)
                {
                    var longer = slot.Choice with { Input = input with { DurationSeconds = seconds } };
                    next.Add(slot with { Choice = longer });
                    continue;
                }

                var again = await TrySlotAsync(
                    library, viewers, slot.Title, slot.Mode, slot.Locked, seconds, random, ct).ConfigureAwait(false);
                if (again is not null)
                {
                    next.Add(slot with { Choice = again });
                }
            }

            if (next.Count == 0)
            {
                break;
            }

            var shrank = next.Count < slots.Count;
            slots = next;
            if (!shrank)
            {
                break;
            }
        }

        return slots;
    }

    private async Task<List<Filled>> FillAsync(
        LibraryInfo library,
        IReadOnlyList<User> viewers,
        IReadOnlyList<TitleCandidate> candidates,
        CoverMode mode,
        CoverStyle style,
        List<CoverSlot> locks,
        IReadOnlyCollection<Guid> previous,
        Random random,
        ISet<Guid> tried,
        CancellationToken ct)
    {
        var lockedIds = locks.Select(slot => slot.ItemId!.Value).ToList();
        var picked = TitlePicker.Pick(candidates, mode, style, lockedIds, previous, random);
        var filled = new List<Filled>();
        if (picked.Count == 0)
        {
            return filled;
        }

        var seconds = SlotLength(style, picked.Count);
        var failed = new HashSet<Guid>();
        for (var i = 0; i < picked.Count; i++)
        {
            var title = picked[i];
            var locked = locks.FirstOrDefault(slot => slot.ItemId == title);
            for (var attempt = 1; ; attempt++)
            {
                tried.Add(title);
                var choice = await TrySlotAsync(library, viewers, title, mode, locked, seconds, random, ct)
                    .ConfigureAwait(false);
                if (choice is not null)
                {
                    filled.Add(new Filled(title, mode, locked, choice));
                    break;
                }

                failed.Add(title);
                if (attempt == TriesPerSlot)
                {
                    break;
                }

                // The titles of the other slots stay where they are; one new draw replaces this one.
                var keep = filled.Select(slot => slot.Title).Concat(picked.Skip(i + 1)).ToList();
                var others = candidates.Where(candidate => !failed.Contains(candidate.Id)).ToList();
                var redrawn = TitlePicker.Pick(others, mode, WithSlots(style, keep.Count + 1), keep, previous, random);
                if (redrawn.Count <= keep.Count)
                {
                    break; // no eligible title is left
                }

                title = redrawn[^1];
                locked = null;
            }
        }

        return filled;
    }

    // A title that cannot give a slot is a null, whatever went wrong with it; only cancellation goes through.
    private async Task<SlotChoice?> TrySlotAsync(
        LibraryInfo library,
        IReadOnlyList<User> viewers,
        Guid title,
        CoverMode mode,
        CoverSlot? locked,
        double seconds,
        Random random,
        CancellationToken ct)
    {
        try
        {
            var choice = await _source.SlotAsync(library, viewers, title, mode, locked, seconds, random, ct)
                .ConfigureAwait(false);
            if (choice is null)
            {
                _log.LogDebug(
                    "Library Cover Maker: title {TitleId} of library {Library} gave no {Mode} slot",
                    title,
                    library.Name,
                    mode);
            }

            return choice;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Library Cover Maker: title {TitleId} of library {Library} could not be used",
                title,
                library.Name);
            return null;
        }
    }

    // Writes the overlay (and the name-only picture when there is no slot) into the temporary folder.
    private RenderPlan Plan(
        LibraryInfo library,
        CoverStyle style,
        PluginConfiguration config,
        Guid coverId,
        List<SlotInput> slots,
        List<string> temporary)
    {
        var width = Even(config.OutputWidth);
        var height = Even(config.OutputHeight);
        Directory.CreateDirectory(_store.TempFolder);

        var overlay = Path.Combine(_store.TempFolder, coverId.ToString("N") + "-overlay.png");
        temporary.Add(overlay);
        File.WriteAllBytes(overlay, _overlay.RenderPng(style, library.Name, width, height));

        if (slots.Count == 0)
        {
            var background = Path.Combine(_store.TempFolder, coverId.ToString("N") + "-name.png");
            temporary.Add(background);
            File.WriteAllBytes(background, NameOnlyPicture(width, height));
            slots.Add(new SlotInput(SlotKind.Image, background, 0, SlotLength(style, 1), IsHdr: false));
        }

        return new RenderPlan(
            slots,
            overlay,
            width,
            height,
            Fps,
            style.Fade,
            _store.AnimatedPath(library.Id, coverId),
            _store.StillPath(library.Id, coverId),
            _store.TempFolder);
    }

    private async Task<BuildResult> CommitAsync(
        LibraryInfo library,
        BuildReason reason,
        LibraryCoverState started,
        CoverRecord record,
        int slotCount,
        string? unlocked,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var discarded = false;
        string? error = null;
        using var run = _applier.StartApply(library.Id);
        try
        {
            await _store.UpdateAsync(
                library.Id,
                async (state, token) =>
                {
                    // An admin put another cover on the library while it rendered: their choice wins. A restore that
                    // breaks the rule is refused, and the renewal audit queues anything else that does.
                    if (state.CurrentCoverId != started.CurrentCoverId)
                    {
                        discarded = true;
                        return;
                    }

                    // Viewers and their limits as they are now, not as they were when the titles were chosen.
                    var viewers = _source.Viewers(library);
                    bool AllowedNow(Guid id) => _source.Access(library, id, viewers) == TitleAccess.Allowed;
                    var mustReplace = CoverRules.MustReplace(CoverRules.Current(state), AllowedNow);

                    // An admin pinned the library or changed its mode while it rendered: their choice wins, unless the
                    // cover it shows must be replaced anyway (decision 9). Then the new cover goes on, and the pin and
                    // the mode stay as the admin set them. After a switch of mode, that cover was rendered in the old
                    // mode: accepted, because the parental rule wins over the look, and the next build uses the new
                    // mode.
                    if ((state.Pinned != started.Pinned || state.Mode != started.Mode) && !mustReplace)
                    {
                        discarded = true;
                        Settle(state, unlocked);
                        return;
                    }

                    // A pinned library, or a manual one rebuilt for an audience change, is built only to replace a
                    // cover that breaks the rule; if it no longer does, the admin's cover stays.
                    var replacing = state.Pinned
                        || (state.Mode == CoverMode.Manual && reason == BuildReason.AudienceChanged);
                    if (replacing && !mustReplace)
                    {
                        discarded = true;
                        Settle(state, unlocked);
                        return;
                    }

                    var refused = record.TitleIds
                        .Select(id => _source.Access(library, id, viewers))
                        .Where(access => access != TitleAccess.Allowed)
                        .ToList();
                    if (refused.Count > 0)
                    {
                        error = refused.Contains(TitleAccess.Missing) ? GoneAtCommit : HiddenAtCommit;
                        Failed(state, error, mustReplace, unlocked);
                        return;
                    }

                    try
                    {
                        // The format as the admin has it now: the global switch may have flipped during the render.
                        await run.ApplyAsync(state, record, _config().PrimaryFormat, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(
                            ex,
                            "Library Cover Maker: the new cover of library {Library} could not be applied",
                            library.Name);
                        error = "The cover could not be applied: " + ex.Message;
                        Failed(state, error, mustReplace, unlocked);
                        return;
                    }

                    state.LastRenderedAt = DateTimeOffset.UtcNow;
                    state.LastError = null;
                    state.LastErrorAt = null;
                    if (mustReplace)
                    {
                        state.Notice = CoverRules.ReplacedNotice(state.Notice, unlocked);
                        state.NoticeAt = state.LastRenderedAt;
                    }
                    else if (CoverRules.IsPending(state.Notice))
                    {
                        // Queued for the rule, which the old cover no longer broke: only the freed locks are news.
                        state.Notice = unlocked;
                        state.NoticeAt = unlocked is null ? null : state.LastRenderedAt;
                    }

                    run.AddToHistory(state, record);
                },
                config.DefaultMode,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            run.Unsaved(ex);
            throw;
        }
        finally
        {
            if (!run.Applied)
            {
                TryDelete(_store.AnimatedPath(library.Id, record.Id));
                TryDelete(_store.StillPath(library.Id, record.Id));
            }
        }

        run.Saved();
        if (discarded)
        {
            _log.LogInformation(
                "Library Cover Maker: library {Library} was changed by an admin while its cover rendered, so the "
                + "render was dropped",
                library.Name);
        }
        else if (error is HiddenAtCommit or GoneAtCommit)
        {
            _log.LogWarning("Library Cover Maker: {Error} (library {Library})", error, library.Name);
        }

        return new BuildResult(run.Applied, error, slotCount);
    }

    // The card says why the build failed and, when the cover it was to replace breaks the rule, that it still does.
    private static void Failed(LibraryCoverState state, string error, bool mustReplace, string? unlocked)
    {
        state.LastError = error;
        state.LastErrorAt = DateTimeOffset.UtcNow;
        if (mustReplace)
        {
            state.Notice = CoverRules.NotReplacedNotice(state.Notice, unlocked);
            state.NoticeAt = state.LastErrorAt;
        }
    }

    // A build queued to replace the cover found that it no longer has to: the card stops saying it is queued.
    private static void Settle(LibraryCoverState state, string? unlocked)
    {
        if (CoverRules.IsPending(state.Notice))
        {
            state.Notice = CoverRules.SettledNotice(unlocked);
            state.NoticeAt = DateTimeOffset.UtcNow;
        }
    }

    private Task SettleAsync(
        Guid libraryId,
        Func<Guid, bool> allowed,
        string? unlocked,
        PluginConfiguration config,
        CancellationToken ct) =>
        _store.UpdateAsync(
            libraryId,
            (state, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (!CoverRules.MustReplace(CoverRules.Current(state), allowed))
                {
                    Settle(state, unlocked);
                }

                return Task.CompletedTask;
            },
            config.DefaultMode,
            ct);

    private Task RecordFailureAsync(
        Guid libraryId,
        string error,
        Func<Guid, bool> allowed,
        string? unlocked,
        PluginConfiguration config,
        CancellationToken ct) =>
        _store.UpdateAsync(
            libraryId,
            (state, token) =>
            {
                token.ThrowIfCancellationRequested();
                Failed(state, error, CoverRules.MustReplace(CoverRules.Current(state), allowed), unlocked);
                return Task.CompletedTask;
            },
            config.DefaultMode,
            ct);

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Library Cover Maker: could not delete {Path}", path);
        }
    }

    // One slot of the cover: the title, the mode and lock it was made with (to make it again at another length), and
    // what it shows.
    private sealed record Filled(Guid Title, CoverMode Mode, CoverSlot? Locked, SlotChoice Choice);
}

/// <summary>What the builder needs to know of a library.</summary>
/// <param name="Id">The library's item id.</param>
/// <param name="Name">The library's name, the cover's text when its style has none.</param>
/// <param name="CollectionType">
/// Jellyfin's type of the library (<c>movies</c>, <c>tvshows</c> and so on); null for a mixed library.
/// </param>
/// <param name="Enabled">Whether the library is switched on in Jellyfin.</param>
internal sealed record LibraryInfo(Guid Id, string Name, string? CollectionType, bool Enabled);

/// <summary>A slot and the item it really shows.</summary>
/// <param name="Input">What the renderer gets.</param>
/// <param name="ShownId">The episode a series slot shows; null when the slot shows the title itself.</param>
internal sealed record SlotChoice(SlotInput Input, Guid? ShownId = null);

/// <summary>Whether the viewers of a library may see an item.</summary>
internal enum TitleAccess
{
    /// <summary>Every viewer may see it, and it is in the library.</summary>
    Allowed,

    /// <summary>Some viewer may not see it under their parental controls.</summary>
    Hidden,

    /// <summary>It is no longer in the library (deleted, or moved to another one).</summary>
    Missing,
}

/// <summary>What the builder asks of Jellyfin about a library and its titles. The tests replace it.</summary>
internal interface ICoverSource
{
    /// <summary>The library, or null when Jellyfin no longer has it.</summary>
    LibraryInfo? Library(Guid libraryId);

    /// <summary>The users who can see the library, as they are now.</summary>
    IReadOnlyList<User> Viewers(LibraryInfo library);

    /// <summary>The library's titles that all <paramref name="viewers"/> may see, with what the picker ranks.</summary>
    IReadOnlyList<TitleCandidate> Candidates(LibraryInfo library, IReadOnlyList<User> viewers);

    /// <summary>Whether all <paramref name="viewers"/> may see an item (title or episode) of the library.</summary>
    TitleAccess Access(LibraryInfo library, Guid itemId, IReadOnlyList<User> viewers);

    /// <summary>A title's name, for a notice on the library's card.</summary>
    string TitleName(Guid titleId);

    /// <summary>
    /// The slot that shows <paramref name="titleId"/> in <paramref name="mode"/>, or null when it cannot give one.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="viewers">The library's viewers, for the episodes of a series.</param>
    /// <param name="titleId">The title.</param>
    /// <param name="mode">Art or scenes; in manual mode, what the lock or the title's artwork says.</param>
    /// <param name="locked">The admin's lock of the slot, with its moment or backdrop. Null for a free slot.</param>
    /// <param name="seconds">How long the slot lasts.</param>
    /// <param name="random">The source of the draws.</param>
    /// <param name="ct">Cancels the search for a scene.</param>
    Task<SlotChoice?> SlotAsync(
        LibraryInfo library,
        IReadOnlyList<User> viewers,
        Guid titleId,
        CoverMode mode,
        CoverSlot? locked,
        double seconds,
        Random random,
        CancellationToken ct);
}

/// <summary>A library's titles and their slots, through Jellyfin.</summary>
internal sealed class JellyfinCoverSource(
    ILibraryManager library,
    IUserDataManager userData,
    LibraryAudience audience,
    ArtworkSource artwork,
    SceneFinder scenes,
    ILogger log) : ICoverSource
{
    // How many series ids one query for their episodes carries.
    private const int SeriesPerQuery = 200;

    public LibraryInfo? Library(Guid libraryId)
    {
        if (library.GetItemById(libraryId) is not CollectionFolder folder)
        {
            return null;
        }

        // The collection types are named in lowercase (movies, tvshows), the way the rules compare them.
        var type = folder.CollectionType?.ToString();
        return new LibraryInfo(folder.Id, folder.Name, type, folder.GetLibraryOptions().Enabled);
    }

    public IReadOnlyList<User> Viewers(LibraryInfo info) => audience.UsersWhoSee(info.Id);

    public IReadOnlyList<TitleCandidate> Candidates(LibraryInfo info, IReadOnlyList<User> viewers)
    {
        var kinds = TitleKinds(info.CollectionType);
        var allowed = Query(info.Id, kinds)
            .Where(item => audience.AllowedForAll(item, viewers))
            .ToList();
        var plays = PlayCounts(allowed, viewers);

        log.LogDebug(
            "Library Cover Maker: library {Library} has {Allowed} titles that its {Users} viewers may all see",
            info.Name,
            allowed.Count,
            viewers.Count);

        return allowed
            .Select(item => new TitleCandidate(
                item.Id,
                plays.GetValueOrDefault(item.Id),
                item.CommunityRating,
                item.DateCreated,
                item.GetImages(ImageType.Backdrop).Any(image => image.IsLocalFile),
                CanGiveScenes(item)))
            .ToList();
    }

    /// <summary>
    /// Whether a title counts as having a video for scenes: a video file of a known length. One without a runtime
    /// (live TV, a recording still being written, a stream saved through a pipe) never gives a clip, so drawing it
    /// would only cost its slot a try. A series is taken to have one: loading every episode of a large library to
    /// check is too costly, and a series with no such episode just gives no slot and is replaced.
    /// </summary>
    internal static bool CanGiveScenes(BaseItem item) =>
        item is Series || (item is Video video && HasFile(video) && HasLength(video));

    public TitleAccess Access(LibraryInfo info, Guid itemId, IReadOnlyList<User> viewers) =>
        AccessOf(library, audience, info.Id, itemId, viewers);

    /// <summary>
    /// Whether all <paramref name="viewers"/> may see an item (title or episode) of the library: the rule every
    /// check of a cover's titles uses, in the build, in a restore and in the renewal audit.
    /// </summary>
    internal static TitleAccess AccessOf(
        ILibraryManager library,
        LibraryAudience audience,
        Guid libraryId,
        Guid itemId,
        IReadOnlyList<User> viewers)
    {
        var item = library.GetItemById(itemId);
        if (item is null || !library.GetCollectionFolders(item).Exists(folder => folder.Id == libraryId))
        {
            return TitleAccess.Missing;
        }

        return audience.AllowedForAll(item, viewers) ? TitleAccess.Allowed : TitleAccess.Hidden;
    }

    public string TitleName(Guid titleId) => library.GetItemById(titleId)?.Name ?? "a title no longer in the library";

    public async Task<SlotChoice?> SlotAsync(
        LibraryInfo info,
        IReadOnlyList<User> viewers,
        Guid titleId,
        CoverMode mode,
        CoverSlot? locked,
        double seconds,
        Random random,
        CancellationToken ct)
    {
        var title = library.GetItemById(titleId);
        if (title is null)
        {
            return null;
        }

        if (UsesArt(title, mode, locked))
        {
            var art = artwork.ForTitle(title, locked?.BackdropIndex, seconds, random);
            return art is null ? null : new SlotChoice(art);
        }

        var video = title switch
        {
            Series series => EpisodeOf(series, viewers),
            Video single => single,
            _ => null,
        };
        if (video is null || !IsOnDisk(video))
        {
            return null;
        }

        var shown = video.Id == title.Id ? (Guid?)null : video.Id;
        var hdr = IsHdr(Probe(video));
        if (locked?.MomentSeconds is double moment)
        {
            var clip = await CoverBuilder.LockedClipAsync(video, moment, seconds, hdr, scenes.CropAtAsync, ct)
                .ConfigureAwait(false);
            return new SlotChoice(clip, shown);
        }

        var choice = await scenes.FindStartAsync(video, seconds, ct).ConfigureAwait(false);
        return choice is null
            ? null
            : new SlotChoice(
                new SlotInput(SlotKind.Video, video.Path, choice.StartSeconds, seconds, hdr, choice.Crop),
                shown);
    }

    /// <summary>The item types that are a library's titles: series for tvshows, movies or videos otherwise.</summary>
    internal static BaseItemKind[] TitleKinds(string? collectionType) => collectionType switch
    {
        "tvshows" => [BaseItemKind.Series],
        "musicvideos" => [BaseItemKind.MusicVideo],
        "homevideos" => [BaseItemKind.Video, BaseItemKind.Movie],
        _ => [BaseItemKind.Movie],
    };

    /// <summary>
    /// The episode a series' scene comes from: the first usable episode of season one, so that a cover does not spoil
    /// later seasons (spec 7.2), else of the next season that has one, and specials last.
    /// </summary>
    internal static Video? FirstEpisode(IEnumerable<Video> episodes, Func<Video, bool> usable) =>
        episodes
            .OrderBy(episode => SeasonRank(episode.ParentIndexNumber))
            .ThenBy(episode => episode.IndexNumber ?? int.MaxValue)
            .ThenBy(episode => episode.Id)
            .FirstOrDefault(usable);

    /// <summary>Whether a video stream is HDR and must be tone mapped, by Jellyfin's own classification.</summary>
    internal static bool IsHdr(MediaStream? stream) => stream?.VideoRange == VideoRange.HDR;

    private static int SeasonRank(int? season) => season switch
    {
        1 => 0,
        > 1 => season.Value,
        _ => int.MaxValue, // specials and episodes without a season
    };

    // In manual mode the lock says which kind of slot it is; a free slot uses the title's artwork when it has some.
    private static bool UsesArt(BaseItem title, CoverMode mode, CoverSlot? locked) => mode switch
    {
        CoverMode.Art => true,
        CoverMode.Scenes => false,
        _ => locked?.BackdropIndex is not null
            || (locked?.MomentSeconds is null && title.GetImages(ImageType.Backdrop).Any(image => image.IsLocalFile)),
    };

    private static bool HasFile(Video video) =>
        !video.IsPlaceHolder && !string.IsNullOrEmpty(video.Path) && Path.IsPathRooted(video.Path);

    private static bool IsOnDisk(Video video) => HasFile(video) && File.Exists(video.Path);

    // The scene finder needs the length to place a clip; without it a video gives none.
    private static bool HasLength(Video video) => video.RunTimeTicks is > 0;

    // What Jellyfin's probe says of the video stream; nothing without the media source manager (the tests).
    private static MediaStream? Probe(Video video) =>
        BaseItem.MediaSourceManager is null ? null : video.GetDefaultVideoStream();

    private IReadOnlyList<BaseItem> Query(Guid ancestorId, BaseItemKind[] kinds) => Query([ancestorId], kinds);

    private IReadOnlyList<BaseItem> Query(Guid[] ancestorIds, BaseItemKind[] kinds) =>
        library.GetItemList(new InternalItemsQuery
        {
            AncestorIds = ancestorIds,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = kinds,
        });

    /// <summary>
    /// Adds one viewer's plays to <paramref name="plays"/>: every item the viewer has started counts with its play
    /// count, finished or not (spec 7.2: plays summed over all users), and an episode's plays count for its series.
    /// </summary>
    internal static void AddPlays(
        Dictionary<Guid, int> plays,
        IReadOnlyList<BaseItem> items,
        IReadOnlyDictionary<Guid, UserItemData> data)
    {
        foreach (var item in items)
        {
            if (data.TryGetValue(item.Id, out var userItem) && userItem.PlayCount > 0)
            {
                var title = item is Episode episode ? episode.SeriesId : item.Id;
                plays[title] = plays.GetValueOrDefault(title) + userItem.PlayCount;
            }
        }
    }

    // Plays summed over the library's viewers, for the allowed titles. For series, the episodes of the allowed series
    // only are loaded (in batches of series), once for all viewers.
    private Dictionary<Guid, int> PlayCounts(List<BaseItem> allowed, IReadOnlyList<User> viewers)
    {
        var plays = new Dictionary<Guid, int>();
        if (allowed.Count == 0 || viewers.Count == 0)
        {
            return plays;
        }

        var series = allowed.Where(item => item is Series).Select(item => item.Id).ToList();
        var counted = new List<BaseItem>(allowed.Where(item => item is not Series));
        foreach (var batch in series.Chunk(SeriesPerQuery))
        {
            counted.AddRange(Query(batch, [BaseItemKind.Episode]));
        }

        if (counted.Count == 0)
        {
            return plays;
        }

        foreach (var user in viewers)
        {
            AddPlays(plays, counted, userData.GetUserDataBatch(counted, user));
        }

        return plays;
    }

    /// <summary>
    /// The episode a series' scene comes from: the first on disk, of a known length, that every one of
    /// <paramref name="viewers"/> may see (<see cref="FirstEpisode"/>). The build and the admin API's slot checks and
    /// frame previews all ask this.
    /// </summary>
    internal static Video? EpisodeFor(
        ILibraryManager library,
        LibraryAudience audience,
        Series series,
        IReadOnlyList<User> viewers)
    {
        var episodes = library.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Episode],
        }).OfType<Video>();
        return FirstEpisode(
            episodes,
            episode => IsOnDisk(episode) && HasLength(episode) && audience.AllowedForAll(episode, viewers));
    }

    private Video? EpisodeOf(Series series, IReadOnlyList<User> viewers) =>
        EpisodeFor(library, audience, series, viewers);
}
