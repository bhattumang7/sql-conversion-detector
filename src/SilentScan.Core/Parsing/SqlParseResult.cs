using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Parsing;

/// <summary>The outcome of parsing one .sql file: the fragment tree plus any parse errors ScriptDOM tolerated.</summary>
public sealed record SqlParseResult(
    string SourcePath,
    TSqlFragment Fragment,
    IReadOnlyList<ParseError> Errors,
    IReadOnlyList<UnanalyzedBatch> UnanalyzedBatches)
{
    public SqlParseResult(string sourcePath, TSqlFragment fragment, IReadOnlyList<ParseError> errors)
        : this(sourcePath, fragment, errors, [])
    {
    }

    public bool HasErrors => Errors.Count > 0;

    /// <summary>
    /// Batches that survived to parse cleanly (docs/audit-remediation-plan.md Phase 4.4) - a
    /// GO-separated batch containing a syntax error is dropped by ScriptDOM itself and does not
    /// appear here, so this can be greater than zero even when <see cref="HasErrors"/> is true.
    /// <see cref="UnanalyzedBatches"/> is the best-effort record of what a dropped batch was.
    /// </summary>
    public int BatchCount => Fragment is TSqlScript script ? script.Batches.Count : 0;
}
