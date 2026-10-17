using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryCoverMaker.Covers;
using Jellyfin.Plugin.LibraryCoverMaker.Model;
using Jellyfin.Plugin.LibraryCoverMaker.Scheduling;
using Jellyfin.Plugin.LibraryCoverMaker.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LibraryCoverMaker.Tests;

public sealed class RenderQueueTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static readonly Guid A = Guid.Parse("0f3c5d7e-1a2b-4c6d-8e9f-a0b1c2d3e4f5");
    private static readonly Guid B = Guid.Parse("1f3c5d7e-1a2b-4c6d-8e9f-a0b1c2d3e4f5");
    private static readonly Guid C = Guid.Parse("2f3c5d7e-1a2b-4c6d-8e9f-a0b1c2d3e4f5");

    private readonly string _root = Directory.CreateTempSubdirectory("render-queue-tests-").FullName;
    private readonly CoverStore _store;
    private readonly List<RenderQueue> _started = new();

    public RenderQueueTests()
    {
        _store = new CoverStore(new Paths(_root), NullLogger<CoverStore>.Instance);
    }

    public void Dispose()
    {
        foreach (var queue in _started)
        {
            queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        Directory.Delete(_root, recursive: true);
    }

    private static BuildResult Ok() => new(true, null, 0);

    private RenderQueue Start(
        Func<Guid, BuildReason, CancellationToken, Task<BuildResult>> build,
        Func<bool>? playing = null,
        Func<Guid, bool>? exists = null)
    {
        var queue = new RenderQueue(
            build,
            playing ?? (() => false),
            exists ?? (_ => true),
            _store,
            NullLogger<RenderQueue>.Instance,
            TimeSpan.FromMilliseconds(10));
        queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _started.Add(queue);
        return queue;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time()
    {
        var running = 0;
        var peak = 0;
        var done = 0;
        var queue = Start(async (_, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            await Task.Delay(60, ct);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref done);
            return Ok();
        });

        queue.Enqueue(A, BuildReason.Scheduled);
        queue.Enqueue(B, BuildReason.Scheduled);
        var manual = await queue.RunNowAsync(C, BuildReason.Manual, CancellationToken.None);

        await Until(() => Volatile.Read(ref done) == 3);
        Assert.True(manual.Applied);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Queued_library_is_not_added_twice()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var calls = new List<Guid>();
        var queue = Start(async (id, _, _) =>
        {
            lock (calls)
            {
                calls.Add(id);
            }

            started.TrySetResult();
            await gate.Task;
            return Ok();
        });

        queue.Enqueue(A, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        queue.Enqueue(B, BuildReason.Scheduled);
        queue.Enqueue(B, BuildReason.Scheduled);
        gate.SetResult();

        await Until(() => { lock (calls) { return calls.Count >= 2; } });
        await Task.Delay(100);
        lock (calls)
        {
            Assert.Equal(new[] { A, B }, calls);
        }
    }

    [Fact]
    public async Task Waits_while_someone_is_playing()
    {
        var playing = true;
        var built = 0;
        var queue = Start(
            (_, _, _) =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(Ok());
            },
            () => Volatile.Read(ref playing));

        queue.Enqueue(A, BuildReason.Scheduled);
        await Task.Delay(150);
        Assert.Equal(0, built);

        Volatile.Write(ref playing, false);
        await Until(() => Volatile.Read(ref built) == 1);
    }

    [Fact]
    public async Task Failed_job_does_not_stop_the_queue()
    {
        var built = new List<Guid>();
        var queue = Start((id, _, _) =>
        {
            lock (built)
            {
                built.Add(id);
            }

            if (id == A)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(id == B ? new BuildResult(false, "no titles", 0) : Ok());
        });

        queue.Enqueue(A, BuildReason.Scheduled);
        queue.Enqueue(B, BuildReason.Scheduled);
        var last = await queue.RunNowAsync(C, BuildReason.Scheduled, CancellationToken.None);

        Assert.True(last.Applied);
        lock (built)
        {
            Assert.Equal(new[] { A, B, C }, built);
        }

        var thrown = await queue.RunNowAsync(A, BuildReason.Manual, CancellationToken.None);
        Assert.False(thrown.Applied);
        Assert.Contains("boom", thrown.Error);
    }

    [Fact]
    public async Task Removed_library_job_is_dropped()
    {
        _store.Update(A, state => state.Pinned = true);
        Assert.Contains(A, _store.KnownLibraries());
        var built = 0;
        var queue = Start(
            (_, _, _) =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(Ok());
            },
            exists: id => id != A);

        var result = await queue.RunNowAsync(A, BuildReason.Scheduled, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(0, built);
        Assert.DoesNotContain(A, _store.KnownLibraries());
    }

    [Fact]
    public void Start_cleans_leftover_temp_files()
    {
        Directory.CreateDirectory(_store.TempFolder);
        var leftover = Path.Combine(_store.TempFolder, "half-done.webp");
        File.WriteAllText(leftover, "x");

        Start((_, _, _) => Task.FromResult(Ok()));

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void Start_removes_orphan_covers_but_keeps_referenced_ones()
    {
        var kept = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        _store.Update(A, state => state.CurrentCoverId = kept);
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AnimatedPath(A, kept))!);
        foreach (var path in new[]
        {
            _store.AnimatedPath(A, kept), _store.StillPath(A, kept),
            _store.AnimatedPath(A, orphan), _store.StillPath(A, orphan),
        })
        {
            File.WriteAllText(path, "x");
        }

        Start((_, _, _) => Task.FromResult(Ok()));

        Assert.True(File.Exists(_store.AnimatedPath(A, kept)));
        Assert.True(File.Exists(_store.StillPath(A, kept)));
        Assert.False(File.Exists(_store.AnimatedPath(A, orphan)));
        Assert.False(File.Exists(_store.StillPath(A, orphan)));
    }

    [Fact]
    public async Task Stop_cancels_the_job_in_flight()
    {
        var started = new TaskCompletionSource();
        var cancelled = false;
        var queue = Start(async (_, _, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return Ok();
        });

        queue.Enqueue(A, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        await queue.StopAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Refresh_task_builds_only_libraries_the_schedule_may_touch()
    {
        var other = Guid.NewGuid();
        var pinned = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var renewing = Guid.NewGuid();
        _store.Update(pinned, state => state.Pinned = true);
        _store.Update(manual, state => state.Mode = CoverMode.Manual);
        _store.Update(renewing, state => state.RenewEveryDays = 30);
        var built = new List<Guid>();
        var reasons = new List<BuildReason>();
        var queue = Start((id, reason, _) =>
        {
            lock (built)
            {
                built.Add(id);
                reasons.Add(reason);
            }

            return Task.FromResult(Ok());
        });
        var task = new RefreshCoversTask(
            () => new[] { A, other, pinned, manual, renewing },
            () => CoverMode.Art,
            _store,
            queue,
            NullLogger<RefreshCoversTask>.Instance);
        var progress = new List<double>();

        await task.ExecuteAsync(new Progress<double>(progress.Add), CancellationToken.None);

        lock (built)
        {
            Assert.Equal(new[] { A, other }, built);
            Assert.All(reasons, reason => Assert.Equal(BuildReason.Scheduled, reason));
        }

        Assert.Equal("Refresh library covers", task.Name);
        Assert.Equal("LibraryCoverMakerRefresh", task.Key);
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public async Task Renewal_enqueues_libraries_whose_time_has_come()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var due = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var never = Guid.NewGuid();
        var pinned = Guid.NewGuid();
        var off = Guid.NewGuid();
        _store.Update(due, s => { s.RenewEveryDays = 7; s.LastRenderedAt = now.AddDays(-8); });
        _store.Update(fresh, s => { s.RenewEveryDays = 7; s.LastRenderedAt = now.AddDays(-6); });
        _store.Update(never, s => s.RenewEveryDays = 7);
        _store.Update(pinned, s => { s.RenewEveryDays = 1; s.Pinned = true; s.LastRenderedAt = now.AddDays(-9); });
        _store.Update(off, s => s.LastRenderedAt = now.AddDays(-90));
        var built = new List<Guid>();
        var queue = Start((id, _, _) =>
        {
            lock (built)
            {
                built.Add(id);
            }

            return Task.FromResult(Ok());
        });
        var scheduler = new RenewalScheduler(
            () => new[] { due, fresh, never, pinned, off },
            () => CoverMode.Art,
            _store,
            queue,
            () => now,
            NullLogger<RenewalScheduler>.Instance);

        scheduler.CheckNow();

        await Until(() => { lock (built) { return built.Count >= 2; } });
        await Task.Delay(100);
        lock (built)
        {
            Assert.Equal(new[] { due, never }, built);
        }
    }

    private string StateFile(Guid library) =>
        Path.Combine(_root, "data", "library-cover-maker", library.ToString("N"), "state.json");

    private void WriteCover(Guid library, Guid cover)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AnimatedPath(library, cover))!);
        File.WriteAllText(_store.AnimatedPath(library, cover), "x");
        File.WriteAllText(_store.StillPath(library, cover), "x");
    }

    [Fact]
    public async Task Manual_job_skips_the_playback_wait()
    {
        var queue = Start((_, _, _) => Task.FromResult(Ok()), () => true);

        var result = await queue.RunNowAsync(A, BuildReason.Manual, CancellationToken.None).WaitAsync(Patience);

        Assert.True(result.Applied);
    }

    [Fact]
    public async Task Manual_job_goes_before_a_job_waiting_for_playback()
    {
        var playing = true;
        var built = new List<Guid>();
        var queue = Start(
            (id, _, _) =>
            {
                lock (built)
                {
                    built.Add(id);
                }

                return Task.FromResult(Ok());
            },
            () => Volatile.Read(ref playing));

        queue.Enqueue(A, BuildReason.Scheduled);
        await Task.Delay(100); // the worker holds A back for playback by now
        await queue.RunNowAsync(B, BuildReason.Manual, CancellationToken.None).WaitAsync(Patience);
        lock (built)
        {
            Assert.Equal(new[] { B }, built);
        }

        Volatile.Write(ref playing, false);
        await Until(() => { lock (built) { return built.Count == 2; } });
        lock (built)
        {
            Assert.Equal(new[] { B, A }, built);
        }
    }

    [Fact]
    public async Task Cancelled_queued_job_is_cancelled_at_once_and_never_built()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var built = new List<Guid>();
        var queue = Start(async (id, _, _) =>
        {
            lock (built)
            {
                built.Add(id);
            }

            started.TrySetResult();
            await gate.Task;
            return Ok();
        });
        queue.Enqueue(A, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        using var cts = new CancellationTokenSource();
        var queued = queue.RunNowAsync(B, BuildReason.Scheduled, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
        gate.SetResult();
        await Task.Delay(100);
        lock (built)
        {
            Assert.Equal(new[] { A }, built);
        }
    }

    [Fact]
    public async Task Refresh_task_cancelled_while_its_job_is_queued_returns_promptly()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var queue = Start(async (_, _, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return Ok();
        });
        queue.Enqueue(A, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        var task = new RefreshCoversTask(
            () => new[] { B }, () => CoverMode.Art, _store, queue, NullLogger<RefreshCoversTask>.Instance);
        using var cts = new CancellationTokenSource();
        var running = task.ExecuteAsync(new Progress<double>(), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        gate.SetResult();
    }

    [Fact]
    public async Task Library_stays_queued_while_the_worker_holds_it_for_playback()
    {
        var playing = true;
        var built = 0;
        var queue = Start(
            (_, _, _) =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(Ok());
            },
            () => Volatile.Read(ref playing));

        queue.Enqueue(A, BuildReason.Scheduled);
        await Task.Delay(100);
        queue.Enqueue(A, BuildReason.Scheduled);
        Volatile.Write(ref playing, false);

        await Until(() => Volatile.Read(ref built) >= 1);
        await Task.Delay(150);
        Assert.Equal(1, built);
    }

    [Fact]
    public async Task Stop_cancels_waiting_callers_and_later_calls()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var queue = Start(async (_, _, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return Ok();
        });
        queue.Enqueue(A, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        var waiting = queue.RunNowAsync(B, BuildReason.Scheduled, CancellationToken.None);

        await queue.StopAsync(CancellationToken.None).WaitAsync(Patience);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
        var after = queue.RunNowAsync(C, BuildReason.Manual, CancellationToken.None);
        Assert.True(after.IsCanceled);
    }

    [Fact]
    public void Start_keeps_history_and_original_covers_and_skips_an_unreadable_state()
    {
        var inHistory = Guid.NewGuid();
        var original = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        _store.Update(A, state =>
        {
            state.History.Add(new CoverRecord { Id = inHistory });
            state.Original = new CoverRecord { Id = original, Origin = CoverOrigin.Original };
        });
        WriteCover(A, inHistory);
        WriteCover(A, original);
        WriteCover(A, orphan);
        var broken = Guid.NewGuid();
        WriteCover(broken, orphan);
        File.WriteAllText(StateFile(broken), "{ not json");

        Start((_, _, _) => Task.FromResult(Ok()));

        Assert.True(File.Exists(_store.AnimatedPath(A, inHistory)));
        Assert.True(File.Exists(_store.StillPath(A, original)));
        Assert.False(File.Exists(_store.AnimatedPath(A, orphan)));
        Assert.True(File.Exists(_store.AnimatedPath(broken, orphan)));
    }

    [Fact]
    public async Task One_unreadable_library_does_not_stop_start_refresh_or_renewal()
    {
        var bad = Guid.NewGuid();
        var good1 = Guid.NewGuid();
        var good2 = Guid.NewGuid();
        var plain1 = Guid.NewGuid();
        var plain2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _store.Update(bad, s => s.RenewEveryDays = 7);
        _store.Update(good1, s => { s.RenewEveryDays = 7; s.LastRenderedAt = now.AddDays(-30); });
        _store.Update(good2, s => { s.RenewEveryDays = 7; s.LastRenderedAt = now.AddDays(-30); });
        _store.Update(plain1, s => s.Mode = CoverMode.Art);
        _store.Update(plain2, s => s.Mode = CoverMode.Scenes);

        // Garbage makes Load copy the file to state.json.bad; a directory in the way makes that copy fail, for
        // every user, root included.
        File.WriteAllText(StateFile(bad), "{ not json");
        Directory.CreateDirectory(StateFile(bad) + ".bad");
        Assert.ThrowsAny<Exception>(() => _store.Load(bad));

        var built = new List<Guid>();
        var queue = Start((id, _, _) =>
        {
            lock (built)
            {
                built.Add(id);
            }

            return Task.FromResult(Ok());
        });

        new RenewalScheduler(
            () => new[] { bad, good1, good2 }, () => CoverMode.Art, _store, queue, () => now,
            NullLogger<RenewalScheduler>.Instance).CheckNow();
        await Until(() => { lock (built) { return built.Count == 2; } });
        lock (built)
        {
            Assert.Equal(new[] { good1, good2 }, built);
            built.Clear();
        }

        var refresh = new RefreshCoversTask(
            () => new[] { bad, plain1, plain2 }, () => CoverMode.Art, _store, queue,
            NullLogger<RefreshCoversTask>.Instance);
        await refresh.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        lock (built)
        {
            Assert.Equal(new[] { plain1, plain2 }, built);
        }
    }

    [Fact]
    public void A_manual_build_is_refused_by_Enqueue()
    {
        var queue = Start((_, _, _) => Task.FromResult(Ok()));

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(A, BuildReason.Manual));

        Assert.Equal("reason", refused.ParamName);
    }

    [Fact]
    public async Task A_manual_build_goes_first_and_the_audience_change_held_for_playback_still_runs()
    {
        var playing = true;
        var built = new List<BuildReason>();
        var queue = Start(
            (_, reason, _) =>
            {
                lock (built)
                {
                    built.Add(reason);
                }

                return Task.FromResult(Ok());
            },
            () => Volatile.Read(ref playing));
        queue.Enqueue(A, BuildReason.AudienceChanged);
        await Task.Delay(100);

        // On a pinned library the manual build changes nothing, so the audience change must not be dropped for it.
        var manual = await queue.RunNowAsync(A, BuildReason.Manual, CancellationToken.None).WaitAsync(Patience);
        Assert.True(manual.Applied);
        lock (built)
        {
            Assert.Equal(new[] { BuildReason.Manual }, built);
        }

        Volatile.Write(ref playing, false);
        await Until(() => { lock (built) { return built.Count == 2; } });
        await Task.Delay(150);
        lock (built)
        {
            Assert.Equal(new[] { BuildReason.Manual, BuildReason.AudienceChanged }, built);
        }
    }

    [Fact]
    public async Task Audience_change_upgrades_a_waiting_scheduled_job()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var built = new List<(Guid Id, BuildReason Reason)>();
        var queue = Start(async (id, reason, _) =>
        {
            lock (built)
            {
                built.Add((id, reason));
            }

            if (id == B)
            {
                started.TrySetResult();
                await gate.Task;
            }

            return Ok();
        });
        queue.Enqueue(B, BuildReason.Scheduled);
        await started.Task.WaitAsync(Patience);
        queue.Enqueue(A, BuildReason.Scheduled);

        // A pinned or manual library ignores a Scheduled build, so the audience change must not be lost behind it.
        queue.Enqueue(A, BuildReason.AudienceChanged);
        queue.Enqueue(A, BuildReason.Scheduled);
        gate.SetResult();

        await Until(() => { lock (built) { return built.Count >= 2; } });
        await Task.Delay(150);
        lock (built)
        {
            Assert.Equal(new[] { (B, BuildReason.Scheduled), (A, BuildReason.AudienceChanged) }, built);
        }
    }

    [Fact]
    public async Task Audience_change_upgrades_a_scheduled_job_held_back_for_playback()
    {
        var playing = true;
        var built = new List<BuildReason>();
        var queue = Start(
            (_, reason, _) =>
            {
                lock (built)
                {
                    built.Add(reason);
                }

                return Task.FromResult(Ok());
            },
            () => Volatile.Read(ref playing));
        queue.Enqueue(A, BuildReason.Scheduled);
        await Task.Delay(100);

        queue.Enqueue(A, BuildReason.AudienceChanged);
        Volatile.Write(ref playing, false);
        await Until(() => { lock (built) { return built.Count == 1; } });
        await Task.Delay(150);

        lock (built)
        {
            Assert.Equal(new[] { BuildReason.AudienceChanged }, built);
        }
    }

    [Fact]
    public async Task Renewal_ignores_a_zero_or_negative_timetable()
    {
        var now = DateTimeOffset.UtcNow;
        var zero = Guid.NewGuid();
        var huge = Guid.NewGuid();
        _store.Update(zero, s => { s.RenewEveryDays = 0; s.LastRenderedAt = now.AddDays(-30); });
        _store.Update(huge, s => { s.RenewEveryDays = int.MaxValue; s.LastRenderedAt = now.AddDays(-30); });
        var built = new List<Guid>();
        var queue = Start((id, _, _) =>
        {
            lock (built)
            {
                built.Add(id);
            }

            return Task.FromResult(Ok());
        });

        new RenewalScheduler(
            () => new[] { zero, huge }, () => CoverMode.Art, _store, queue, () => now,
            NullLogger<RenewalScheduler>.Instance).CheckNow();
        await Task.Delay(150);

        lock (built)
        {
            Assert.Empty(built);
        }
    }

    [Fact]
    public async Task Renewal_waits_six_hours_after_a_failed_build()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var failing = Guid.NewGuid();
        var marker = Guid.NewGuid();
        _store.Update(failing, s => { s.RenewEveryDays = 1; s.LastRenderedAt = now.AddDays(-9); });
        var attempts = 0;
        var markerRuns = 0;
        var queue = Start((id, _, _) =>
        {
            if (id == marker)
            {
                Interlocked.Increment(ref markerRuns);
                return Task.FromResult(Ok());
            }

            Interlocked.Increment(ref attempts);
            return Task.FromResult(new BuildResult(false, "no titles", 0));
        });
        var scheduler = new RenewalScheduler(
            () => new[] { failing }, () => CoverMode.Art, _store, queue, () => now,
            NullLogger<RenewalScheduler>.Instance);

        // The marker job runs after the previous one, so its start means the failure has been recorded.
        async Task Settle(int markers)
        {
            queue.Enqueue(marker, BuildReason.Scheduled);
            await Until(() => Volatile.Read(ref markerRuns) == markers);
        }

        scheduler.CheckNow();
        await Settle(1);
        Assert.Equal(1, attempts);

        now = now.AddHours(5);
        scheduler.CheckNow();
        await Settle(2);
        Assert.Equal(1, attempts);

        now = now.AddHours(2);
        scheduler.CheckNow();
        await Settle(3);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Audit_queues_every_library_whose_cover_breaks_the_rule_pinned_and_manual_included()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var pinned = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var fine = Guid.NewGuid();
        _store.Update(pinned, s => s.Pinned = true);
        _store.Update(manual, s => s.Mode = CoverMode.Manual);
        _store.Update(fine, s => s.Pinned = true);
        var built = new List<(Guid Id, BuildReason Reason)>();
        var queue = Start((id, reason, _) =>
        {
            lock (built)
            {
                built.Add((id, reason));
            }

            return Task.FromResult(Ok());
        });
        var scheduler = new RenewalScheduler(
            () => new[] { pinned, manual, fine },
            () => CoverMode.Art,
            _store,
            queue,
            () => now,
            NullLogger<RenewalScheduler>.Instance,
            (id, _) => id != fine);

        scheduler.CheckNow();

        await Until(() => { lock (built) { return built.Count >= 2; } });
        await Task.Delay(100);
        lock (built)
        {
            Assert.Equal(new[] { (pinned, BuildReason.AudienceChanged), (manual, BuildReason.AudienceChanged) }, built);
        }
    }

    [Fact]
    public async Task Audit_tries_again_after_an_hour_not_after_the_renewal_pause()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var failing = Guid.NewGuid();
        var marker = Guid.NewGuid();
        _store.Update(failing, s => { s.Pinned = true; s.RenewEveryDays = 1; });
        var attempts = 0;
        var markerRuns = 0;
        var queue = Start((id, _, _) =>
        {
            if (id == marker)
            {
                Interlocked.Increment(ref markerRuns);
                return Task.FromResult(Ok());
            }

            Interlocked.Increment(ref attempts);
            return Task.FromResult(new BuildResult(false, "ffmpeg exited with code 1", 0));
        });
        var breaks = true;
        var scheduler = new RenewalScheduler(
            () => new[] { failing }, () => CoverMode.Art, _store, queue, () => now,
            NullLogger<RenewalScheduler>.Instance, (_, _) => breaks);

        async Task Settle(int markers)
        {
            queue.Enqueue(marker, BuildReason.Scheduled);
            await Until(() => Volatile.Read(ref markerRuns) == markers);
        }

        scheduler.CheckNow();
        await Settle(1);
        Assert.Equal(1, attempts);

        // The next check, 15 minutes later: the audit's own pause holds.
        now = now.AddMinutes(15);
        scheduler.CheckNow();
        await Settle(2);
        Assert.Equal(1, attempts);

        // An hour after the first: tried again, although a failed renewal would wait six hours.
        now = now.AddMinutes(46);
        scheduler.CheckNow();
        await Settle(3);
        Assert.Equal(2, attempts);

        // Once the cover keeps the rule, the pause is forgotten: a new break is queued at the next check.
        breaks = false;
        now = now.AddMinutes(15);
        scheduler.CheckNow();
        breaks = true;
        now = now.AddMinutes(15);
        scheduler.CheckNow();
        await Settle(4);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Audit_leaves_out_a_library_that_is_switched_off(bool enabled, bool queued)
    {
        var library = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var cover = new CoverRecord { Id = Guid.NewGuid(), Origin = CoverOrigin.Generated, TitleIds = [gone] };
        var state = new LibraryCoverState { LibraryId = library, History = [cover], CurrentCoverId = cover.Id };
        var libraries = SceneFinderTests.Stub<MediaBrowser.Controller.Library.ILibraryManager>(new()
        {
            ["GetItemById"] = args => (Guid)args[0]! == library
                ? new MediaBrowser.Controller.Entities.CollectionFolder { Id = library }
                : null,
            ["GetLibraryOptions"] = _ => new MediaBrowser.Model.Configuration.LibraryOptions { Enabled = enabled },
        });
        var users = SceneFinderTests.Stub<MediaBrowser.Controller.Library.IUserManager>(new()
        {
            ["GetUsers"] = _ => Array.Empty<Jellyfin.Database.Implementations.Entities.User>(),
        });

        // The cover shows a title that left the library: it breaks the rule, but a switched-off library is never built.
        var breaks = RenewalScheduler.BreaksRuleNow(
            libraries,
            new Jellyfin.Plugin.LibraryCoverMaker.Selection.LibraryAudience(users),
            library,
            state);

        Assert.Equal(queued, breaks);
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
