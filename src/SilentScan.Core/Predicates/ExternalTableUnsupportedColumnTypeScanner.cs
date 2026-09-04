using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ExternalTableUnsupportedColumnTypeScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly HashSet<SqlTypeCategory> UnsupportedCategories =
    [
        SqlTypeCategory.SqlVariant,
        SqlTypeCategory.Xml,
        SqlTypeCategory.HierarchyId,
        SqlTypeCategory.Geometry,
        SqlTypeCategory.Geography,
        SqlTypeCategory.NText,
        SqlTypeCategory.Text,
        SqlTypeCategory.Image,
        SqlTypeCategory.Timestamp,
    ];

    private static readonly HashSet<SqlTypeCategory> MaxLengthUnsupportedCategories =
    [
        SqlTypeCategory.VarChar,
        SqlTypeCategory.NVarChar,
        SqlTypeCategory.VarBinary,
    ];

    public static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static bool IsUnsupported(SqlType type) =>
        UnsupportedCategories.Contains(type.Category) || (type.IsMax && MaxLengthUnsupportedCategories.Contains(type.Category));

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly Dictionary<QuerySpecification, string> pendingCetasQueries = new(ReferenceEqualityComparer.Instance);

        public List<ExternalTableUnsupportedColumnTypeFinding> Findings { get; } = [];

        public void OnEnterCreateExternalTableStatement(CreateExternalTableStatement node, ModuleWalker walker)
        {
            var tableName = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);

            foreach (var columnDefinition in node.ColumnDefinitions)
            {
                var column = columnDefinition.ColumnDefinition;
                if (column?.ColumnIdentifier is not { Value: { } columnName }
                    || SqlTypeReferenceResolver.Resolve(column.DataType, columnCollation: null, catalog.TypeAliases) is not { } type
                    || !IsUnsupported(type))
                {
                    continue;
                }

                Findings.Add(new ExternalTableUnsupportedColumnTypeFinding(
                    tableName, columnName, type.ToString(), sourcePath, column.StartLine, column.StartColumn));
            }

            if (node.SelectStatement?.QueryExpression is QuerySpecification querySpecification)
            {
                pendingCetasQueries[querySpecification] = tableName;
            }
        }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (!pendingCetasQueries.Remove(node, out var tableName))
            {
                return;
            }

            for (var ordinal = 0; ordinal < node.SelectElements.Count; ordinal++)
            {
                if (node.SelectElements[ordinal] is not SelectScalarExpression { Expression: { } expression } scalarElement)
                {
                    continue;
                }

                var type = ScalarExpressionResolver.ResolveScalarType(
                    expression, scopeChain, sourcePath, new ScalarExpressionResolver.ScalarTypeContext(Ledger: null, catalog.TypeAliases, catalog));
                if (type is null || !IsUnsupported(type))
                {
                    continue;
                }

                var columnName = scalarElement.ColumnName?.Identifier?.Value
                    ?? (expression as ColumnReferenceExpression)?.MultiPartIdentifier.Identifiers[^1].Value
                    ?? $"(column {ordinal + 1})";

                Findings.Add(new ExternalTableUnsupportedColumnTypeFinding(
                    tableName, columnName, type.ToString(), sourcePath, expression.StartLine, expression.StartColumn));
            }
        }
    }
}
