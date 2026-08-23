using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Parsing;

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

public int BatchCount => Fragment is TSqlScript script ? script.Batches.Count : 0;
}
