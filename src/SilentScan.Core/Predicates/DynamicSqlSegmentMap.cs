using System.Text;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Maps a position inside a dynamic SQL string reassembled from one or more literal segments
/// back to the file/line/column it came from in the original source. Two things make this
/// non-trivial: a single literal segment can span multiple source lines, and T-SQL's only
/// in-literal escape (<c>''</c> for a literal quote) means the reassembled (unescaped) text is
/// shorter than the raw source for any segment containing an escaped quote - so a naive
/// index-for-index mapping drifts as soon as one appears before the target position. Every
/// <c>'</c> character surviving in an already-unescaped literal value must have come from a raw
/// <c>''</c> pair (a lone unescaped quote can't appear inside a literal at all), so counting
/// them recovers the raw offset exactly.
/// </summary>
public sealed class DynamicSqlSegmentMap
{
    private sealed record Segment(int InnerStart, string Value, string SourcePath, int StartLine, int ContentStartColumn, bool IsPlaceholder = false);

    private readonly List<Segment> _segments = [];
    private readonly StringBuilder _innerText = new();

    public string InnerText => _innerText.ToString();

    /// <summary>
    /// Inner-text offsets where a new, independently-sourced literal segment begins - one entry
    /// per literal segment after the first (a lone segment can't be a concatenation boundary with
    /// itself), placeholder segments excluded. Each offset is a point where two source fragments
    /// authored/resolved separately (e.g. a string literal, then a folded constant variable's own
    /// value, then another string literal) were spliced together into <see cref="InnerText"/> -
    /// exactly what docs/detection-checklist.md's "Dynamic SQL quality" stream needs to find where
    /// a VALUE was concatenated into otherwise-fixed dynamic SQL text, as opposed to the whole
    /// script being one single literal with no splicing at all.
    /// </summary>
    public IReadOnlyList<int> ConcatenationBoundaryOffsets =>
        [.. _segments.Skip(1).Where(s => !s.IsPlaceholder).Select(s => s.InnerStart)];

    /// <param name="sourcePath">The file the literal came from.</param>
    /// <param name="startLine">The literal token's starting line (1-based, ScriptDOM convention).</param>
    /// <param name="startColumn">The literal token's starting column - the column of its opening quote, or its <c>N</c> prefix character if national.</param>
    /// <param name="prefixLength">Raw characters before the literal's content: 1 for <c>'</c>, 2 for <c>N'</c>.</param>
    /// <param name="value">The literal's already-unescaped value (ScriptDOM decodes <c>''</c> to <c>'</c>).</param>
    public void AppendLiteral(string sourcePath, int startLine, int startColumn, int prefixLength, string value)
    {
        _segments.Add(new Segment(_innerText.Length, value, sourcePath, startLine, startColumn + prefixLength));
        _innerText.Append(value);
    }

    /// <summary>
    /// Appends a synthesized placeholder token - standing in for a value this scanner could not
    /// prove constant - occupying real space in <see cref="InnerText"/> so the reparsed SQL stays
    /// syntactically valid, but with no real source text underneath it: unlike
    /// <see cref="AppendLiteral"/>, <paramref name="value"/> is a token this scanner invented, not
    /// something ScriptDOM decoded from the file, so there is no quote-escaping or multi-line
    /// arithmetic to do when <see cref="Map"/> is later asked for a position inside it - every
    /// such position collapses to this call's own origin, <paramref name="startLine"/>/
    /// <paramref name="startColumn"/>. Returns the token's own start offset within
    /// <see cref="InnerText"/> so the caller can build a <see cref="PlaceholderOccurrence"/> for it
    /// without re-deriving that offset from a string search.
    /// </summary>
    public int AppendPlaceholder(string sourcePath, int startLine, int startColumn, string value)
    {
        var innerStart = _innerText.Length;
        _segments.Add(new Segment(innerStart, value, sourcePath, startLine, startColumn, IsPlaceholder: true));
        _innerText.Append(value);
        return innerStart;
    }

    /// <summary>Maps a 1-based (line, column) position in <see cref="InnerText"/> back to its source origin.</summary>
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
            // No real source text underneath a placeholder token - every position inside it
            // collapses to the token's own origin, rather than running LocateWithinValue's
            // quote-counting/line-delta arithmetic against text this scanner invented.
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
                // The only way a quote character can appear in an already-unescaped literal
                // Value is via a raw `''` pair, so each one seen here represents one extra raw
                // source character that widens the gap between a Value offset and the source
                // column it corresponds to.
                quotesSinceNewline++;
                quotesTotal++;
            }
        }

        var column = lineDelta == 0
            ? contentStartColumn + localOffset + quotesTotal
            : (localOffset - (lastNewlineIndex + 1)) + quotesSinceNewline + 1;

        return (lineDelta, column);
    }

    /// <summary>The last segment whose InnerStart is at or before <paramref name="offset"/>. The first segment always starts at offset 0, so this always finds one - <see cref="Map"/> guards the only way that wouldn't hold (no segments appended yet).</summary>
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
