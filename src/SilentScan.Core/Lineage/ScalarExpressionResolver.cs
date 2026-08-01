using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Lineage;

/// <summary>Resolves a single SELECT-list scalar expression to its <see cref="ColumnProvenance"/>.</summary>
public static class ScalarExpressionResolver
{
    public static ColumnProvenance Resolve(
        ScalarExpression expression, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, SkipLedger? ledger = null) =>
        expression switch
        {
            ColumnReferenceExpression columnRef => ResolveColumnReference(columnRef, scope, orderedRelations, sourcePath, ledger),
            CastCall castCall => ResolveCastOrConvert(castCall.DataType, castCall.Parameter, scope, orderedRelations, sourcePath, castCall.StartLine, ledger),
            ConvertCall convertCall => ResolveCastOrConvert(convertCall.DataType, convertCall.Parameter, scope, orderedRelations, sourcePath, convertCall.StartLine, ledger),
            Literal literal => new ColumnProvenance.Expression(LiteralTypeResolver.Resolve(literal), Inputs: []),
            _ => ResolveGenericExpression(expression, scope, orderedRelations, sourcePath, ledger),
        };

    private static ColumnProvenance ResolveCastOrConvert(
        DataTypeReference dataType, ScalarExpression parameter, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, int line, SkipLedger? ledger)
    {
        var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
        if (resolved is not { } type)
        {
            ledger?.Record(AnalysisPass.Lineage, sourcePath, line, dataType.StartColumn, "CAST/CONVERT", "target type could not be resolved");
            return new ColumnProvenance.Unknown("CAST/CONVERT target type could not be resolved");
        }

        var inner = Resolve(parameter, scope, orderedRelations, sourcePath, ledger);
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
        ScalarExpression expression, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, SkipLedger? ledger)
    {
        var collector = new ColumnReferenceCollector();
        expression.Accept(collector);
        var inputs = collector.References.Select(columnRef => ResolveColumnReference(columnRef, scope, orderedRelations, sourcePath, ledger)).ToList();
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

    /// <summary>
    /// Resolves a column reference against a single FROM scope: the one algorithm both Pass 2
    /// (this class) and Pass 3 (<see cref="Predicates.TypedPredicateExtractor"/>) use, so a
    /// qualified reference whose qualifier doesn't resolve is unresolved everywhere, never
    /// silently falling back to a name-only search across the whole scope
    /// (docs/audit-remediation-plan.md Phase 2.1 - that fallback could bind a correlated
    /// outer-query reference like "o.Id" to an unrelated same-named column on a completely
    /// different table). Equivalent to the chain overload with a single-level chain.
    /// </summary>
    internal static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, SkipLedger? ledger) =>
        ResolveColumnReference(columnRef, [(scope, orderedRelations)], sourcePath, ledger);

    /// <summary>
    /// Resolves a column reference against a chain of nested FROM scopes, innermost first
    /// (docs/audit-remediation-plan.md Phase 2.2): a qualifier or unqualified column name is
    /// looked up in the innermost scope first, then progressively outer scopes, matching SQL's
    /// own correlated-subquery name resolution rule. An ambiguous match WITHIN one scope level
    /// stops the search there rather than skipping past it to an outer level - that ambiguity is
    /// real, not a reason to guess the query meant an enclosing query's column instead.
    /// </summary>
    internal static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        string sourcePath,
        SkipLedger? ledger)
    {
        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        var columnName = identifiers[^1].Value;

        ColumnProvenance.Unknown Unresolved(string reason)
        {
            ledger?.Record(AnalysisPass.Lineage, sourcePath, columnRef.StartLine, columnRef.StartColumn, "column reference", reason);
            return new ColumnProvenance.Unknown(reason);
        }

        if (identifiers.Count >= 2)
        {
            var qualifier = identifiers[^2].Value;
            foreach (var (byAlias, _) in scopeChain)
            {
                if (!byAlias.TryGetValue(qualifier, out var entry))
                {
                    continue;
                }

                var column = entry.Relation.FindColumn(columnName);
                return column is null
                    ? Unresolved($"column '{columnName}' not found on '{qualifier}'")
                    : BumpDepthIfViewLayer(column.Provenance, entry.IsViewLayer);
            }

            return Unresolved($"unknown table alias '{qualifier}'");
        }

        foreach (var (_, ordered) in scopeChain)
        {
            var matches = ordered
                .Select(entry => (Entry: entry, Column: entry.Relation.FindColumn(columnName)))
                .Where(m => m.Column is not null)
                .ToList();

            if (matches.Count == 1)
            {
                return BumpDepthIfViewLayer(matches[0].Column!.Provenance, matches[0].Entry.IsViewLayer);
            }

            if (matches.Count > 1)
            {
                return Unresolved($"column '{columnName}' is ambiguous across the FROM scope");
            }
        }

        return Unresolved($"column '{columnName}' not found in FROM scope");
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
