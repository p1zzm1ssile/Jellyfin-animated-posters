using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LibraryCoverMaker.Tests;

public sealed class CoverStoreTests : IDisposable
{
    private const string UnreadableNotice =
        "Settings for this library could not be read and were reset; the current cover is kept until you choose.";

    // How long a test waits for something that should be quick before it calls it stuck.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static readonly Guid Library = Guid.Parse("0f3c5d7e-1a2b-4c6d-8e9f-a0b1c2d3e4f5");

    private readonly string _root = Directory.CreateTempSubdirectory("cover-store-tests-").FullName;
    private readonly Paths _paths;
    private readonly CoverStore _store;

    public CoverStoreTests()
    {
        _paths = new Paths(_root);
        _store = new CoverStore(_paths, NullLogger<CoverStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string DataFolder => Path.Combine(_root, "data", "library-cover-maker");

    private string LibraryFolder(Guid library) => Path.Combine(DataFolder, library.ToString("N"));

    private string StatePath(Guid library) => Path.Combine(LibraryFolder(library), "state.json");

    [Fact]
    public void Load_returns_defaults_for_unknown_library()
    {
        var state = _store.Load(Library);

        Assert.Equal(Library, state.LibraryId);
        Assert.Equal(CoverMode.Art, state.Mode);
        Assert.False(state.Pinned);
        Assert.Equal(5, state.Slots.Length);
        Assert.Equal(5, state.Slots.Distinct().Count());
        Assert.All(state.Slots, slot =>
        {
            Assert.False(slot.Locked);
            Assert.Null(slot.ItemId);
            Assert.Null(slot.MomentSeconds);
            Assert.Null(slot.BackdropIndex);
        });
        Assert.Null(state.CurrentCoverId);
        Assert.Null(state.Original);
        Assert.Empty(state.History);
        Assert.Null(state.RenewEveryDays);
        Assert.Null(state.LastRenderedAt);
        Assert.Null(state.LastError);
        Assert.Null(state.LastErrorAt);
        Assert.Null(state.Notice);
        Assert.Null(state.NoticeAt);
        Assert.Null(state.AppliedPrimaryPath);
        Assert.Null(state.AppliedPrimaryModified);

        var style = state.Style;
        Assert.Equal(string.Empty, style.Title);
        Assert.Equal("Bodoni Moda", style.Font);
        Assert.Equal(120, style.TextSize);
        Assert.Equal(15, style.Darken);
        Assert.True(style.Frame);
        Assert.True(style.Shadow);
        Assert.Equal(6, style.LengthSeconds);
        Assert.True(style.Fade);
        Assert.Equal(5, style.SlotCount);
        Assert.True(style.UseMostWatched);
        Assert.True(style.UseTopRated);
        Assert.True(style.UseRecent);

        // Asking about a library must not leave anything behind.
        Assert.False(Directory.Exists(DataFolder));
    }

    [Fact]
    public void New_library_uses_the_given_default_mode()
    {
        Assert.Equal(CoverMode.Scenes, _store.Load(Library, CoverMode.Scenes).Mode);

        // The default mode is only for a library with no state file: a saved state keeps its own mode.
        _store.Save(_store.Load(Library));
        Assert.Equal(CoverMode.Art, _store.Load(Library, CoverMode.Scenes).Mode);

        // A state file that cannot be read does not get it either.
        var unreadable = Guid.NewGuid();
        Directory.CreateDirectory(LibraryFolder(unreadable));
        File.WriteAllText(StatePath(unreadable), "{ \"Mode\": ");
        Assert.Equal(CoverMode.Art, _store.Load(unreadable, CoverMode.Scenes).Mode);
    }

    [Fact]
    public void Slot_is_locked_exactly_when_it_has_a_title()
    {
        Assert.False(new CoverSlot().Locked);
        Assert.True(new CoverSlot { ItemId = Guid.NewGuid() }.Locked);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var saved = EveryFieldSet(Library);

        _store.Save(saved);

        AssertSame(saved, _store.Load(Library));

        // The common state of a new library, where the optional fields are all empty.
        var fresh = _store.Load(Guid.NewGuid());
        _store.Save(fresh);
        AssertSame(fresh, _store.Load(fresh.LibraryId));
    }

    [Fact]
    public void Save_leaves_no_temp_file()
    {
        var state = _store.Load(Library);
        _store.Save(state);
        state.Pinned = true;
        _store.Save(state);

        Assert.Equal(new[] { StatePath(Library) }, Directory.GetFiles(LibraryFolder(Library)));
        Assert.Empty(Directory.GetFiles(DataFolder, "*.tmp", SearchOption.AllDirectories));
        Assert.True(_store.Load(Library).Pinned);
    }

    [Fact]
    public void Failed_save_removes_its_temp_file()
    {
        var state = _store.Load(Library);

        // A folder where the file belongs: the move that ends Save fails after the temp file was written.
        Directory.CreateDirectory(StatePath(Library));

        var failure = Record.Exception(() => _store.Save(state));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"Save failed with: {failure}");
        Assert.Empty(Directory.GetFiles(LibraryFolder(Library)));
    }

    [Fact]
    public void Save_without_a_state_is_refused() => Assert.Throws<ArgumentNullException>(() => _store.Save(null!));

    [Fact]
    public void Save_is_not_part_of_the_public_surface()
    {
        // From outside the plugin only Update and UpdateAsync write a state, because they alone take the library's turn first.
        Assert.DoesNotContain(typeof(CoverStore).GetMethods(), method => method.Name == "Save");
    }

    [Fact]
    public void Save_refuses_a_state_that_Load_would_not_accept()
    {
        var damage = new Dictionary<string, Action<LibraryCoverState>>
        {
            ["no slots"] = state => state.Slots = Array.Empty<CoverSlot>(),
            ["four slots"] = state => state.Slots = state.Slots[..4],
            ["six slots"] = state => state.Slots = state.Slots.Append(new CoverSlot()).ToArray(),
            ["a missing slot"] = state => state.Slots[2] = null!,
            ["slots null"] = state => state.Slots = null!,
            ["a missing history entry"] = state => state.History.Add(null!),
            ["history null"] = state => state.History = null!,
            ["style null"] = state => state.Style = null!,
            ["title ids null"] = state => state.History.Add(new CoverRecord { TitleIds = null! }),
        };

        foreach (var (what, apply) in damage)
        {
            var state = _store.Load(Library);
            apply(state);

            Assert.True(Record.Exception(() => _store.Save(state)) is not null, what);
            Assert.False(File.Exists(StatePath(Library)), what);
        }
    }

    [Fact]
    public void Enums_are_stored_as_names()
    {
        var state = _store.Load(Library);
        state.History.Add(new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Uploaded, Mode = CoverMode.Manual });

        _store.Save(state);

        var text = File.ReadAllText(StatePath(Library));
        Assert.Contains("\"Art\"", text);
        using var json = JsonDocument.Parse(text);
        var mode = json.RootElement.GetProperty("Mode");
        Assert.Equal(JsonValueKind.String, mode.ValueKind);
        Assert.Equal("Art", mode.GetString());
        var entry = json.RootElement.GetProperty("History")[0];
        Assert.Equal("Uploaded", entry.GetProperty("Origin").GetString());
        Assert.Equal("Manual", entry.GetProperty("Mode").GetString());
    }

    [Fact]
    public void Cover_files_live_under_the_library_folder_and_renders_in_progress_under_tmp()
    {
        var cover = Guid.NewGuid();
        var covers = Path.Combine(DataFolder, Library.ToString("N"), "covers");

        Assert.Equal(Path.Combine(covers, cover.ToString("N") + ".webp"), _store.AnimatedPath(Library, cover));
        Assert.Equal(Path.Combine(covers, cover.ToString("N") + ".jpg"), _store.StillPath(Library, cover));
        Assert.Equal(Path.Combine(DataFolder, "tmp"), _store.TempFolder);
    }

    [Fact]
    public void AddToHistory_keeps_newest_8_and_deletes_dropped_files()
    {
        var state = _store.Load(Library);

        // Added oldest first, each a day newer than the one before.
        var records = Enumerable.Range(0, 10).Select(i => RecordWithFiles(Library, Day(i))).ToList();

        foreach (var record in records)
        {
            _store.AddToHistory(state, record, 8);
        }

        Assert.Equal(records.Skip(2).Reverse().Select(r => r.Id), state.History.Select(r => r.Id));
        AssertNewestFirst(state);
        foreach (var dropped in records.Take(2))
        {
            AssertFilesDeleted(Library, dropped);
        }

        foreach (var kept in records.Skip(2))
        {
            AssertFilesKept(Library, kept);
        }
    }

    [Fact]
    public void AddToHistory_never_drops_Original()
    {
        var state = _store.Load(Library);
        var original = RecordWithFiles(Library, Day(-400), CoverOrigin.Original);
        state.Original = original;

        for (var i = 0; i < 10; i++)
        {
            _store.AddToHistory(state, RecordWithFiles(Library, Day(i)), 3);
        }

        Assert.Equal(3, state.History.Count);
        Assert.DoesNotContain(state.History, r => r.Id == original.Id);
        Assert.Same(original, state.Original);
        AssertFilesKept(Library, original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddToHistory_always_keeps_the_cover_just_added(int historySize)
    {
        var state = _store.Load(Library);
        var earlier = RecordWithFiles(Library, Day(0));
        var added = RecordWithFiles(Library, Day(1));
        _store.AddToHistory(state, earlier, 8);

        _store.AddToHistory(state, added, historySize);

        Assert.Equal(new[] { added.Id }, state.History.Select(r => r.Id));
        AssertFilesKept(Library, added);
        AssertFilesDeleted(Library, earlier);
    }

    [Fact]
    public void AddToHistory_without_a_state_or_a_cover_is_refused()
    {
        var state = _store.Load(Library);

        Assert.Throws<ArgumentNullException>(() => _store.AddToHistory(null!, new CoverRecord(), 8));
        Assert.Throws<ArgumentNullException>(() => _store.AddToHistory(state, null!, 8));
        Assert.Empty(state.History);
    }

    [Fact]
    public void Re_adding_a_cover_moves_it_to_the_front_without_duplicates()
    {
        var state = _store.Load(Library);
        var oldest = RecordWithFiles(Library, Day(0));
        var middle = RecordWithFiles(Library, Day(1));
        var newest = RecordWithFiles(Library, Day(2));
        foreach (var cover in new[] { oldest, middle, newest })
        {
            _store.AddToHistory(state, cover, 8);
        }

        // A cover from the middle of the history comes back, for example restored by an admin: it is listed once, at the front.
        var again = new CoverRecord { Id = middle.Id, CreatedAt = Day(3), Origin = middle.Origin, Mode = middle.Mode };
        _store.AddToHistory(state, again, 8);

        Assert.Equal(new[] { middle.Id, newest.Id, oldest.Id }, state.History.Select(r => r.Id));
        Assert.Same(again, state.History[0]);
        AssertNewestFirst(state);

        // When the history is full and its oldest cover comes back (as a record of its own, dated a day later than
        // all the others), nothing is dropped and no file is lost.
        var oldestAgain = new CoverRecord { Id = oldest.Id, CreatedAt = Day(4), Origin = oldest.Origin, Mode = oldest.Mode };
        _store.AddToHistory(state, oldestAgain, 3);

        Assert.Equal(new[] { oldest.Id, middle.Id, newest.Id }, state.History.Select(r => r.Id));
        AssertNewestFirst(state);
        AssertFilesKept(Library, oldest);
        AssertFilesKept(Library, middle);
        AssertFilesKept(Library, newest);
    }

    [Fact]
    public void Files_of_the_current_cover_are_never_deleted()
    {
        var state = _store.Load(Library);
        var current = RecordWithFiles(Library, Day(0));
        foreach (var cover in new[] { current, RecordWithFiles(Library, Day(1)), RecordWithFiles(Library, Day(2)) })
        {
            _store.AddToHistory(state, cover, 3);
        }

        // An old cover was restored and pinned, so the oldest entry of the history is the one on the library.
        state.CurrentCoverId = current.Id;

        _store.AddToHistory(state, RecordWithFiles(Library, Day(3)), 3);

        Assert.DoesNotContain(state.History, r => r.Id == current.Id);
        AssertFilesKept(Library, current);
    }

    [Fact]
    public void Files_still_used_by_the_original_are_never_deleted()
    {
        var state = _store.Load(Library);
        var original = RecordWithFiles(Library, Day(0), CoverOrigin.Original);
        state.Original = original;

        // The original is listed in the history too, as when it is restored, and then falls off the end.
        _store.AddToHistory(state, original, 2);
        _store.AddToHistory(state, RecordWithFiles(Library, Day(1)), 2);
        _store.AddToHistory(state, RecordWithFiles(Library, Day(2)), 2);

        Assert.DoesNotContain(state.History, r => r.Id == original.Id);
        AssertFilesKept(Library, original);
    }

    [Fact]
    public void Files_of_a_cover_listed_twice_are_kept_while_one_entry_remains()
    {
        var state = _store.Load(Library);
        var twice = RecordWithFiles(Library, Day(0));
        state.History.Add(twice);
        state.History.Add(RecordWithFiles(Library, Day(1)));
        state.History.Add(new CoverRecord { Id = twice.Id, CreatedAt = twice.CreatedAt }); // as a hand-edited file can list it

        _store.AddToHistory(state, RecordWithFiles(Library, Day(2)), 3);

        Assert.Equal(3, state.History.Count);
        Assert.Contains(state.History, r => r.Id == twice.Id);
        AssertFilesKept(Library, twice);
    }

    [Fact]
    public void Corrupt_state_file_loads_defaults_and_keeps_a_copy()
    {
        var log = new RecordingLogger();
        var store = new CoverStore(_paths, log);
        var corrupt = "{ \"Mode\": ";
        Directory.CreateDirectory(LibraryFolder(Library));
        File.WriteAllText(StatePath(Library), corrupt);

        var before = DateTimeOffset.UtcNow;
        var state = store.Load(Library);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(Library, state.LibraryId);
        Assert.Equal(CoverMode.Art, state.Mode);
        Assert.Equal(5, state.Slots.Length);
        Assert.Empty(state.History);

        // Settings that cannot be read must never let a schedule replace the cover: the library is pinned, and says why.
        Assert.True(state.Pinned);
        Assert.Equal(UnreadableNotice, state.Notice);
        Assert.NotNull(state.NoticeAt);
        Assert.InRange(state.NoticeAt.Value, before.AddMinutes(-1), after.AddMinutes(1)); // now, give or take a clock adjustment

        Assert.Equal(corrupt, File.ReadAllText(StatePath(Library) + ".bad"));
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);

        // The pinned state is saved at once, so the next load reads it: no new warning, and the copy stays as it is.
        File.WriteAllText(StatePath(Library) + ".bad", "kept as it is");
        var again = store.Load(Library);
        Assert.True(again.Pinned);
        Assert.Equal(UnreadableNotice, again.Notice);
        Assert.Equal(state.NoticeAt, again.NoticeAt);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("kept as it is", File.ReadAllText(StatePath(Library) + ".bad"));

        // A file that goes bad later is a new case: a new warning, and its own copy replaces the earlier one.
        File.WriteAllText(StatePath(Library), "not json at all");
        store.Load(Library);
        Assert.Equal("not json at all", File.ReadAllText(StatePath(Library) + ".bad"));
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"Mode\": \"Sideways\" }")]
    [InlineData("{ \"Mode\": 1 }")]
    [InlineData("{ \"Style\": null }")]
    [InlineData("{ \"Slots\": null }")]
    [InlineData("{ \"History\": null }")]
    [InlineData("{ \"Slots\": [ {}, {}, {} ] }")]
    [InlineData("{ \"Slots\": [ {}, {}, {}, {}, {}, {} ] }")]
    [InlineData("{ \"Slots\": [ null, {}, {}, {}, {} ] }")]
    [InlineData("{ \"History\": [ null ] }")]
    [InlineData("{ \"History\": [ { \"TitleIds\": null } ] }")]
    public void A_state_file_that_is_not_a_state_is_treated_as_corrupt(string contents)
    {
        Directory.CreateDirectory(LibraryFolder(Library));
        File.WriteAllText(StatePath(Library), contents);

        var state = _store.Load(Library);

        Assert.Equal(CoverMode.Art, state.Mode);
        Assert.True(state.Pinned);
        Assert.Equal(UnreadableNotice, state.Notice);
        Assert.Equal(contents, File.ReadAllText(StatePath(Library) + ".bad"));
    }

    [Fact]
    public void File_with_another_library_id_loads_with_the_requested_id()
    {
        var other = Guid.NewGuid();
        _store.Save(EveryFieldSet(other));
        Directory.CreateDirectory(LibraryFolder(Library));
        File.Copy(StatePath(other), StatePath(Library)); // a folder copied by hand, say

        var loaded = _store.Load(Library);

        Assert.Equal(Library, loaded.LibraryId);
        Assert.True(loaded.Pinned); // the rest of the file is kept
        Assert.Equal("Cinzel", loaded.Style.Font);
        Assert.False(File.Exists(StatePath(Library) + ".bad"));

        // Saving it writes to its own folder, not to the library the file came from.
        var untouched = File.ReadAllBytes(StatePath(other));
        loaded.Pinned = false;
        _store.Save(loaded);
        Assert.Equal(untouched, File.ReadAllBytes(StatePath(other)));
        Assert.False(_store.Load(Library).Pinned);
    }

    [Fact]
    public void Forget_removes_the_library_folder()
    {
        var other = Guid.NewGuid();
        var cover = SaveWithACover(Library);
        SaveWithACover(other);

        _store.Forget(Library);

        Assert.False(Directory.Exists(LibraryFolder(Library)));
        Assert.False(File.Exists(_store.AnimatedPath(Library, cover.Id)));
        Assert.Empty(_store.Load(Library).History);
        Assert.True(File.Exists(StatePath(other)));
    }

    [Fact]
    public void Forget_ignores_a_library_that_has_no_folder()
    {
        Assert.Null(Record.Exception(() => _store.Forget(Library)));

        _store.Save(_store.Load(Guid.NewGuid()));
        Assert.Null(Record.Exception(() => _store.Forget(Library)));
    }

    [Fact]
    public void KnownLibraries_lists_the_libraries_that_have_a_folder()
    {
        Assert.Empty(_store.KnownLibraries());

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _store.Save(_store.Load(first));
        _store.Save(_store.Load(second));
        Directory.CreateDirectory(_store.TempFolder);
        Directory.CreateDirectory(Path.Combine(DataFolder, "not-a-library"));

        Assert.Equal(new[] { first, second }.OrderBy(id => id), _store.KnownLibraries().OrderBy(id => id));
    }

    [Fact]
    public void Saving_and_loading_at_the_same_time_never_clash()
    {
        _store.Save(EveryFieldSet(Library));
        var failures = new ConcurrentBag<Exception>();

        Parallel.For(0, 200, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    var state = EveryFieldSet(Library);
                    state.RenewEveryDays = i;
                    _store.Save(state);
                }
                else
                {
                    // The defaults would mean the file was read half written.
                    Assert.Equal("Cinzel", _store.Load(Library).Style.Font);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
        Assert.Equal("Cinzel", _store.Load(Library).Style.Font);
    }

    [Fact]
    public async Task Update_saves_the_change_and_returns_the_saved_state()
    {
        var fresh = Guid.NewGuid();

        // A library with no state file starts from the default mode that is passed.
        var created = _store.Update(fresh, state => state.Pinned = true, CoverMode.Scenes);
        Assert.Equal(CoverMode.Scenes, created.Mode);
        Assert.True(created.Pinned);
        AssertSame(created, _store.Load(fresh));

        // A saved state keeps its own mode, and a change that awaits is saved too, with the caller's token to hand.
        var cover = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var received = CancellationToken.None;
        var changed = await _store.UpdateAsync(
            fresh,
            async (state, token) =>
            {
                received = token;
                await Task.Yield();
                state.History.Add(new CoverRecord { Id = cover });
            },
            cancellationToken: cancellation.Token).WaitAsync(Patience);
        Assert.Equal(CoverMode.Scenes, changed.Mode);
        Assert.Equal(new[] { cover }, changed.History.Select(r => r.Id));
        Assert.Equal(cancellation.Token, received);
        AssertSame(changed, _store.Load(fresh));

        // The asynchronous form starts a new library from the default mode that is passed as well.
        var freshToo = await _store.UpdateAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CoverMode.Manual).WaitAsync(Patience);
        Assert.Equal(CoverMode.Manual, freshToo.Mode);
        Assert.Equal(CoverMode.Manual, _store.Load(freshToo.LibraryId).Mode);
    }

    [Fact]
    public async Task Parallel_updates_lose_nothing()
    {
        var added = Enumerable.Range(0, 80).Select(_ => Guid.NewGuid()).ToList();
        var updates = new List<Task>();

        for (var i = 0; i < added.Count; i++)
        {
            var id = added[i];
            if (i % 2 == 0)
            {
                // A thread of its own for each blocking call, so that waiting for a turn cannot starve the awaiting ones.
                updates.Add(Task.Factory.StartNew(
                    () => _store.Update(Library, state => state.History.Add(new CoverRecord { Id = id })),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));
            }
            else
            {
                updates.Add(_store.UpdateAsync(Library, async (state, _) =>
                {
                    await Task.Yield(); // the change spans an await, as applying an image will
                    state.History.Add(new CoverRecord { Id = id });
                }));
            }
        }

        await Task.WhenAll(updates).WaitAsync(Patience);

        Assert.Equal(added.OrderBy(id => id), _store.Load(Library).History.Select(r => r.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task Failed_update_saves_nothing()
    {
        _store.Save(EveryFieldSet(Library));
        var before = File.ReadAllBytes(StatePath(Library));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.UpdateAsync(Library, async (state, _) =>
        {
            state.Pinned = false;
            state.Notice = "never saved";
            await Task.Yield();
            throw new InvalidOperationException("the change failed");
        }).WaitAsync(Patience));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => _store.Update(Library, state =>
        {
            state.Notice = "never saved";
            throw new InvalidOperationException("the change failed");
        })).WaitAsync(Patience));

        Assert.Equal(before, File.ReadAllBytes(StatePath(Library)));
        Assert.Empty(Directory.GetFiles(LibraryFolder(Library), "*.tmp"));

        // A library with no state file gets none.
        var fresh = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => _store.Update(fresh, _ => throw new InvalidOperationException("the change failed")));
        Assert.False(Directory.Exists(LibraryFolder(fresh)));

        // And the library is not left locked: the next update runs.
        var next = await Task.Run(() => _store.Update(Library, state => state.RenewEveryDays = 21)).WaitAsync(Patience);
        Assert.Equal(21, next.RenewEveryDays);
    }

    [Fact]
    public async Task Update_without_a_change_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => _store.Update(Library, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.UpdateAsync(Library, null!));

        Assert.False(Directory.Exists(LibraryFolder(Library)));
    }

    [Fact]
    public async Task Update_always_saves_the_library_it_was_asked_for()
    {
        var other = Guid.NewGuid();
        _store.Save(_store.Load(other));
        var untouched = File.ReadAllBytes(StatePath(other));

        // A change that alters the id must not make the save land in another library's file.
        var viaUpdate = _store.Update(Library, state => state.LibraryId = other);
        var viaUpdateAsync = await _store.UpdateAsync(Library, (state, _) =>
        {
            state.LibraryId = other;
            return Task.CompletedTask;
        }).WaitAsync(Patience);

        Assert.Equal(Library, viaUpdate.LibraryId);
        Assert.Equal(Library, viaUpdateAsync.LibraryId);
        Assert.True(File.Exists(StatePath(Library)));
        Assert.Equal(untouched, File.ReadAllBytes(StatePath(other)));
    }

    [Fact]
    public async Task Updates_of_different_libraries_do_not_wait_for_each_other()
    {
        var other = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // An update that stays inside its change until the test lets it go.
        var held = _store.UpdateAsync(Library, async (state, _) =>
        {
            entered.SetResult();
            await release.Task;
            state.Pinned = true;
        });
        await entered.Task.WaitAsync(Patience);

        // Another library is not held up by it...
        var updated = await Task.Run(() => _store.Update(other, state => state.RenewEveryDays = 9)).WaitAsync(Patience);
        Assert.Equal(9, updated.RenewEveryDays);

        // ...but a second update of the same library waits for its turn.
        var queued = Task.Run(() => _store.Update(Library, state => state.RenewEveryDays = 3));
        await Task.Delay(200);
        Assert.False(queued.IsCompleted);

        release.SetResult();
        await held.WaitAsync(Patience);
        await queued.WaitAsync(Patience);

        var final = _store.Load(Library);
        Assert.True(final.Pinned);
        Assert.Equal(3, final.RenewEveryDays);
    }

    [Fact]
    public async Task Update_waiting_for_its_turn_can_be_cancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = _store.UpdateAsync(Library, async (state, _) =>
        {
            entered.SetResult();
            await release.Task;
            state.Pinned = true;
        });
        await entered.Task.WaitAsync(Patience);

        var ran = false;
        using var cancellation = new CancellationTokenSource();
        var cancelled = _store.UpdateAsync(
            Library,
            (_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));
        Assert.False(ran);

        // Giving up must not hand out the turn that the first update still holds.
        var next = _store.UpdateAsync(Library, (state, _) =>
        {
            state.RenewEveryDays = 5;
            return Task.CompletedTask;
        });
        await Task.Delay(200);
        Assert.False(next.IsCompleted);

        release.SetResult();
        await held.WaitAsync(Patience);
        var final = await next.WaitAsync(Patience);
        Assert.True(final.Pinned);
        Assert.Equal(5, final.RenewEveryDays);
    }

    // The n-th day of a made-up month, so that a later day is a later date.
    private static DateTimeOffset Day(int n) => new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero).AddDays(n);

    // A record with both of its files on disk, as the renderer leaves it.
    private CoverRecord RecordWithFiles(Guid library, DateTimeOffset createdAt, CoverOrigin origin = CoverOrigin.Generated)
    {
        var record = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            Origin = origin,
            Mode = CoverMode.Art,
            TitleIds = { Guid.NewGuid() },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AnimatedPath(library, record.Id))!);
        File.WriteAllText(_store.AnimatedPath(library, record.Id), "animated");
        File.WriteAllText(_store.StillPath(library, record.Id), "still");
        return record;
    }

    // The history is newest first by position, and in these tests by date as well.
    private static void AssertNewestFirst(LibraryCoverState state) =>
        Assert.Equal(state.History.OrderByDescending(r => r.CreatedAt).Select(r => r.Id), state.History.Select(r => r.Id));

    private void AssertFilesKept(Guid library, CoverRecord cover)
    {
        Assert.True(File.Exists(_store.AnimatedPath(library, cover.Id)));
        Assert.True(File.Exists(_store.StillPath(library, cover.Id)));
    }

    private void AssertFilesDeleted(Guid library, CoverRecord cover)
    {
        Assert.False(File.Exists(_store.AnimatedPath(library, cover.Id)));
        Assert.False(File.Exists(_store.StillPath(library, cover.Id)));
    }

    private CoverRecord SaveWithACover(Guid library)
    {
        var state = _store.Load(library);
        var cover = RecordWithFiles(library, Day(0));
        _store.AddToHistory(state, cover, 8);
        _store.Save(state);
        return cover;
    }

    // Every field away from its default, so that a field that is not stored shows up as a difference.
    private static LibraryCoverState EveryFieldSet(Guid library)
    {
        var state = new LibraryCoverState
        {
            LibraryId = library,
            Mode = CoverMode.Scenes,
            Pinned = true,
            Style = new CoverStyle
            {
                Title = "Movies",
                Font = "Cinzel",
                TextSize = 90,
                Darken = 40,
                Frame = false,
                Shadow = false,
                LengthSeconds = 9,
                Fade = false,
                SlotCount = 3,
                UseMostWatched = false,
                UseTopRated = false,
                UseRecent = false,
            },
            Original = new CoverRecord
            {
                Id = Guid.NewGuid(),
                CreatedAt = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.FromHours(-3)),
                Origin = CoverOrigin.Original,
                Mode = CoverMode.Art,
            },
            RenewEveryDays = 14,
            LastRenderedAt = new DateTimeOffset(2026, 10, 11, 3, 0, 7, TimeSpan.Zero),
            LastError = "No title has a backdrop.",
            LastErrorAt = new DateTimeOffset(2026, 10, 4, 3, 0, 9, TimeSpan.Zero),
            Notice = "Replaced because a user may not see Alien.",
            NoticeAt = new DateTimeOffset(2026, 10, 12, 18, 45, 0, TimeSpan.Zero),
            AppliedPrimaryPath = "/config/root/default/Movies/folder.webp",
            AppliedPrimaryModified = new DateTime(2026, 10, 11, 3, 0, 8, DateTimeKind.Utc),
        };
        state.Slots[0] = new CoverSlot { ItemId = Guid.NewGuid(), MomentSeconds = 1234.567 };
        state.Slots[3] = new CoverSlot { ItemId = Guid.NewGuid(), BackdropIndex = 2 };
        var current = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 10, 11, 3, 0, 7, TimeSpan.Zero),
            Origin = CoverOrigin.Generated,
            Mode = CoverMode.Scenes,
            TitleIds = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() },
        };
        var uploaded = new CoverRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
            Origin = CoverOrigin.Uploaded,
            Mode = CoverMode.Manual,
        };
        state.History.Add(current);
        state.History.Add(uploaded);
        state.CurrentCoverId = current.Id;
        return state;
    }

    private static void AssertSame(LibraryCoverState expected, LibraryCoverState actual)
    {
        Assert.Equal(expected.LibraryId, actual.LibraryId);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Pinned, actual.Pinned);
        AssertSame(expected.Style, actual.Style);
        Assert.Equal(expected.Slots.Length, actual.Slots.Length);
        for (var i = 0; i < expected.Slots.Length; i++)
        {
            AssertSame(expected.Slots[i], actual.Slots[i]);
        }

        Assert.Equal(expected.CurrentCoverId, actual.CurrentCoverId);
        AssertSame(expected.Original, actual.Original);
        Assert.Equal(expected.History.Count, actual.History.Count);
        for (var i = 0; i < expected.History.Count; i++)
        {
            AssertSame(expected.History[i], actual.History[i]);
        }

        Assert.Equal(expected.RenewEveryDays, actual.RenewEveryDays);
        Assert.Equal(expected.LastRenderedAt, actual.LastRenderedAt);
        Assert.Equal(expected.LastError, actual.LastError);
        Assert.Equal(expected.LastErrorAt, actual.LastErrorAt);
        Assert.Equal(expected.Notice, actual.Notice);
        Assert.Equal(expected.NoticeAt, actual.NoticeAt);
        Assert.Equal(expected.AppliedPrimaryPath, actual.AppliedPrimaryPath);
        Assert.Equal(expected.AppliedPrimaryModified, actual.AppliedPrimaryModified);
    }

    private static void AssertSame(CoverStyle expected, CoverStyle actual)
    {
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Font, actual.Font);
        Assert.Equal(expected.TextSize, actual.TextSize);
        Assert.Equal(expected.Darken, actual.Darken);
        Assert.Equal(expected.Frame, actual.Frame);
        Assert.Equal(expected.Shadow, actual.Shadow);
        Assert.Equal(expected.LengthSeconds, actual.LengthSeconds);
        Assert.Equal(expected.Fade, actual.Fade);
        Assert.Equal(expected.SlotCount, actual.SlotCount);
        Assert.Equal(expected.UseMostWatched, actual.UseMostWatched);
        Assert.Equal(expected.UseTopRated, actual.UseTopRated);
        Assert.Equal(expected.UseRecent, actual.UseRecent);
    }

    private static void AssertSame(CoverSlot expected, CoverSlot actual)
    {
        Assert.Equal(expected.ItemId, actual.ItemId);
        Assert.Equal(expected.MomentSeconds, actual.MomentSeconds);
        Assert.Equal(expected.BackdropIndex, actual.BackdropIndex);
        Assert.Equal(expected.Locked, actual.Locked);
    }

    private static void AssertSame(CoverRecord? expected, CoverRecord? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Origin, actual.Origin);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.TitleIds, actual.TitleIds);
    }

    private sealed class RecordingLogger : ILogger<CoverStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // Jellyfin's folders, all under one temporary folder that the test deletes.
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
