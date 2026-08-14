using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// The dynamic-SQL engine's entry point - the sole production implementation (the earlier
/// engine this one replaced during the V1-to-V2 rebuild has since been deleted). Walks every
/// batch's top-level statements through <see cref="DynamicSqlCfg"/>/<see cref="DynamicSqlTransfer"/>,
/// which recurses into any nested CREATE/ALTER PROCEDURE, FUNCTION, or TRIGGER body as its own
/// fresh scope.
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
    /// "variable-not-in-scope" if referenced - a call graph is purely additive precision, never a
    /// soundness requirement. <paramref name="catalog"/>, when supplied, lets
    /// <see cref="DynamicSqlTransfer.CompileLeaf"/>'s own SELECT-assignment handling splice a
    /// single catalog-known table's own column types into an otherwise-tainted
    /// <c>SELECT @var = expr FROM table</c> shape (see
    /// <c>TryCompileSelectAssignmentFromSingleKnownTable</c>) - null leaves that shape exactly as
    /// tainted/havoc'd as it always was. <paramref name="rowValueFetcher"/>, when supplied
    /// (scan-db's own opt-in <c>--fetch-sql-from-tables</c> flag only - never file-mode/corpus,
    /// which has no live connection to fetch through), lets that same SELECT-assignment splice
    /// go one step further: when the WHERE clause pins the row down to a literal-equality key,
    /// the real value is fetched and spliced in as a genuine literal instead of a
    /// <see cref="HoleKind.RowDependentColumn"/> hole. Null (the default) leaves that shape
    /// exactly as it was - purely additive precision, never a soundness requirement.
    /// </summary>
    public static DynamicSqlExtractionResult Scan(
        SqlParseResult parseResult,
        DynamicSqlScope? enclosingScope = null,
        ProcCallGraph? callGraph = null,
        IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? outputSummaryIndex = null,
        DatabaseCatalog? catalog = null,
        ILiveRowValueFetcher? rowValueFetcher = null)
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
                    findings, scripts, outputSummaries, callGraph, outputSummaryIndex, catalog, rowValueFetcher);
                SolveBatch(batch, context);
            }
        }

        return new DynamicSqlExtractionResult(findings, scripts, outputSummaries);
    }

    /// <summary>
    /// Pre-seeds every DECLARE found anywhere in the batch (see <see cref="DynamicSqlTransfer.SeedBatchDeclaredVariables"/>
    /// for why - T-SQL's own DECLARE is batch-scoped, not block-scoped) before handing the batch
    /// to the CFG solver.
    /// </summary>
    private static void SolveBatch(TSqlBatch batch, TransferContext context)
    {
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        DynamicSqlTransfer.SeedBatchDeclaredVariables(batch.Statements, context, seed);

        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => DynamicSqlTransfer.CompileLeaf(s, activeGuards, context));
        cfg.Solve(batch.Statements, seed);
    }
}
