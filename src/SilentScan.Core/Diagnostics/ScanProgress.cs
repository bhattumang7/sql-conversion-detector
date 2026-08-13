using System.Diagnostics;
using System.Globalization;

namespace SilentScan.Core.Diagnostics;

/// <summary>
/// Stage-level progress reporting for a long-running scan. A large database takes minutes to
/// scan and previously produced no output whatsoever until the whole report was rendered, so a
/// caller had no way to tell a slow stage from a hung one. Implementations write to a side
/// channel (stderr), never to the report's own stdout, so <c>--format json</c>/<c>sarif</c>
/// piping stays byte-clean.
/// </summary>
public interface IScanProgress
{
    /// <summary>
    /// Starts a named stage. Dispose the returned scope to emit its completion line.
    /// <paramref name="total"/>, when known, enables periodic "N/total" heartbeats so a stage
    /// that runs for a minute still proves it is making progress.
    /// </summary>
    IScanStage Begin(string name, int? total = null);

    /// <summary>Emits the final "done in Xs" line for the whole run.</summary>
    void Done(TimeSpan elapsed);
}

/// <summary>One in-flight stage. Disposing it emits the stage's completion line exactly once.</summary>
public interface IScanStage : IDisposable
{
    /// <summary>Records that <paramref name="count"/> more items finished, for heartbeat output.</summary>
    void Advance(int count = 1);

    /// <summary>
    /// Sets the description printed on the completion line (e.g. "1,284 tables, 9,113 columns").
    /// When never called, the completion line falls back to the advance counter.
    /// </summary>
    void Complete(string detail);
}

/// <summary>The no-output implementation, for callers (tests, library consumers) that want none.</summary>
public sealed class NullScanProgress : IScanProgress
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static readonly NullScanProgress Instance = new();

    private NullScanProgress()
    {
    }

    /// <inheritdoc />
    public IScanStage Begin(string name, int? total = null) => NullScanStage.Shared;

    /// <inheritdoc />
    public void Done(TimeSpan elapsed)
    {
    }

    private sealed class NullScanStage : IScanStage
    {
        internal static readonly NullScanStage Shared = new();

        public void Advance(int count = 1)
        {
        }

        public void Complete(string detail)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Writes one plain line per stage to a <see cref="TextWriter"/> (stderr in the CLI). The stage
/// name is written and flushed the moment the stage STARTS, with its detail and elapsed time
/// completing the same line when it finishes - so the line currently on screen always names what
/// the scan is doing right now, and a redirected log reads back identically to a terminal.
/// </summary>
public sealed class TextWriterScanProgress : IScanProgress
{
    /// <summary>
    /// How long a stage must run before it starts emitting "N/total" heartbeats, and the gap
    /// between them. Long enough that an ordinary fast stage stays a single clean line matching
    /// the no-heartbeat format, short enough that a minutes-long stage never looks hung.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly TextWriter _writer;
    private readonly Lock _gate = new();

    /// <param name="writer">Where stage lines go. The CLI passes stderr.</param>
    public TextWriterScanProgress(TextWriter writer)
    {
        _writer = writer;
    }

    /// <inheritdoc />
    public IScanStage Begin(string name, int? total = null)
    {
        lock (_gate)
        {
            _writer.Write($"{name}... ");
            _writer.Flush();
        }

        return new Stage(this, total);
    }

    /// <inheritdoc />
    public void Done(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _writer.WriteLine($"done in {Format(elapsed)}");
            _writer.Flush();
        }
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private sealed class Stage : IScanStage
    {
        private readonly TextWriterScanProgress _owner;
        private readonly int? _total;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _advanced;
        private long _lastHeartbeatTicks;
        private string? _detail;
        private bool _disposed;

        internal Stage(TextWriterScanProgress owner, int? total)
        {
            _owner = owner;
            _total = total;
        }

        public void Advance(int count = 1)
        {
            // Called from inside AsParallel() bodies, so the counter itself must be atomic; the
            // heartbeat write is additionally serialized on the writer's own gate below.
            var done = Interlocked.Add(ref _advanced, count);
            if (_total is null || _stopwatch.Elapsed < HeartbeatInterval)
            {
                return;
            }

            var elapsedTicks = _stopwatch.Elapsed.Ticks;
            var previous = Interlocked.Read(ref _lastHeartbeatTicks);
            if (elapsedTicks - previous < HeartbeatInterval.Ticks
                || Interlocked.CompareExchange(ref _lastHeartbeatTicks, elapsedTicks, previous) != previous)
            {
                return;
            }

            lock (_owner._gate)
            {
                _owner._writer.Write($"{done:N0}/{_total.Value:N0} ");
                _owner._writer.Flush();
            }
        }

        public void Complete(string detail) => _detail = detail;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();

            // A stage that threw is disposed by its `using` without ever calling Complete, and a
            // countless stage has nothing to fall back to - printing "0" there would read as a
            // successful stage that found nothing, which is exactly the wrong thing to tell
            // someone whose scan just failed. Emit the elapsed time alone and let the error that
            // follows speak for itself.
            var advanced = Volatile.Read(ref _advanced);
            var detail = _detail;
            if (detail is null && _total is not null)
            {
                detail = $"{advanced:N0}/{_total.Value:N0}";
            }
            else if (detail is null && advanced > 0)
            {
                detail = $"{advanced:N0}";
            }

            var suffix = detail is null ? string.Empty : detail + " ";

            lock (_owner._gate)
            {
                _owner._writer.WriteLine($"{suffix}({Format(_stopwatch.Elapsed)})");
                _owner._writer.Flush();
            }
        }
    }
}
