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
    void Advance(int count = 1, string? currentItem = null);

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

        public void Advance(int count = 1, string? currentItem = null)
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
    private const int BarWidth = 24;
    private const int MaxCurrentItemLength = 60;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InteractiveRedrawInterval = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter _writer;
    private readonly bool _isInteractive;
    private readonly Lock _gate = new();

    public TextWriterScanProgress(TextWriter writer, bool isInteractive = false)
    {
        _writer = writer;
        _isInteractive = isInteractive;
    }

    public IScanStage Begin(string name, int? total = null)
    {
        var stage = new Stage(this, name, total, _isInteractive);
        if (!_isInteractive)
        {
            lock (_gate)
            {
                _writer.Write($"{name}... ");
                _writer.Flush();
            }
        }

        return stage;
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

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : "..." + text[^(maxLength - 3)..];

    private sealed class Stage : IScanStage
    {
        private readonly TextWriterScanProgress _owner;
        private readonly string _name;
        private readonly int? _total;
        private readonly bool _isInteractive;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Timer _heartbeat;
        private int _advanced;
        private string? _detail;
        private string? _currentItem;
        private int _lastLineLength;
        private bool _disposed;

        internal Stage(TextWriterScanProgress owner, string name, int? total, bool isInteractive)
        {
            _owner = owner;
            _name = name;
            _total = total;
            _isInteractive = isInteractive;
            var interval = isInteractive ? InteractiveRedrawInterval : HeartbeatInterval;
            _heartbeat = new Timer(_ => Tick(), null, interval, interval);
            if (isInteractive)
            {
                Redraw();
            }
        }

        public void Advance(int count = 1, string? currentItem = null)
        {
            Interlocked.Add(ref _advanced, count);
            if (currentItem is not null)
            {
                Volatile.Write(ref _currentItem, currentItem);
            }
        }

        private void Tick()
        {
            if (_disposed)
            {
                return;
            }

            if (_isInteractive)
            {
                Redraw();
                return;
            }

            var advanced = Volatile.Read(ref _advanced);
            var currentItem = Volatile.Read(ref _currentItem);
            var progressText = _total is { } total
                ? $"{advanced:N0}/{total:N0}"
                : advanced > 0
                    ? $"{advanced:N0}"
                    : Format(_stopwatch.Elapsed);
            var text = currentItem is null ? $"{progressText} " : $"{progressText} ({currentItem}) ";

            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _owner._writer.Write(text);
                _owner._writer.Flush();
            }
        }

        private string BuildBarLine()
        {
            var advanced = Volatile.Read(ref _advanced);
            var currentItem = Volatile.Read(ref _currentItem);

            string progress;
            if (_total is { } total and > 0)
            {
                var fraction = Math.Clamp((double)advanced / total, 0, 1);
                var filled = (int)(fraction * BarWidth);
                var bar = new string('#', filled) + new string('-', BarWidth - filled);
                var percent = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
                progress = $"[{bar}] {advanced:N0}/{total:N0} ({percent}%)";
            }
            else if (advanced > 0)
            {
                progress = $"{advanced:N0} processed";
            }
            else
            {
                progress = "starting";
            }

            var item = currentItem is null ? string.Empty : $" - {Truncate(currentItem, MaxCurrentItemLength)}";
            return $"{_name}... {progress} ({Format(_stopwatch.Elapsed)}){item}";
        }

        private void Redraw()
        {
            var line = BuildBarLine();

            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _owner._writer.Write('\r');
                _owner._writer.Write(line);
                var pad = _lastLineLength - line.Length;
                if (pad > 0)
                {
                    _owner._writer.Write(new string(' ', pad));
                }

                _lastLineLength = line.Length;
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
            _heartbeat.Dispose();
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
                if (_isInteractive)
                {
                    var finalLine = $"{_name}... {suffix}({Format(_stopwatch.Elapsed)})";
                    _owner._writer.Write('\r');
                    _owner._writer.Write(finalLine);
                    var pad = _lastLineLength - finalLine.Length;
                    if (pad > 0)
                    {
                        _owner._writer.Write(new string(' ', pad));
                    }

                    _owner._writer.WriteLine();
                }
                else
                {
                    _owner._writer.WriteLine($"{suffix}({Format(_stopwatch.Elapsed)})");
                }

                _owner._writer.Flush();
            }
        }
    }
}
