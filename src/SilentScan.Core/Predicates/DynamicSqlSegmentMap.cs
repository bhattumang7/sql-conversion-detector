using System.Text;

namespace SilentScan.Core.Predicates;

public sealed class DynamicSqlSegmentMap
{
    private sealed record Segment(int InnerStart, string Value, string SourcePath, int StartLine, int ContentStartColumn, bool IsPlaceholder = false);

    private readonly List<Segment> _segments = [];
    private readonly StringBuilder _innerText = new();

    public string InnerText => _innerText.ToString();

public IReadOnlyList<int> ConcatenationBoundaryOffsets =>
        [.. _segments.Skip(1).Where(s => !s.IsPlaceholder).Select(s => s.InnerStart)];

public void AppendLiteral(string sourcePath, int startLine, int startColumn, int prefixLength, string value)
    {
        _segments.Add(new Segment(_innerText.Length, value, sourcePath, startLine, startColumn + prefixLength));
        _innerText.Append(value);
    }

public int AppendPlaceholder(string sourcePath, int startLine, int startColumn, string value)
    {
        var innerStart = _innerText.Length;
        _segments.Add(new Segment(innerStart, value, sourcePath, startLine, startColumn, IsPlaceholder: true));
        _innerText.Append(value);
        return innerStart;
    }

public SourceSpan Map(int innerLine, int innerColumn)
    {
        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("Cannot map a position before any literal segment has been appended.");
        }

        var text = InnerText;
        var offset = ToOffset(text, innerLine, innerColumn);
        var segment = FindSegment(offset);

        if (segment.IsPlaceholder)
        {
            return new SourceSpan(segment.SourcePath, segment.StartLine, segment.ContentStartColumn);
        }

        var localOffset = Math.Clamp(offset - segment.InnerStart, 0, segment.Value.Length);

        var (lineDelta, column) = LocateWithinValue(segment.Value, localOffset, segment.ContentStartColumn);
        return new SourceSpan(segment.SourcePath, segment.StartLine + lineDelta, column);
    }

    private static (int LineDelta, int Column) LocateWithinValue(string value, int localOffset, int contentStartColumn)
    {
        var lineDelta = 0;
        var lastNewlineIndex = -1;
        var quotesSinceNewline = 0;
        var quotesTotal = 0;

        for (var i = 0; i < localOffset; i++)
        {
            if (value[i] == '\n')
            {
                lineDelta++;
                lastNewlineIndex = i;
                quotesSinceNewline = 0;
            }
            else if (value[i] == '\'')
            {
                quotesSinceNewline++;
                quotesTotal++;
            }
        }

        var column = lineDelta == 0
            ? contentStartColumn + localOffset + quotesTotal
            : (localOffset - (lastNewlineIndex + 1)) + quotesSinceNewline + 1;

        return (lineDelta, column);
    }

private Segment FindSegment(int offset)
    {
        var candidate = _segments[0];
        foreach (var segment in _segments)
        {
            if (segment.InnerStart > offset)
            {
                break;
            }

            candidate = segment;
        }

        return candidate;
    }

    private static int ToOffset(string text, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;
        while (currentLine < line)
        {
            var newlineIndex = text.IndexOf('\n', offset);
            if (newlineIndex < 0)
            {
                return text.Length;
            }

            offset = newlineIndex + 1;
            currentLine++;
        }

        return Math.Min(offset + column - 1, text.Length);
    }
}
