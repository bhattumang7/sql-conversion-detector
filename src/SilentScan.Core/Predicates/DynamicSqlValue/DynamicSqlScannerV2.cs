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
    /// <paramref name="callGraph"/> seeds a proc body's own formal parameters from what its known
    /// callers pass (<see cref="DynamicSqlTransfer.CompileLeaf"/>'s own <c>CompileScopedBody</c>);
    /// <paramref name="outputSummaryIndex"/> seeds an ordinary EXEC's own OUTPUT arguments from a
    /// callee's already-proven-constant OUTPUT parameter. Both null (the common case in isolated/
    /// unit-tested scans) leaves every formal parameter unseeded, reporting
    /// "variable-not-in-scope" if referenced - exactly the old scanner's own behavior whenever no
    /// call graph is supplied at all, so this is purely additive precision, never a soundness
    /// requirement. <paramref name="catalog"/> is accepted for signature compatibility with
    /// <see cref="DynamicSqlScanner.Scan"/> but not yet consulted - single-table SELECT-assignment
    /// column resolution remains a deferred precision improvement.
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
                    findings, scripts, outputSummaries, callGraph, outputSummaryIndex);

                var cfg = new DynamicSqlCfg(parseResult.SourcePath, Cap, s => DynamicSqlTransfer.CompileLeaf(s, context));
                cfg.Solve(batch.Statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));
            }
        }

        return new DynamicSqlExtractionResult(findings, scripts, outputSummaries);
    }
}
