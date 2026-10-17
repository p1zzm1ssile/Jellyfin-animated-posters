using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryCoverMaker.Rendering;

/// <summary>
/// Runs Jellyfin's own ffmpeg (<see cref="IMediaEncoder.EncoderPath"/>) the way a background job should: at the
/// lowest priority, with every argument passed as it is (no shell, so no quoting), its error output read as it
/// comes (a full pipe would stop it), and the whole process tree stopped when the job is cancelled, when ffmpeg
/// hangs, or when it has used more processor time than any cover needs.
/// </summary>
public sealed class FfmpegRunner
{
    /// <summary>
    /// The exit code <see cref="RunAsync"/> reports when ffmpeg did not run to its end: it could not be started,
    /// Jellyfin has no ffmpeg configured, it was stopped for showing no sign of life, or it was stopped for using
    /// too much processor time. The error text says which.
    /// </summary>
    public const int NotCompleted = -1;

    /// <summary>
    /// How many threads ffmpeg may use for each file and for its filters. Covers are made in the background and
    /// must not compete with playback, so ffmpeg does not get a thread for every core.
    /// </summary>
    public const int MaxThreads = 2;

    /// <summary>
    /// How much processor time one run of ffmpeg may use before it is stopped: 15 minutes. A cover takes seconds,
    /// at idle priority and two threads. A demuxer or decoder that loops on a malformed file uses all the processor
    /// time it gets and keeps showing signs of life for ever, so the stall rule never stops it, and the one worker
    /// of the render queue would be stuck behind it. Processor time and not wall-clock time: a render that is starved
    /// at idle priority on a busy host waits a long time and uses little, and must not be stopped for waiting.
    /// </summary>
    public static readonly TimeSpan DefaultProcessorTimeBudget = TimeSpan.FromMinutes(15);

    // Enough lines to find a message that came before a flood of others, few enough to stay small.
    private const int ErrorLinesKept = 200;

    // The options every run starts with: no banner, no reading from the terminal, overwrite the output (callers
    // write to a temporary file of their own), and only errors on stderr. A caller that needs more output, such as
    // scene cut detection, passes its own -loglevel: the last one wins.
    private static readonly string[] StandardOptions = ["-hide_banner", "-nostdin", "-y", "-loglevel", "error"];

    private readonly IMediaEncoder _encoder;
    private readonly ILogger<FfmpegRunner> _log;
    private readonly RunnerLimits _limits;

    /// <summary>Creates the runner.</summary>
    public FfmpegRunner(IMediaEncoder encoder, ILogger<FfmpegRunner> log)
        : this(encoder, log, RunnerLimits.Default)
    {
    }

    // For the tests, which need limits of milliseconds where a real run has minutes.
    internal FfmpegRunner(IMediaEncoder encoder, ILogger<FfmpegRunner> log, RunnerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.ProcessorTimeBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.FrameStall, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.StopGrace, TimeSpan.Zero);

        _encoder = encoder;
        _log = log;
        _limits = limits;
    }

    /// <summary>
    /// <c>-threads</c> with <see cref="MaxThreads"/>. The option belongs to the one file that follows it, so it goes
    /// before every <c>-i</c> and before every output; <see cref="RunAsync"/> cannot add it for the caller.
    /// </summary>
    public static IReadOnlyList<string> ThreadLimit { get; } =
        Array.AsReadOnly(["-threads", MaxThreads.ToString(CultureInfo.InvariantCulture)]);

    /// <summary>
    /// Runs ffmpeg with the standard options (no banner, no stdin, overwrite, <c>-loglevel error</c>) followed by
    /// <paramref name="args"/>, at idle priority, and returns its exit code with the end of what it wrote to
    /// stderr: the last 200 lines at most, one per line, without blank ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ffmpeg that shows no sign of life for <paramref name="stallTimeout"/> is stopped and the exit code is
    /// <see cref="NotCompleted"/>. A sign of life is a line on stderr or processor time used: a render that is
    /// working uses the processor, and one that waits for a hung disk uses neither.
    /// </para>
    /// <para>
    /// An ffmpeg that has used <see cref="DefaultProcessorTimeBudget"/> of processor time without finishing is
    /// stopped too, with the same exit code: it is working, so it is never silent, and nothing else would end it.
    /// </para>
    /// <para>
    /// Cancelling kills ffmpeg and everything it started, then throws <see cref="OperationCanceledException"/>.
    /// Nothing else is thrown for ffmpeg itself: an ffmpeg that cannot be started is reported as
    /// <see cref="NotCompleted"/>.
    /// </para>
    /// </remarks>
    /// <param name="args">What follows the standard options. Each file's own options go with it, such as
    /// <see cref="ThreadLimit"/>.</param>
    /// <param name="stallTimeout">How long ffmpeg may show no sign of life. Positive.</param>
    /// <param name="ct">Cancels the run.</param>
    public async Task<(int Exit, string Errors)> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan stallTimeout,
        CancellationToken ct)
    {
        var run = await RunCoreAsync(args, captureOutput: false, stallTimeout, ct).ConfigureAwait(false);
        return (run.Exit, run.Errors);
    }

    /// <summary>
    /// Runs ffmpeg as <see cref="RunAsync"/> does and returns, besides the end of what it wrote, the very first line
    /// it wrote: with more than 200 lines the tail has dropped it, and it is the line that says why ffmpeg failed.
    /// </summary>
    internal Task<RunResult> RunDetailedAsync(
        IReadOnlyList<string> args,
        TimeSpan stallTimeout,
        CancellationToken ct) =>
        RunCoreAsync(args, captureOutput: false, stallTimeout, ct);

    /// <summary>
    /// One frame of a video as an 8-bit RGB PNG, scaled to <paramref name="width"/> (the height follows the
    /// shape of the picture as it is shown, not as its pixels are stored), or null when ffmpeg cannot give one: the
    /// file is damaged, the moment is past its end, or ffmpeg did not finish with its output. The seek comes before
    /// the input, so ffmpeg jumps to the moment instead of decoding up to it. HDR frames are not tone mapped.
    /// </summary>
    /// <param name="mediaPath">The video file. An absolute path.</param>
    /// <param name="atSeconds">The moment, from the start of the video.</param>
    /// <param name="width">Width of the picture, 16 to 4096 pixels.</param>
    /// <param name="ct">Cancels the run.</param>
    public async Task<byte[]?> ReadFrameAsync(string mediaPath, double atSeconds, int width, CancellationToken ct)
    {
        // A path that does not start at the root could be taken for an option or a URL by ffmpeg.
        if (string.IsNullOrWhiteSpace(mediaPath) || !Path.IsPathRooted(mediaPath))
        {
            throw new ArgumentException($"The video must be an absolute path, not \"{mediaPath}\".", nameof(mediaPath));
        }

        if (!double.IsFinite(atSeconds) || atSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(atSeconds),
                atSeconds,
                "The moment must be 0 seconds or later.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 4096);

        var args = new List<string>(ThreadLimit);
        args.AddRange(["-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", mediaPath]);
        args.AddRange(ThreadLimit);
        args.AddRange(["-filter_threads", MaxThreads.ToString(CultureInfo.InvariantCulture)]);

        // reset_sar: a clip with non-square pixels (DVD, DV) is as wide on screen as its sample aspect ratio says,
        // and the height that -2 works out has to follow that, or the picture comes out thin.
        var scale = string.Create(CultureInfo.InvariantCulture, $"scale={width}:-2:reset_sar=1");
        args.AddRange(["-frames:v", "1", "-an", "-sn", "-vf", scale]);

        // Without it a 10-bit video gives a 16-bit PNG, five times the size, which not every decoder reads the same
        // way.
        args.AddRange(["-pix_fmt", "rgb24"]);
        args.AddRange(["-f", "image2pipe", "-c:v", "png", "pipe:1"]);

        var run = await RunCoreAsync(args, captureOutput: true, _limits.FrameStall, ct).ConfigureAwait(false);
        if (run.Exit == 0 && run.Output.Length > 0)
        {
            return run.Output;
        }

        // Sampling a file at many moments meets this all the time, so it is not worth more than a debug line.
        _log.LogDebug(
            "Library Cover Maker: no frame at {Seconds} s of {Path} (ffmpeg exit {Exit}): {Errors}",
            atSeconds,
            mediaPath,
            run.Exit,
            run.Errors);
        return null;
    }

    // Stalled and OverBudget are the two reasons to stop an ffmpeg that is still running.
    private enum Verdict
    {
        Exited,
        Stalled,
        OverBudget,
    }

    // 1 minute, 15 minutes, 1 second, 0.3 seconds.
    private static string Describe(TimeSpan span)
    {
        var (value, unit) = span >= TimeSpan.FromMinutes(1)
            ? (span.TotalMinutes, "minute")
            : (span.TotalSeconds, "second");
        var text = value.ToString("0.#", CultureInfo.InvariantCulture);
        return text == "1" ? $"1 {unit}" : $"{text} {unit}s";
    }

    private async Task<RunResult> RunCoreAsync(
        IReadOnlyList<string> args,
        bool captureOutput,
        TimeSpan stallTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stallTimeout, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return new RunResult(
                NotCompleted,
                "Jellyfin has no ffmpeg configured, so there is no ffmpeg to run.",
                string.Empty,
                []);
        }

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var option in StandardOptions)
        {
            info.ArgumentList.Add(option);
        }

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new RunResult(
                NotCompleted,
                $"ffmpeg could not be started ({ffmpeg}): {ex.Message}",
                string.Empty,
                []);
        }

        // Joining the arguments costs something on every run: it is not done for a message nobody has asked for.
        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("Library Cover Maker: running ffmpeg {Arguments}", string.Join(' ', info.ArgumentList));
        }

        LowerPriority(process);

        // Both pipes are read from the start: ffmpeg stops when one fills up.
        var state = new RunState();
        var output = new MemoryStream();
        var stderrRead = Task.Run(() => DrainAsync(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                state.AddLine(line);
            }
        }));
        var stdoutRead = captureOutput
            ? Task.Run(
                () => DrainAsync(() => process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None)))
            : Task.CompletedTask;
        var pipes = Task.WhenAll(stderrRead, stdoutRead);

        var verdict = Verdict.Exited;
        try
        {
            verdict = await WatchAsync(process, state, stallTimeout, _limits.ProcessorTimeBudget, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process).ConfigureAwait(false);
            await WaitBrieflyAsync(pipes).ConfigureAwait(false);
            throw;
        }

        // A verdict and a natural exit can arrive together: an ffmpeg that has finished is reported as it finished,
        // with its exit code, and not as stopped.
        if (verdict != Verdict.Exited && process.HasExited)
        {
            verdict = Verdict.Exited;
        }

        var reason = string.Empty;
        if (verdict == Verdict.Stalled)
        {
            reason = $"ffmpeg showed no sign of life for {Describe(stallTimeout)}, so it was stopped.";
            _log.LogWarning(
                "Library Cover Maker: ffmpeg showed no sign of life for {Limit}, so it was stopped",
                Describe(stallTimeout));
        }
        else if (verdict == Verdict.OverBudget)
        {
            reason = $"ffmpeg used {Describe(_limits.ProcessorTimeBudget)} of processor time without finishing and was "
                + "stopped.";
            _log.LogWarning(
                "Library Cover Maker: ffmpeg used {Budget} of processor time without finishing, so it was stopped",
                Describe(_limits.ProcessorTimeBudget));
        }

        if (verdict != Verdict.Exited)
        {
            await StopAsync(process).ConfigureAwait(false);
        }

        // The pipes close when ffmpeg ends; a child that outlived it could keep them open, so the wait is bounded.
        var drained = await WaitBrieflyAsync(pipes).ConfigureAwait(false);
        if (!drained)
        {
            _log.LogDebug(
                "Library Cover Maker: the output of ffmpeg was still open {Grace} after it ended: something it started "
                + "holds it",
                Describe(_limits.StopGrace));
        }

        var errors = state.Errors();
        if (verdict != Verdict.Exited)
        {
            return new RunResult(
                NotCompleted,
                errors.Length == 0 ? reason : errors + "\n" + reason,
                state.FirstLine,
                []);
        }

        // What was read while the pipes were still open may be half of the output: none is better than half a picture.
        return new RunResult(
            process.ExitCode,
            errors,
            state.FirstLine,
            captureOutput && drained ? output.ToArray() : []);
    }

    // Waits for ffmpeg to end, or says why it has to be stopped: no sign of life for too long, or too much processor
    // time.
    private static async Task<Verdict> WatchAsync(
        Process process,
        RunState state,
        TimeSpan stallTimeout,
        TimeSpan budget,
        CancellationToken ct)
    {
        // Often enough to stop a stall or an overspend close to its limit, rarely enough to cost nothing.
        var basis = stallTimeout < budget ? stallTimeout : budget;
        var interval = TimeSpan.FromTicks(
            Math.Clamp(basis.Ticks / 4, TimeSpan.FromMilliseconds(25).Ticks, TimeSpan.FromSeconds(5).Ticks));
        var processorTime = TimeSpan.Zero;
        while (!process.HasExited)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(interval);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The interval is over and ffmpeg still runs: see whether it is alive.
            }

            // Without a reading there is no verdict: better to wait than to stop an ffmpeg that is working.
            if (process.HasExited || !TryReadProcessorTime(process, out var now))
            {
                continue;
            }

            if (now >= budget)
            {
                return Verdict.OverBudget;
            }

            if (now > processorTime)
            {
                processorTime = now;
                state.Beat();
            }
            else if (state.Silence > stallTimeout)
            {
                return Verdict.Stalled;
            }
        }

        return Verdict.Exited;
    }

    private static bool TryReadProcessorTime(Process process, out TimeSpan time)
    {
        try
        {
            time = process.TotalProcessorTime;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            time = TimeSpan.Zero;
            return false;
        }
    }

    // The pipe closing under a read is how a stopped ffmpeg ends; it is not an error.
    private static async Task DrainAsync(Func<Task> read)
    {
        try
        {
            await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Nothing more to read.
        }
    }

    private async Task StopAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            _log.LogDebug(ex, "Library Cover Maker: ffmpeg was already gone, or could not be stopped");
        }

        await WaitBrieflyAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        if (!process.HasExited)
        {
            _log.LogWarning(
                "Library Cover Maker: ffmpeg has not exited {Grace} after it was stopped",
                Describe(_limits.StopGrace));
        }
    }

    // True when the task ended in time; false when whoever asked has waited long enough and it is still running.
    private async Task<bool> WaitBrieflyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(_limits.StopGrace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // The nice value of the process, and of the threads it starts from now on, is the lowest there is.
    private void LowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _log.LogDebug(
                ex,
                "Library Cover Maker: could not lower the priority of ffmpeg, so it runs at normal priority");
        }
    }

    // What the two readers and the watcher share: when ffmpeg last showed a sign of life, the very first line it
    // wrote, and the end of its messages.
    private sealed class RunState
    {
        private readonly Queue<string> _tail = new();
        private long _lastSign = Stopwatch.GetTimestamp();
        private string _firstLine = string.Empty;

        public TimeSpan Silence => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSign));

        public string FirstLine
        {
            get
            {
                lock (_tail)
                {
                    return _firstLine;
                }
            }
        }

        public void Beat() => Interlocked.Exchange(ref _lastSign, Stopwatch.GetTimestamp());

        public void AddLine(string line)
        {
            Beat();

            // A blank line shows ffmpeg is alive, and is not worth keeping.
            if (line.Length == 0)
            {
                return;
            }

            lock (_tail)
            {
                // The tail drops old lines to stay small; the first one is the cause of a failure and is kept apart.
                if (_firstLine.Length == 0)
                {
                    _firstLine = line;
                }

                _tail.Enqueue(line);
                if (_tail.Count > ErrorLinesKept)
                {
                    _tail.Dequeue();
                }
            }
        }

        public string Errors()
        {
            lock (_tail)
            {
                return string.Join('\n', _tail);
            }
        }
    }
}

/// <summary>
/// What a run of ffmpeg came to: the exit code, the end of its messages, the first one it wrote, and what it wrote to
/// its output.
/// </summary>
internal readonly record struct RunResult(int Exit, string Errors, string FirstLine, byte[] Output);

/// <summary>
/// The limits a run of ffmpeg is held to. A real run has the defaults; the tests have limits of milliseconds.
/// </summary>
/// <param name="ProcessorTimeBudget">How much processor time a run may use before it is stopped.</param>
/// <param name="FrameStall">How long reading one frame may show no sign of life: looking at one frame is quick, so a
/// minute is a file that hangs.</param>
/// <param name="StopGrace">How long to wait for a stopped ffmpeg to exit, and for its pipes to close, before giving up
/// on them.</param>
internal sealed record RunnerLimits(TimeSpan ProcessorTimeBudget, TimeSpan FrameStall, TimeSpan StopGrace)
{
    /// <summary>The limits of a real run.</summary>
    public static RunnerLimits Default { get; } =
        new(FfmpegRunner.DefaultProcessorTimeBudget, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
}
