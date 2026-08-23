using System.Diagnostics;
using System.Globalization;

namespace SilentScan.Core.Diagnostics;

public interface IScanProgress
{
IScanStage Begin(string name, int? total = null);

void Done(TimeSpan elapsed);
}

public interface IScanStage : IDisposable
{
void Advance(int count = 1);

void Complete(string detail);
}

public sealed class NullScanProgress : IScanProgress
{
public static readonly NullScanProgress Instance = new();

    private NullScanProgress()
    {
    }

public IScanStage Begin(string name, int? total = null) => NullScanStage.Shared;

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

public sealed class TextWriterScanProgress : IScanProgress
{
private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly TextWriter _writer;
    private readonly Lock _gate = new();

public TextWriterScanProgress(TextWriter writer)
    {
        _writer = writer;
    }

public IScanStage Begin(string name, int? total = null)
    {
        lock (_gate)
        {
            _writer.Write($"{name}... ");
            _writer.Flush();
        }

        return new Stage(this, total);
    }

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
