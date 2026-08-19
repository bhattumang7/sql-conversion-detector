using System.Text;
using System.Text.RegularExpressions;

namespace SilentScan.Verify.Deployment;

/// <summary>
/// Splits a .sql script on GO batch separators. GO is a client-side convention (sqlcmd/SSMS),
/// not T-SQL grammar, so it must be handled before anything reaches the server - ScriptDOM
/// itself already batches on GO when parsing, but the raw deploy path here works directly
/// against SqlClient and needs its own split.
///
/// Splitting is lexer-aware, not a blind line regex over raw text: a line that looks like a GO
/// separator but sits inside a single-quoted string literal, a block comment, or after a line
/// comment marker is real script content, not a batch boundary - a naive `^\s*GO\s*$` regex
/// corrupts both adjacent batches whenever real-world DDL happens to contain one (a seed
/// script's string literal, a commented-out block). `GO n` repeats the preceding batch n times,
/// matching sqlcmd/SSMS semantics.
/// </summary>
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

    /// <summary>
    /// Re-scans one line character by character, tracking whether it ends inside a string
    /// literal, a bracketed identifier (<c>[...]</c>), a double-quoted identifier
    /// (<c>"..."</c> - delimited here regardless of QUOTED_IDENTIFIER, since either way it isn't
    /// a real GO separator underneath it), or a block comment, so the caller never treats a
    /// GO-shaped line inside any of them as a real separator. <paramref name="startCommentDepth"/>
    /// tracks nested block comments (<c>/* ... /* ... */ ... */</c>) - only the OUTERMOST
    /// <c>*/</c> actually exits, matching T-SQL's own nesting behavior.
    /// </summary>
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
            // Line comment: nothing after this point on the line is code, but whatever
            // preceded it still is - just stop scanning this line.
            return (line.Length, LexState.Default, 0);
        }

        return (i + 1, LexState.Default, 0);
    }

    /// <summary>Shared escaping rule for both <c>'...'</c> string literals and <c>"..."</c> quoted identifiers - a doubled delimiter is a literal instance of it, not the end of the region.</summary>
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

    /// <summary>A doubled <c>]]</c> inside a bracketed identifier is a literal <c>]</c>, not the identifier's end - same escaping shape as a quoted string/identifier, but bracket-specific since <c>[</c> itself never needs escaping inside one.</summary>
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
