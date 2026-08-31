using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class ViewDefinitionExtractor
{
    private readonly record struct TvfContext(Collation? DefaultCollation, IReadOnlyDictionary<string, SqlType>? TypeAliases, SkipLedger? Ledger);

    public static (List<ViewDefinition> Views, List<MultiStatementTvfDefinition> MultiStatementTvfs) Extract(
        IEnumerable<SqlParseResult> parseResults, Collation? defaultCollation = null, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null,
        IScanStage? stage = null)
    {

        var identifierComparer = Collation.IdentifierComparer(defaultCollation);
        var viewsByName = new Dictionary<string, ViewDefinition>(identifierComparer);
        var tvfsByName = new Dictionary<string, MultiStatementTvfDefinition>(identifierComparer);
        var tvfContext = new TvfContext(defaultCollation, typeAliases, ledger);

        foreach (var result in parseResults)
        {
            stage?.Advance(currentItem: result.SourcePath);

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
