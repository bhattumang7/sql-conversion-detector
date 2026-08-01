using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Lineage;

/// <summary>Extracts CREATE VIEW / inline TVF / multi-statement TVF definitions from parsed source.</summary>
public static class ViewDefinitionExtractor
{
    private const string ViewOrTvfDefinerConstructKind = "view/TVF definer";

    public static (List<ViewDefinition> Views, List<MultiStatementTvfDefinition> MultiStatementTvfs) Extract(
        IEnumerable<SqlParseResult> parseResults, Collation? defaultCollation = null, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null)
    {
        var views = new List<ViewDefinition>();
        var tvfs = new List<MultiStatementTvfDefinition>();

        foreach (var result in parseResults)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                switch (statement)
                {
                    case CreateViewStatement createView:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(createView.SchemaObjectName),
                            createView.SelectStatement,
                            ExplicitColumnNames(createView.Columns),
                            result.SourcePath,
                            createView.StartLine));
                        break;

                    case CreateFunctionStatement { ReturnType: SelectFunctionReturnType inlineReturn } createFunction:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(createFunction.Name),
                            inlineReturn.SelectStatement,
                            ExplicitColumnNames: null,
                            result.SourcePath,
                            createFunction.StartLine));
                        break;

                    // RETURNS TABLE always carries an explicit column list in valid T-SQL, whether
                    // the body is a multi-statement RETURNS @t TABLE(...) or a CLR RETURNS TABLE
                    // (...) AS EXTERNAL NAME - so DeclareTableVariableBody.Definition is never
                    // null here on a successful parse; this covers both shapes identically (a
                    // CLR TVF's declared return shape is exactly as authoritative as a T-SQL
                    // MSTVF's, unlike a CLR scalar function's return type, which has no local
                    // declaration to read at all).
                    case CreateFunctionStatement { ReturnType: TableValuedFunctionReturnType tableReturn } createFunction:
                        var columns = CatalogBuilder.BuildColumnsForExternalUse(tableReturn.DeclareTableVariableBody.Definition, defaultCollation, typeAliases, ledger, result.SourcePath);
                        tvfs.Add(new MultiStatementTvfDefinition(
                            SchemaObjectNameHelper.Qualify(createFunction.Name),
                            columns,
                            result.SourcePath,
                            createFunction.StartLine));
                        break;

                    // ALTER VIEW / CREATE OR ALTER VIEW and the ALTER/CREATE OR ALTER forms of a
                    // view-shaped function are distinct ScriptDOM node types from their CREATE-only
                    // counterparts matched above (the same double-dispatch trap TypedPredicateExtractor
                    // and CatalogBuilder both had to guard against for procedures/functions) - not yet
                    // resolved into lineage, so a redefinition through any of these is invisible to the
                    // view-inheritance analysis rather than silently mis-typed (coverage-remediation-plan.md
                    // Phase 2.1).
                    case AlterViewStatement alterView:
                        ledger?.Record(
                            AnalysisPass.Lineage, result.SourcePath, alterView.StartLine, alterView.StartColumn,
                            ViewOrTvfDefinerConstructKind, $"'{SchemaObjectNameHelper.Qualify(alterView.SchemaObjectName)}' redefined via ALTER VIEW - not yet resolved into lineage");
                        break;

                    case CreateOrAlterViewStatement createOrAlterView:
                        ledger?.Record(
                            AnalysisPass.Lineage, result.SourcePath, createOrAlterView.StartLine, createOrAlterView.StartColumn,
                            ViewOrTvfDefinerConstructKind, $"'{SchemaObjectNameHelper.Qualify(createOrAlterView.SchemaObjectName)}' redefined via CREATE OR ALTER VIEW - not yet resolved into lineage");
                        break;

                    case AlterFunctionStatement { ReturnType: SelectFunctionReturnType or TableValuedFunctionReturnType } alterFunction:
                        ledger?.Record(
                            AnalysisPass.Lineage, result.SourcePath, alterFunction.StartLine, alterFunction.StartColumn,
                            ViewOrTvfDefinerConstructKind, $"'{SchemaObjectNameHelper.Qualify(alterFunction.Name)}' redefined via ALTER FUNCTION - not yet resolved into lineage");
                        break;

                    case CreateOrAlterFunctionStatement { ReturnType: SelectFunctionReturnType or TableValuedFunctionReturnType } createOrAlterFunction:
                        ledger?.Record(
                            AnalysisPass.Lineage, result.SourcePath, createOrAlterFunction.StartLine, createOrAlterFunction.StartColumn,
                            ViewOrTvfDefinerConstructKind, $"'{SchemaObjectNameHelper.Qualify(createOrAlterFunction.Name)}' redefined via CREATE OR ALTER FUNCTION - not yet resolved into lineage");
                        break;
                }
            }
        }

        return (views, tvfs);
    }

    private static List<string>? ExplicitColumnNames(IList<Identifier> columns) =>
        columns.Count > 0 ? [.. columns.Select(c => c.Value)] : null;
}
