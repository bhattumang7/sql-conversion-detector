namespace SilentScan.Core.Predicates;

/// <summary>
/// How a dynamic SQL call site's argument was (or wasn't) resolved. CLAUDE.md's dynamic SQL
/// policy: never silently count a call site as clean - every outcome here is either a real
/// analysis result or an honest, specific reason it couldn't be one.
/// </summary>
public enum DynamicSqlOutcome
{
    /// <summary>The argument was provably constant (a literal, or a concatenation of bare literals) and its reassembled text was successfully reparsed and run through the normal pipeline.</summary>
    AnalyzedLiteral,

    /// <summary>The argument depends on a variable, parameter, or expression - tracing its runtime value would mean guessing, which this tool never does.</summary>
    Unanalyzable,

    /// <summary>The argument was provably constant, but the reassembled text did not parse as T-SQL (e.g. it targets a different dialect, or is itself malformed).</summary>
    InnerParseFailed,
}

/// <summary>An EXEC(@sql)/EXEC('...')/sp_executesql call site, and how its argument was resolved.</summary>
public sealed record DynamicSqlFinding(string SourcePath, int Line, int Column, DynamicSqlOutcome Outcome, string? Reason);
