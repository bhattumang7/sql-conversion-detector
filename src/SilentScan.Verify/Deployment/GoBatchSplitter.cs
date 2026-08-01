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
    [GeneratedRegex(@"^GO\s*(\d+)?\s*(--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLinePattern();

    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var state = LexState.Default;

        foreach (var rawLine in SplitLines(script))
        {
            var (line, endState) = ScanLine(rawLine, state);

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

    /// <summary>Re-scans one line character by character, tracking whether it ends inside a string literal or block comment so the caller never treats a GO-shaped line inside either as a real separator.</summary>
    private static (string Line, LexState EndState) ScanLine(string line, LexState startState)
    {
        var state = startState;
        var i = 0;
        while (i < line.Length)
        {
            switch (state)
            {
                case LexState.Default:
                    if (line[i] == '\'')
                    {
                        state = LexState.InString;
                        i++;
                    }
                    else if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                    {
                        state = LexState.InBlockComment;
                        i += 2;
                    }
                    else if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
                    {
                        // Line comment: nothing after this point on the line is code, but
                        // whatever preceded it still is - just stop scanning this line.
                        i = line.Length;
                    }
                    else
                    {
                        i++;
                    }

                    break;

                case LexState.InString:
                    if (line[i] == '\'')
                    {
                        // A doubled '' is an escaped quote, not the string's end.
                        if (i + 1 < line.Length && line[i + 1] == '\'')
                        {
                            i += 2;
                        }
                        else
                        {
                            state = LexState.Default;
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }

                    break;

                case LexState.InBlockComment:
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                    {
                        state = LexState.Default;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;
            }
        }

        return (line, state);
    }

    private enum LexState
    {
        Default,
        InString,
        InBlockComment,
    }
}
