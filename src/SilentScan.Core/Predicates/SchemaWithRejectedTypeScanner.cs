using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class SchemaWithRejectedTypeScanner
{
    public static IReadOnlyList<SchemaWithRejectedTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog?.TypeAliases);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static SchemaWithRejectedTypeKind? ClassifyOpenXmlColumn(SqlTypeCategory category) => category switch
    {
        SqlTypeCategory.HierarchyId or SqlTypeCategory.Geometry or SqlTypeCategory.Geography => SchemaWithRejectedTypeKind.OpenXmlClrType,
        _ => null,
    };

    private static SchemaWithRejectedTypeKind? ClassifyOpenRowsetColumn(SqlTypeCategory category) => category switch
    {
        SqlTypeCategory.SqlVariant or SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image => SchemaWithRejectedTypeKind.OpenRowsetLegacyType,
        SqlTypeCategory.HierarchyId or SqlTypeCategory.Geometry or SqlTypeCategory.Geography => SchemaWithRejectedTypeKind.OpenRowsetClrType,
        SqlTypeCategory.Xml => SchemaWithRejectedTypeKind.OpenRowsetXml,
        _ => null,
    };

    private sealed class Visitor(string sourcePath, IReadOnlyDictionary<string, SqlType>? typeAliases) : TSqlFragmentVisitor
    {
        public List<SchemaWithRejectedTypeFinding> Findings { get; } = [];

        public override void ExplicitVisit(OpenXmlTableReference node)
        {
            foreach (var item in node.SchemaDeclarationItems)
            {
                InspectColumn(item.ColumnDefinition, ClassifyOpenXmlColumn);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BulkOpenRowset node)
        {
            foreach (var column in node.WithColumns)
            {
                InspectColumn(column, ClassifyOpenRowsetColumn);
            }

            base.ExplicitVisit(node);
        }

        private void InspectColumn(ColumnDefinitionBase column, Func<SqlTypeCategory, SchemaWithRejectedTypeKind?> classify)
        {
            if (column.ColumnIdentifier is not { Value: { } columnName }
                || SqlTypeReferenceResolver.Resolve(column.DataType, columnCollation: null, typeAliases) is not { } type
                || classify(type.Category) is not { } kind)
            {
                return;
            }

            Findings.Add(new SchemaWithRejectedTypeFinding(columnName, type.ToString(), kind, sourcePath, column.StartLine, column.StartColumn));
        }
    }
}
