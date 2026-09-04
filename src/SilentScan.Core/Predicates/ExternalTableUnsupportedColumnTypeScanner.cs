using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ExternalTableUnsupportedColumnTypeScanner
{
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

    public static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
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

    private static bool IsUnsupported(SqlType type) =>
        UnsupportedCategories.Contains(type.Category) || (type.IsMax && MaxLengthUnsupportedCategories.Contains(type.Category));

    private sealed class Visitor(string sourcePath, IReadOnlyDictionary<string, SqlType>? typeAliases) : TSqlFragmentVisitor
    {
        public List<ExternalTableUnsupportedColumnTypeFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateExternalTableStatement node)
        {
            var tableName = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);

            foreach (var columnDefinition in node.ColumnDefinitions)
            {
                var column = columnDefinition.ColumnDefinition;
                if (column?.ColumnIdentifier is not { Value: { } columnName }
                    || SqlTypeReferenceResolver.Resolve(column.DataType, columnCollation: null, typeAliases) is not { } type
                    || !IsUnsupported(type))
                {
                    continue;
                }

                Findings.Add(new ExternalTableUnsupportedColumnTypeFinding(
                    tableName, columnName, type.ToString(), sourcePath, column.StartLine, column.StartColumn));
            }

            base.ExplicitVisit(node);
        }
    }
}
