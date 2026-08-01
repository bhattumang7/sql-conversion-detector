using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Lineage;

/// <summary>Extracts CREATE/ALTER/CREATE OR ALTER VIEW, inline TVF and multi-statement TVF definitions from parsed source.</summary>
public static class ViewDefinitionExtractor
{
    /// <summary>The context threaded through every TVF column-shape resolution in one <see cref="Extract"/> call - bundled so <see cref="AddTvf"/> stays under Sonar's parameter-count limit.</summary>
    private readonly record struct TvfContext(Collation? DefaultCollation, IReadOnlyDictionary<string, SqlType>? TypeAliases, SkipLedger? Ledger);

    public static (List<ViewDefinition> Views, List<MultiStatementTvfDefinition> MultiStatementTvfs) Extract(
        IEnumerable<SqlParseResult> parseResults, Collation? defaultCollation = null, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null)
    {
        var views = new List<ViewDefinition>();
        var tvfs = new List<MultiStatementTvfDefinition>();
        var tvfContext = new TvfContext(defaultCollation, typeAliases, ledger);

        foreach (var result in parseResults)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                // ALTER VIEW / CREATE OR ALTER VIEW and the ALTER/CREATE OR ALTER forms of a
                // view-shaped function are distinct ScriptDOM node types from their CREATE-only
                // counterparts (the same double-dispatch trap TypedPredicateExtractor and
                // CatalogBuilder both had to guard against for procedures/functions/triggers) -
                // matched here on the same shape as CREATE so a redefinition through any of them
                // resolves into lineage identically, not just CREATE (coverage-remediation-plan.md
                // Phase 2.1). ViewDependencyGraph.TopologicalSort already applies "last definition
                // in source order wins" across every ViewDefinition sharing a qualified name, so a
                // CREATE VIEW stub followed by an ALTER VIEW with the real body naturally resolves
                // to the ALTER's body with no extra handling here.
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

                    case AlterViewStatement alterView:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(alterView.SchemaObjectName),
                            alterView.SelectStatement,
                            ExplicitColumnNames(alterView.Columns),
                            result.SourcePath,
                            alterView.StartLine));
                        break;

                    case CreateOrAlterViewStatement createOrAlterView:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(createOrAlterView.SchemaObjectName),
                            createOrAlterView.SelectStatement,
                            ExplicitColumnNames(createOrAlterView.Columns),
                            result.SourcePath,
                            createOrAlterView.StartLine));
                        break;

                    case CreateFunctionStatement { ReturnType: SelectFunctionReturnType inlineReturn } createFunction:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(createFunction.Name),
                            inlineReturn.SelectStatement,
                            ExplicitColumnNames: null,
                            result.SourcePath,
                            createFunction.StartLine));
                        break;

                    case AlterFunctionStatement { ReturnType: SelectFunctionReturnType alterInlineReturn } alterFunction:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(alterFunction.Name),
                            alterInlineReturn.SelectStatement,
                            ExplicitColumnNames: null,
                            result.SourcePath,
                            alterFunction.StartLine));
                        break;

                    case CreateOrAlterFunctionStatement { ReturnType: SelectFunctionReturnType coaInlineReturn } createOrAlterFunction:
                        views.Add(new ViewDefinition(
                            SchemaObjectNameHelper.Qualify(createOrAlterFunction.Name),
                            coaInlineReturn.SelectStatement,
                            ExplicitColumnNames: null,
                            result.SourcePath,
                            createOrAlterFunction.StartLine));
                        break;

                    // RETURNS TABLE always carries an explicit column list in valid T-SQL, whether
                    // the body is a multi-statement RETURNS @t TABLE(...) or a CLR RETURNS TABLE
                    // (...) AS EXTERNAL NAME - so DeclareTableVariableBody.Definition is never
                    // null here on a successful parse; this covers both shapes identically (a
                    // CLR TVF's declared return shape is exactly as authoritative as a T-SQL
                    // MSTVF's, unlike a CLR scalar function's return type, which has no local
                    // declaration to read at all).
                    case CreateFunctionStatement { ReturnType: TableValuedFunctionReturnType tableReturn } createFunction:
                        AddTvf(tvfs, createFunction.Name, tableReturn, createFunction.StartLine, result.SourcePath, tvfContext);
                        break;

                    case AlterFunctionStatement { ReturnType: TableValuedFunctionReturnType alterTableReturn } alterFunction:
                        AddTvf(tvfs, alterFunction.Name, alterTableReturn, alterFunction.StartLine, result.SourcePath, tvfContext);
                        break;

                    case CreateOrAlterFunctionStatement { ReturnType: TableValuedFunctionReturnType coaTableReturn } createOrAlterFunction:
                        AddTvf(tvfs, createOrAlterFunction.Name, coaTableReturn, createOrAlterFunction.StartLine, result.SourcePath, tvfContext);
                        break;
                }
            }
        }

        return (views, tvfs);
    }

    private static void AddTvf(
        List<MultiStatementTvfDefinition> tvfs, SchemaObjectName name, TableValuedFunctionReturnType tableReturn, int startLine, string sourcePath, TvfContext context)
    {
        var columns = CatalogBuilder.BuildColumnsForExternalUse(
            tableReturn.DeclareTableVariableBody.Definition, context.DefaultCollation, context.TypeAliases, context.Ledger, sourcePath);
        tvfs.Add(new MultiStatementTvfDefinition(SchemaObjectNameHelper.Qualify(name), columns, sourcePath, startLine));
    }

    private static List<string>? ExplicitColumnNames(IList<Identifier> columns) =>
        columns.Count > 0 ? [.. columns.Select(c => c.Value)] : null;
}
