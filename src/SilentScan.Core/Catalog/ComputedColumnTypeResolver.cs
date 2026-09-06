using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Catalog;

internal static class ComputedColumnTypeResolver
{
    public static List<CatalogColumn> ResolveAll(
        List<CatalogColumn> columns, IReadOnlyDictionary<string, ScalarExpression> computedExpressions, IReadOnlyDictionary<string, SqlType>? typeAliases,
        StringComparer? identifierComparer = null)
    {
        if (computedExpressions.Count == 0)
        {
            return columns;
        }

        for (var iteration = 0; iteration < computedExpressions.Count; iteration++)
        {
            var typesByName = columns
                .Where(c => c.Type is not null)
                .ToDictionary(c => c.Name, c => c.Type, identifierComparer ?? StringComparer.OrdinalIgnoreCase);

            if (!TryResolveOnePass(columns, computedExpressions, typesByName, typeAliases, out var next))
            {
                break;
            }

            columns = next;
        }

        return columns;
    }

    private static bool TryResolveOnePass(
        List<CatalogColumn> columns, IReadOnlyDictionary<string, ScalarExpression> computedExpressions,
        Dictionary<string, SqlType?> typesByName, IReadOnlyDictionary<string, SqlType>? typeAliases, out List<CatalogColumn> result)
    {
        var progressed = false;
        var next = new List<CatalogColumn>(columns.Count);

        foreach (var column in columns)
        {
            if (column.Type is not null || !computedExpressions.TryGetValue(column.Name, out var expression))
            {
                next.Add(column);
                continue;
            }

            var resolved = Resolve(expression, typesByName, typeAliases);
            if (resolved is null)
            {
                next.Add(column);
                continue;
            }

            progressed = true;
            next.Add(column with { Type = resolved });
        }

        result = progressed ? next : columns;
        return progressed;
    }

    private static SqlType? Resolve(
        ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes, IReadOnlyDictionary<string, SqlType>? typeAliases) =>
        TypeInference.ExpressionTypeInferencer.Resolve(expression, e => ResolveLeaf(e, columnTypes), typeAliases);

    private static SqlType? ResolveLeaf(ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes) => expression switch
    {
        ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] } =>
            columnTypes.GetValueOrDefault(last.Value),

        _ => null,
    };

    private static readonly HashSet<string> AlwaysImpreciseFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "STR", "GREATEST", "LEAST",
    };

    public static bool IsImprecise(
        ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> columnTypes, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        var visitor = new ImpreciseVisitor(columnTypes, typeAliases);
        expression.Accept(visitor);
        return visitor.Found;
    }

    private sealed class ImpreciseVisitor(IReadOnlyDictionary<string, SqlType?> columnTypes, IReadOnlyDictionary<string, SqlType>? typeAliases) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (AlwaysImpreciseFunctionNames.Contains(node.FunctionName.Value) || IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BinaryExpression node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CastCall node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ConvertCall node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryCastCall node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryConvertCall node)
        {
            if (IsFloatFamily(node))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParseCall node)
        {
            if (IsFloatFamilyDataType(node.DataType))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryParseCall node)
        {
            if (IsFloatFamilyDataType(node.DataType))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        private bool IsFloatFamily(ScalarExpression node) =>
            Resolve(node, columnTypes, typeAliases) is { Category: SqlTypeCategory.Float or SqlTypeCategory.Real };

        private static bool IsFloatFamilyDataType(DataTypeReference dataType) =>
            SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null) is { Category: SqlTypeCategory.Float or SqlTypeCategory.Real };
    }
}
