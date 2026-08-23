using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public static class DynamicSqlScannerV2
{
    private const int Cap = 32;

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

    private static void SolveBatch(TSqlBatch batch, TransferContext context)
    {
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        DynamicSqlTransfer.SeedBatchDeclaredVariables(batch.Statements, context, seed);

        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => DynamicSqlTransfer.CompileLeaf(s, activeGuards, context));
        cfg.Solve(batch.Statements, seed);
    }
}
