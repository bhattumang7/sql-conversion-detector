using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

/// <summary>Extracts CREATE/ALTER/CREATE OR ALTER VIEW, inline TVF and multi-statement TVF definitions from parsed source.</summary>
public static class ViewDefinitionExtractor
{
    /// <summary>The context threaded through every TVF column-shape resolution in one <see cref="Extract"/> call - bundled so <see cref="AddTvf"/> stays under Sonar's parameter-count limit.</summary>
    private readonly record struct TvfContext(Collation? DefaultCollation, IReadOnlyDictionary<string, SqlType>? TypeAliases, SkipLedger? Ledger);

    public static (List<ViewDefinition> Views, List<MultiStatementTvfDefinition> MultiStatementTvfs) Extract(
        IEnumerable<SqlParseResult> parseResults, Collation? defaultCollation = null, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null)
    {
        // Dictionaries keyed by qualified name, upserted/removed in file-then-statement order -
        // not a flat list dedup'd afterward - so DROP VIEW/DROP FUNCTION can participate in the
        // same "last event wins" model CatalogBuilder uses for tables (catalog lifecycle: a
        // dropped-and-never-recreated view/TVF disappears entirely rather than leaving a stale
        // definition; a drop immediately followed by a recreate in the same file set still ends
        // up with the recreated shape, since the recreate's upsert simply runs after the drop's
        // removal in this same ordered walk).
        var viewsByName = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        var tvfsByName = new Dictionary<string, MultiStatementTvfDefinition>(StringComparer.OrdinalIgnoreCase);
        var tvfContext = new TvfContext(defaultCollation, typeAliases, ledger);

        foreach (var result in parseResults)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                ApplyStatement(statement, result.SourcePath, viewsByName, tvfsByName, tvfContext);
            }
        }

        return ([.. viewsByName.Values], [.. tvfsByName.Values]);
    }

    /// <summary>
    /// ALTER VIEW / CREATE OR ALTER VIEW and the ALTER/CREATE OR ALTER forms of a view-shaped
    /// function are distinct ScriptDOM node types from their CREATE-only counterparts (the same
    /// double-dispatch trap TypedPredicateExtractor and CatalogBuilder both had to guard against
    /// for procedures/functions/triggers) - matched here on the same shape as CREATE so a
    /// redefinition through any of them resolves into lineage identically, not just CREATE
    /// (coverage-remediation-plan.md Phase 2.1).
    /// </summary>
    private static void ApplyStatement(
        TSqlStatement statement, string sourcePath,
        Dictionary<string, ViewDefinition> viewsByName, Dictionary<string, MultiStatementTvfDefinition> tvfsByName, TvfContext tvfContext)
    {
        switch (statement)
        {
            case CreateViewStatement createView:
                UpsertView(viewsByName, createView.SchemaObjectName, createView.SelectStatement, createView.Columns, sourcePath, createView.StartLine);
                break;

            case AlterViewStatement alterView:
                UpsertView(viewsByName, alterView.SchemaObjectName, alterView.SelectStatement, alterView.Columns, sourcePath, alterView.StartLine);
                break;

            case CreateOrAlterViewStatement createOrAlterView:
                UpsertView(viewsByName, createOrAlterView.SchemaObjectName, createOrAlterView.SelectStatement, createOrAlterView.Columns, sourcePath, createOrAlterView.StartLine);
                break;

            case CreateFunctionStatement { ReturnType: SelectFunctionReturnType inlineReturn } createFunction:
                UpsertView(viewsByName, createFunction.Name, inlineReturn.SelectStatement, columns: null, sourcePath, createFunction.StartLine);
                break;

            case AlterFunctionStatement { ReturnType: SelectFunctionReturnType alterInlineReturn } alterFunction:
                UpsertView(viewsByName, alterFunction.Name, alterInlineReturn.SelectStatement, columns: null, sourcePath, alterFunction.StartLine);
                break;

            case CreateOrAlterFunctionStatement { ReturnType: SelectFunctionReturnType coaInlineReturn } createOrAlterFunction:
                UpsertView(viewsByName, createOrAlterFunction.Name, coaInlineReturn.SelectStatement, columns: null, sourcePath, createOrAlterFunction.StartLine);
                break;

            // RETURNS TABLE always carries an explicit column list in valid T-SQL, whether the
            // body is a multi-statement RETURNS @t TABLE(...) or a CLR RETURNS TABLE (...) AS
            // EXTERNAL NAME - so DeclareTableVariableBody.Definition is never null here on a
            // successful parse; this covers both shapes identically (a CLR TVF's declared
            // return shape is exactly as authoritative as a T-SQL MSTVF's, unlike a CLR scalar
            // function's return type, which has no local declaration to read at all).
            case CreateFunctionStatement { ReturnType: TableValuedFunctionReturnType tableReturn } createFunction:
                AddTvf(tvfsByName, createFunction.Name, tableReturn, createFunction.StartLine, sourcePath, tvfContext);
                break;

            case AlterFunctionStatement { ReturnType: TableValuedFunctionReturnType alterTableReturn } alterFunction:
                AddTvf(tvfsByName, alterFunction.Name, alterTableReturn, alterFunction.StartLine, sourcePath, tvfContext);
                break;

            case CreateOrAlterFunctionStatement { ReturnType: TableValuedFunctionReturnType coaTableReturn } createOrAlterFunction:
                AddTvf(tvfsByName, createOrAlterFunction.Name, coaTableReturn, createOrAlterFunction.StartLine, sourcePath, tvfContext);
                break;

            case DropViewStatement dropView:
                foreach (var target in dropView.Objects)
                {
                    viewsByName.Remove(SchemaObjectNameHelper.Qualify(target));
                }

                break;

            // DROP FUNCTION doesn't say up front whether the function was scalar, inline-TVF, or
            // multi-statement TVF - a scalar UDF's registry entry lives in DatabaseCatalog
            // (removed by CatalogBuilder's own DropFunctionStatement visitor), so this only
            // needs to cover the two view-shaped forms this extractor itself tracks. Removing a
            // name from whichever dictionary never held it is a harmless no-op.
            case DropFunctionStatement dropFunction:
                foreach (var target in dropFunction.Objects)
                {
                    var qualifiedName = SchemaObjectNameHelper.Qualify(target);
                    viewsByName.Remove(qualifiedName);
                    tvfsByName.Remove(qualifiedName);
                }

                break;
        }
    }

    private static void UpsertView(
        Dictionary<string, ViewDefinition> viewsByName, SchemaObjectName name, SelectStatement selectStatement, IList<Identifier>? columns, string sourcePath, int startLine)
    {
        var qualifiedName = SchemaObjectNameHelper.Qualify(name);
        viewsByName[qualifiedName] = new ViewDefinition(
            qualifiedName, selectStatement, columns is null ? null : ExplicitColumnNames(columns), sourcePath, startLine);
    }

    private static void AddTvf(
        Dictionary<string, MultiStatementTvfDefinition> tvfsByName, SchemaObjectName name, TableValuedFunctionReturnType tableReturn, int startLine, string sourcePath, TvfContext context)
    {
        var columns = CatalogBuilder.BuildColumnsForExternalUse(
            tableReturn.DeclareTableVariableBody.Definition, context.DefaultCollation, context.TypeAliases, context.Ledger, sourcePath);
        var qualifiedName = SchemaObjectNameHelper.Qualify(name);
        tvfsByName[qualifiedName] = new MultiStatementTvfDefinition(qualifiedName, columns, sourcePath, startLine);
    }

    private static List<string>? ExplicitColumnNames(IList<Identifier> columns) =>
        columns.Count > 0 ? [.. columns.Select(c => c.Value)] : null;
}
