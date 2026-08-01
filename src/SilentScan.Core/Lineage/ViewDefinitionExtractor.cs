using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Lineage;

/// <summary>Extracts CREATE VIEW / inline TVF / multi-statement TVF definitions from parsed source.</summary>
public static class ViewDefinitionExtractor
{
    public static (List<ViewDefinition> Views, List<MultiStatementTvfDefinition> MultiStatementTvfs) Extract(
        IEnumerable<SqlParseResult> parseResults, Collation? defaultCollation = null, IReadOnlyDictionary<string, SqlType>? typeAliases = null)
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

                    case CreateFunctionStatement { ReturnType: TableValuedFunctionReturnType tableReturn } createFunction
                        when tableReturn.DeclareTableVariableBody.Definition is { } definition:
                        var columns = CatalogBuilder.BuildColumnsForExternalUse(definition, defaultCollation, typeAliases);
                        tvfs.Add(new MultiStatementTvfDefinition(
                            SchemaObjectNameHelper.Qualify(createFunction.Name),
                            columns,
                            result.SourcePath,
                            createFunction.StartLine));
                        break;
                }
            }
        }

        return (views, tvfs);
    }

    private static List<string>? ExplicitColumnNames(IList<Identifier> columns) =>
        columns.Count > 0 ? [.. columns.Select(c => c.Value)] : null;
}
