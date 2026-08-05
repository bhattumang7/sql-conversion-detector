using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// The new engine's entry point - same signature and contract as the old
/// <see cref="DynamicSqlScanner.Scan"/>, so the two can run side by side against the same input
/// during the rebuild (docs/dynamic-sql-rebuild-plan.md Phase 3 exit gate: a parity harness
/// comparing both before any cutover). Walks every batch's top-level statements through
/// <see cref="DynamicSqlCfg"/>/<see cref="DynamicSqlTransfer"/>, which recurses into any nested
/// CREATE/ALTER PROCEDURE, FUNCTION, or TRIGGER body as its own fresh scope.
/// </summary>
public static class DynamicSqlScannerV2
{
    private const int Cap = 32;

    /// <summary>
    /// <paramref name="callGraph"/>/<paramref name="outputSummaryIndex"/>/<paramref name="catalog"/>
    /// are accepted for signature compatibility with <see cref="DynamicSqlScanner.Scan"/> but not
    /// yet consulted - the old scanner's cross-procedure call-graph parameter seeding, OUTPUT-
    /// summary caller seeding, and single-table SELECT-assignment column resolution are each a
    /// precision improvement for a later increment (docs/dynamic-sql-rebuild-plan.md Phase 3 §4),
    /// never a soundness requirement: an unseeded formal parameter simply reports
    /// "variable-not-in-scope" if referenced, exactly like the old scanner's own behavior
    /// whenever no call graph is supplied at all.
    /// </summary>
    public static DynamicSqlExtractionResult Scan(
        SqlParseResult parseResult,
        DynamicSqlScope? enclosingScope = null,
        ProcCallGraph? callGraph = null,
        IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? outputSummaryIndex = null,
        DatabaseCatalog? catalog = null)
    {
        var findings = new List<DynamicSqlFinding>();
        var scripts = new List<DynamicSqlScript>();
        var outputSummaries = new List<ProcedureOutputSummary>();

        if (parseResult.Fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                var context = new TransferContext(
                    new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase),
                    parseResult.SourcePath, Cap, enclosingScope ?? DynamicSqlScope.None,
                    findings, scripts, outputSummaries);

                var cfg = new DynamicSqlCfg(parseResult.SourcePath, Cap, s => DynamicSqlTransfer.CompileLeaf(s, context));
                cfg.Solve(batch.Statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));
            }
        }

        return new DynamicSqlExtractionResult(findings, scripts, outputSummaries);
    }
}
