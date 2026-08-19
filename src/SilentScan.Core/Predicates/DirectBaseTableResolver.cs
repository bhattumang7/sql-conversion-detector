using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Shared "resolve a table reference to a direct base <see cref="CatalogTable"/>, keyed by its
/// alias (or bare name when unaliased)" logic used by several standalone scanners that
/// deliberately work off the raw join tree rather than the full <see
/// cref="Lineage.FromScopeResolver"/> scope-chain/lineage machinery (a known v1 scope limit shared
/// by every caller: a reference reached through a view/derived table is left unanalyzed, not
/// guessed at). Was byte-identical across <c>FloatEqualityPredicateScanner</c> and
/// <c>AggregateDivisionColumnstoreScanner</c>, whose own doc comments already named each other as
/// precedent without anyone extracting it; <c>NonUniqueUpdateSourceScanner</c> resolves one
/// reference at a time with the same rule.
///
/// CTE shadowing is NOT covered by that "unanalyzed" framing, though, and every caller must pass
/// a real <c>cteNames</c> set (typically <see cref="Lineage.CteNameCollector.Collect"/> over the
/// enclosing statement) rather than an empty set: a CTE is never schema-qualified, so it always
/// shadows a same-named real base table for its statement's own lifetime, and a raw
/// <c>SchemaObjectNameHelper.Qualify</c> + <c>catalog.Find</c> lookup has no way to see that on
/// its own - it silently matched a CTE-shadowed reference against an unrelated real table sharing
/// its name (2026-08 audit), which is the opposite of "unanalyzed", not a variant of it.
/// </summary>
internal static class DirectBaseTableResolver
{
    public static (string Alias, CatalogTable Table)? ResolveDirectBaseTable(DatabaseCatalog catalog, TableReference tableReference, IReadOnlySet<string> cteNames)
    {
        if (tableReference is not NamedTableReference named)
        {
            return null;
        }

        if (named.SchemaObject.SchemaIdentifier is null && cteNames.Contains(named.SchemaObject.BaseIdentifier.Value))
        {
            return null;
        }

        var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
        return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } table ? (alias, table) : null;
    }

    /// <summary>
    /// Flattens every reference to its direct <see cref="NamedTableReference"/> leaves - only a
    /// leaf that resolves to a real base <see cref="CatalogTableKind.Table"/> is kept; a view,
    /// TVF, derived table, CTE, or unresolved reference is silently excluded. <paramref
    /// name="extraTarget"/> covers an UPDATE/DELETE with no explicit FROM clause at all, where the
    /// statement's own target table is the only thing in scope.
    /// </summary>
    public static Dictionary<string, CatalogTable> ResolveDirectBaseTables(
        DatabaseCatalog catalog, IList<TableReference>? tableReferences, IReadOnlySet<string> cteNames, TableReference? extraTarget = null)
    {
        var tables = new Dictionary<string, CatalogTable>(StringComparer.OrdinalIgnoreCase);

        if (extraTarget is not null && ResolveDirectBaseTable(catalog, extraTarget, cteNames) is { } targetEntry)
        {
            tables[targetEntry.Alias] = targetEntry.Table;
        }

        if (tableReferences is null)
        {
            return tables;
        }

        foreach (var reference in tableReferences)
        {
            foreach (var leaf in PredicateTreeWalker.FlattenTableReferences(reference))
            {
                if (ResolveDirectBaseTable(catalog, leaf, cteNames) is { } entry)
                {
                    tables[entry.Alias] = entry.Table;
                }
            }
        }

        return tables;
    }

    /// <summary>Same rule as <see cref="ResolveDirectBaseTable"/>, but as a nullable-string pair for callers matching a join side by alias rather than collecting a whole scope's tables.</summary>
    public static (string? Alias, string? QualifiedName) ResolveDirectBaseTableName(DatabaseCatalog catalog, TableReference tableReference, IReadOnlySet<string> cteNames)
    {
        var resolved = ResolveDirectBaseTable(catalog, tableReference, cteNames);
        return resolved is { } entry ? (entry.Alias, entry.Table.QualifiedName) : (null, null);
    }

    /// <summary>The last-identifier-matches-alias rule shared by every scanner that resolves a column reference back to a join alias without going through the full scope-chain machinery.</summary>
    public static string? ColumnNameIfQualifiedByAlias(ScalarExpression expression, string alias)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        return identifiers.Count >= 2 && string.Equals(identifiers[^2].Value, alias, StringComparison.OrdinalIgnoreCase)
            ? identifiers[^1].Value
            : null;
    }

    /// <summary>
    /// Resolves a column reference against a scope built by <see cref="ResolveDirectBaseTables"/>.
    /// An alias-qualified reference resolves only through that alias; an unqualified one resolves
    /// only when exactly one table is in scope, so an ambiguous bare column name is left
    /// unresolved rather than attributed to a guessed table. Was byte-identical (comment included)
    /// in <c>FloatEqualityPredicateScanner</c> and <c>StringConcatNullScanner</c>, the two scanners
    /// that pair it with the dictionary this class already builds for them.
    /// </summary>
    public static (CatalogTable Table, CatalogColumn Column)? TryResolveColumn(
        ColumnReferenceExpression columnRef, Dictionary<string, CatalogTable> tables)
    {
        var identifiers = columnRef.MultiPartIdentifier?.Identifiers;
        if (identifiers is null || identifiers.Count == 0)
        {
            return null;
        }

        var columnName = identifiers[^1].Value;

        if (identifiers.Count >= 2)
        {
            var alias = identifiers[^2].Value;
            if (tables.TryGetValue(alias, out var table) && table.FindColumn(columnName) is { } column)
            {
                return (table, column);
            }

            return null;
        }

        // Unqualified reference - only safe to resolve when exactly one table is in scope, to
        // avoid guessing which of several tables an ambiguous bare column name belongs to.
        if (tables.Count == 1)
        {
            var single = tables.Values.Single();
            if (single.FindColumn(columnName) is { } singleColumn)
            {
                return (single, singleColumn);
            }
        }

        return null;
    }

    /// <summary>Collects every <see cref="ColumnReferenceExpression"/> reachable from a fragment, unresolved - used only to then test each one against <see cref="ColumnNameIfQualifiedByAlias"/>.</summary>
    public sealed class RawColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> References { get; } = [];

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            References.Add(node);
            base.ExplicitVisit(node);
        }
    }
}
