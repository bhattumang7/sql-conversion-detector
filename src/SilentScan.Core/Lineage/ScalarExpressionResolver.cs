using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Lineage;

/// <summary>Resolves a single SELECT-list scalar expression to its <see cref="ColumnProvenance"/>.</summary>
public static class ScalarExpressionResolver
{
    public static ColumnProvenance Resolve(
        ScalarExpression expression, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath) =>
        expression switch
        {
            ColumnReferenceExpression columnRef => ResolveColumnReference(columnRef, scope, orderedRelations),
            CastCall castCall => ResolveCastOrConvert(castCall.DataType, castCall.Parameter, scope, orderedRelations, sourcePath, castCall.StartLine),
            ConvertCall convertCall => ResolveCastOrConvert(convertCall.DataType, convertCall.Parameter, scope, orderedRelations, sourcePath, convertCall.StartLine),
            Literal literal => new ColumnProvenance.Expression(LiteralTypeResolver.Resolve(literal), Inputs: []),
            _ => ResolveGenericExpression(expression, scope, orderedRelations, sourcePath),
        };

    private static ColumnProvenance ResolveCastOrConvert(
        DataTypeReference dataType, ScalarExpression parameter, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, int line)
    {
        var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
        if (resolved is not { } type)
        {
            return new ColumnProvenance.Unknown("CAST/CONVERT target type could not be resolved");
        }

        var inner = Resolve(parameter, scope, orderedRelations, sourcePath);
        return new ColumnProvenance.Cast(type, inner, sourcePath, line);
    }

    /// <summary>
    /// Any scalar expression ScriptDOM hands us that isn't a plain column reference, CAST/
    /// CONVERT, or literal (a function call, arithmetic, CASE, ...). Rather than exhaustively
    /// modeling every ScriptDOM expression shape, this collects every column reference
    /// reachable anywhere inside the expression tree and resolves each one's own provenance -
    /// enough to tell whether a real, possibly-indexed base column sits underneath, without
    /// needing to mirror the expression's exact structure.
    /// </summary>
    private static ColumnProvenance.Expression ResolveGenericExpression(
        ScalarExpression expression, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath)
    {
        var collector = new ColumnReferenceCollector();
        expression.Accept(collector);
        var inputs = collector.References.Select(columnRef => ResolveColumnReference(columnRef, scope, orderedRelations)).ToList();
        return new ColumnProvenance.Expression(InferredType: null, inputs, sourcePath, expression.StartLine);
    }

    private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> References { get; } = [];

        public override void Visit(ColumnReferenceExpression node)
        {
            // A wildcard reference (the `*` inside e.g. COUNT(*)) has no MultiPartIdentifier -
            // it isn't "a column" for lineage purposes, and it isn't sargability-relevant
            // either. Same class of bug as NonSargablePredicateScanner's earlier COUNT(*) fix.
            if (node.MultiPartIdentifier is { Identifiers.Count: > 0 })
            {
                References.Add(node);
            }
        }
    }

    private static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations)
    {
        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        var columnName = identifiers[^1].Value;

        if (identifiers.Count >= 2)
        {
            var qualifier = identifiers[^2].Value;
            if (!scope.TryGetValue(qualifier, out var entry))
            {
                return new ColumnProvenance.Unknown($"unknown table alias '{qualifier}'");
            }

            var column = entry.Relation.FindColumn(columnName);
            return column is null
                ? new ColumnProvenance.Unknown($"column '{columnName}' not found on '{qualifier}'")
                : BumpDepthIfViewLayer(column.Provenance, entry.IsViewLayer);
        }

        var matches = orderedRelations
            .Select(entry => (Entry: entry, Column: entry.Relation.FindColumn(columnName)))
            .Where(m => m.Column is not null)
            .ToList();

        return matches.Count switch
        {
            0 => new ColumnProvenance.Unknown($"column '{columnName}' not found in FROM scope"),
            > 1 => new ColumnProvenance.Unknown($"column '{columnName}' is ambiguous across the FROM scope"),
            _ => BumpDepthIfViewLayer(matches[0].Column!.Provenance, matches[0].Entry.IsViewLayer),
        };
    }

    internal static ColumnProvenance BumpDepthIfViewLayer(ColumnProvenance provenance, bool isViewLayer)
    {
        if (!isViewLayer)
        {
            return provenance;
        }

        return provenance switch
        {
            ColumnProvenance.BaseColumn bc => bc with { Depth = bc.Depth + 1 },
            ColumnProvenance.Cast cast => cast with { Depth = cast.Depth + 1, Inner = BumpDepthIfViewLayer(cast.Inner, isViewLayer) },
            ColumnProvenance.Expression expr => expr with { Depth = expr.Depth + 1, Inputs = [.. expr.Inputs.Select(i => BumpDepthIfViewLayer(i, isViewLayer))] },
            _ => provenance,
        };
    }
}
