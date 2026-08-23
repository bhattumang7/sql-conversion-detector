using System.Text;
using System.Text.RegularExpressions;

namespace SilentScan.Core.Parsing;

public static partial class GoBatchSplitter
{
    [GeneratedRegex(@"^GO\s*(\d+)?\s*(--.*|/\*.*\*/)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLinePattern();

    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var state = LexState.Default;
        var commentDepth = 0;

        foreach (var rawLine in SplitLines(script))
        {
            var (line, endState, endCommentDepth) = ScanLine(rawLine, state, commentDepth);

            if (state == LexState.Default && endState == LexState.Default)
            {
                var trimmed = line.Trim();
                var match = GoLinePattern().Match(trimmed);
                if (match.Success)
                {
                    FlushBatch(current, batches, repeatCount: ParseRepeatCount(match));
                    continue;
                }
            }

            current.Append(rawLine).Append('\n');
            state = endState;
            commentDepth = endCommentDepth;
        }

        FlushBatch(current, batches, repeatCount: 1);
        return batches;
    }

public static IReadOnlyList<(int Start, int Length, string Text)> SplitWithSpans(string script)
    {
        var spans = new List<(int Start, int Length, string Text)>();
        var state = LexState.Default;
        var commentDepth = 0;
        var batchStart = -1;
        var batchEnd = 0;

        foreach (var (rawLine, lineStart) in SplitLinesWithOffsets(script))
        {
            var (line, endState, endCommentDepth) = ScanLine(rawLine, state, commentDepth);

            if (state == LexState.Default && endState == LexState.Default)
            {
                var trimmed = line.Trim();
                var match = GoLinePattern().Match(trimmed);
                if (match.Success)
                {
                    FlushSpan(spans, script, batchStart, batchEnd);
                    batchStart = -1;
                    continue;
                }
            }

            if (batchStart < 0)
            {
                batchStart = lineStart;
            }

            batchEnd = lineStart + rawLine.Length;
            state = endState;
            commentDepth = endCommentDepth;
        }

        FlushSpan(spans, script, batchStart, batchEnd);
        return spans;
    }

    private static void FlushSpan(List<(int Start, int Length, string Text)> spans, string script, int start, int end)
    {
        if (start < 0 || start >= end)
        {
            return;
        }

        var raw = script[start..end];
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var leadingWhitespace = raw.Length - raw.TrimStart().Length;
        spans.Add((start + leadingWhitespace, trimmed.Length, trimmed));
    }

    private static int ParseRepeatCount(Match match) =>
        match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var n) && n > 0 ? n : 1;

    private static void FlushBatch(StringBuilder current, List<string> batches, int repeatCount)
    {
        var batch = current.ToString().Trim();
        current.Clear();
        if (batch.Length == 0)
        {
            return;
        }

        for (var i = 0; i < repeatCount; i++)
        {
            batches.Add(batch);
        }
    }

    private static IEnumerable<string> SplitLines(string script)
    {
        var start = 0;
        for (var i = 0; i < script.Length; i++)
        {
            if (script[i] == '\n')
            {
                var end = i > start && script[i - 1] == '\r' ? i - 1 : i;
                yield return script[start..end];
                start = i + 1;
            }
        }

        if (start < script.Length)
        {
            yield return script[start..];
        }
    }

    private static IEnumerable<(string Line, int Start)> SplitLinesWithOffsets(string script)
    {
        var start = 0;
        for (var i = 0; i < script.Length; i++)
        {
            if (script[i] == '\n')
            {
                var end = i > start && script[i - 1] == '\r' ? i - 1 : i;
                yield return (script[start..end], start);
                start = i + 1;
            }
        }

        if (start < script.Length)
        {
            yield return (script[start..], start);
        }
    }

private static (string Line, LexState EndState, int EndCommentDepth) ScanLine(string line, LexState startState, int startCommentDepth)
    {
        var state = startState;
        var commentDepth = startCommentDepth;
        var i = 0;
        while (i < line.Length)
        {
            (i, state, commentDepth) = state switch
            {
                LexState.Default => ScanDefault(line, i),
                LexState.InString => ScanInDelimited(line, i, '\'', LexState.InString),
                LexState.InBracket => ScanInBracket(line, i),
                LexState.InQuotedIdentifier => ScanInDelimited(line, i, '"', LexState.InQuotedIdentifier),
                LexState.InBlockComment => ScanInBlockComment(line, i, commentDepth),
                _ => (i + 1, state, commentDepth),
            };
        }

        return (line, state, commentDepth);
    }

    private static (int NextIndex, LexState NextState, int CommentDepth) ScanDefault(string line, int i)
    {
        if (line[i] == '\'')
        {
            return (i + 1, LexState.InString, 0);
        }

        if (line[i] == '[')
        {
            return (i + 1, LexState.InBracket, 0);
        }

        if (line[i] == '"')
        {
            return (i + 1, LexState.InQuotedIdentifier, 0);
        }

        if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
        {
            return (i + 2, LexState.InBlockComment, 1);
        }

        if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
        {
            return (line.Length, LexState.Default, 0);
        }

        return (i + 1, LexState.Default, 0);
    }

private static (int NextIndex, LexState NextState, int CommentDepth) ScanInDelimited(string line, int i, char delimiter, LexState inState)
    {
        if (line[i] != delimiter)
        {
            return (i + 1, inState, 0);
        }

        return i + 1 < line.Length && line[i + 1] == delimiter
            ? (i + 2, inState, 0)
            : (i + 1, LexState.Default, 0);
    }

private static (int NextIndex, LexState NextState, int CommentDepth) ScanInBracket(string line, int i)
    {
        if (line[i] != ']')
        {
            return (i + 1, LexState.InBracket, 0);
        }

        return i + 1 < line.Length && line[i + 1] == ']'
            ? (i + 2, LexState.InBracket, 0)
            : (i + 1, LexState.Default, 0);
    }

    private static (int NextIndex, LexState NextState, int CommentDepth) ScanInBlockComment(string line, int i, int commentDepth)
    {
        if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
        {
            return (i + 2, LexState.InBlockComment, commentDepth + 1);
        }

        if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
        {
            var nextDepth = commentDepth - 1;
            return nextDepth == 0 ? (i + 2, LexState.Default, 0) : (i + 2, LexState.InBlockComment, nextDepth);
        }

        return (i + 1, LexState.InBlockComment, commentDepth);
    }

    private enum LexState
    {
        Default,
        InString,
        InBracket,
        InQuotedIdentifier,
        InBlockComment,
    }
}
