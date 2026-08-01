using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Lineage;

/// <summary>Resolves a FROM clause to an alias-&gt;relation scope, flattening the join tree to its leaf table references.</summary>
public static class FromScopeResolver
{
    /// <summary>
    /// Bundles the context threaded through every table-reference resolution in this class -
    /// introduced to keep individual method signatures within a sane parameter count once
    /// Phase 4.1's UPDATE/DELETE/MERGE entry points added their own table references alongside
    /// the shared catalog/views/CTE/scope context every one of them still needs.
    /// <paramref name="ProcScope"/> is the qualified name of the innermost enclosing
    /// procedure/function/trigger, if any (docs/audit-remediation-plan.md Phase 2.5) - temp
    /// tables and table variables declared inside one are cataloged under a key scoped to it, so
    /// resolving a bare "#t"/"@t" reference needs the same scope to find them; a real persistent
    /// table was never stored with a scope, so passing one here is always safe (DatabaseCatalog
    /// falls back to the unscoped lookup automatically).
    /// </summary>
    internal readonly record struct ResolutionContext(
        DatabaseCatalog Catalog,
        IReadOnlyDictionary<string, ResolvedRelation> ResolvedViews,
        string SourcePath,
        SkipLedger? Ledger,
        IReadOnlyDictionary<string, ResolvedRelation>? CteRelations,
        string? ProcScope);

    public static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) Resolve(
        FromClause? fromClause,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger = null,
        IReadOnlyDictionary<string, ResolvedRelation>? cteRelations = null,
        string? procScope = null)
    {
        var context = new ResolutionContext(catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
        var byAlias = new Dictionary<string, ScopeEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ScopeEntry>();

        if (fromClause is null)
        {
            return (byAlias, ordered);
        }

        foreach (var tableReference in fromClause.TableReferences)
        {
            AddResolved(tableReference, context, aliasOverride: null, byAlias, ordered);
        }

        return (byAlias, ordered);
    }

    /// <summary>
    /// Resolves the FROM scope for an UPDATE/DELETE statement (docs/audit-remediation-plan.md
    /// Phase 4.1): if the statement has its own extended FROM clause (UPDATE t SET ... FROM
    /// Table t JOIN Other o ON ... WHERE ...), that alone defines the scope - it already
    /// includes the target, by T-SQL convention. With no FROM clause (the common simple case,
    /// UPDATE dbo.T SET Col = 1 WHERE Id = 5), the target itself is the whole (single-table)
    /// scope.
    /// </summary>
    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) ResolveForDataModification(
        TableReference target, FromClause? extraFromClause, ResolutionContext context)
    {
        if (extraFromClause is not null)
        {
            return Resolve(extraFromClause, context.Catalog, context.ResolvedViews, context.SourcePath, context.Ledger, context.CteRelations, context.ProcScope);
        }

        var byAlias = new Dictionary<string, ScopeEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ScopeEntry>();
        AddResolved(target, context, aliasOverride: null, byAlias, ordered);
        return (byAlias, ordered);
    }

    /// <summary>
    /// Resolves the FROM scope for a MERGE statement's ON clause and WHEN [NOT] MATCHED actions
    /// (docs/audit-remediation-plan.md Phase 4.1). MergeSpecification is a naming trap verified
    /// against the real parser output, not assumed: the inherited <c>Target</c> property is the
    /// INTO target (its alias lives separately in <paramref name="targetAlias"/>, the
    /// TableAlias property - NamedTableReference.Alias is null there), while the MERGE type's
    /// own <c>TableReference</c> property is actually the USING source (which does carry its
    /// own alias normally).
    /// </summary>
    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) ResolveForMerge(
        TableReference target, Identifier? targetAlias, TableReference source, ResolutionContext context)
    {
        var byAlias = new Dictionary<string, ScopeEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ScopeEntry>();

        AddResolved(target, context, targetAlias?.Value, byAlias, ordered);
        AddResolved(source, context, aliasOverride: null, byAlias, ordered);

        return (byAlias, ordered);
    }

    private static void AddResolved(
        TableReference tableReference, ResolutionContext context, string? aliasOverride, Dictionary<string, ScopeEntry> byAlias, List<ScopeEntry> ordered)
    {
        foreach (var leaf in FlattenJoins(tableReference))
        {
            var (alias, entry) = ResolveTableReference(leaf, context, aliasOverride);
            if (alias is not null)
            {
                byAlias[alias] = entry;
            }

            ordered.Add(entry);
        }
    }

    private static IEnumerable<TableReference> FlattenJoins(TableReference tableReference)
    {
        switch (tableReference)
        {
            case JoinTableReference join:
                foreach (var t in FlattenJoins(join.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenJoins(join.SecondTableReference))
                {
                    yield return t;
                }

                break;

            case JoinParenthesisTableReference parenthesis:
                foreach (var t in FlattenJoins(parenthesis.Join))
                {
                    yield return t;
                }

                break;

            default:
                yield return tableReference;
                break;
        }
    }

    private static (string? Alias, ScopeEntry Entry) ResolveTableReference(TableReference tableReference, ResolutionContext context, string? aliasOverride)
    {
        var (catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope) = context;

        switch (tableReference)
        {
            case NamedTableReference named:
                // CTE names shadow catalog tables/views of the same name (docs/audit-
                // remediation-plan.md Phase 2.4) - a CTE is never schema-qualified, so a
                // schema-qualified reference can never mean one regardless of a name collision.
                if (named.SchemaObject.SchemaIdentifier is null
                    && cteRelations is not null
                    && cteRelations.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cteRelation))
                {
                    var cteAlias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
                    return (cteAlias, new ScopeEntry(cteRelation, IsViewLayer: false));
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(named.SchemaObject);
                var isViewLayer = resolvedViews.TryGetValue(qualifiedName, out var view);
                var catalogTable = catalog.Find(qualifiedName, procScope);
                if (!isViewLayer && catalogTable is null)
                {
                    ledger?.Record(
                        AnalysisPass.Lineage, sourcePath, named.StartLine, named.StartColumn,
                        "FROM table reference", $"'{qualifiedName}' has no known DDL and is not a resolved view/TVF");
                }

                var relation = isViewLayer ? view! : ToResolvedRelation(catalogTable, qualifiedName);
                var alias = named.Alias?.Value ?? aliasOverride ?? SchemaObjectNameHelper.Resolve(named.SchemaObject).Name;
                return (alias, new ScopeEntry(relation, isViewLayer));

            case QueryDerivedTable derived:
                // A derived-table subquery is inline, local to this statement - not a
                // persisted view/TVF, so it does not add view-layer depth. The enclosing
                // statement's CTEs stay visible inside it.
                var innerColumns = QueryExpressionResolver.Resolve(derived.QueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
                if (derived.Columns.Count > 0)
                {
                    innerColumns = [.. innerColumns.Zip(derived.Columns, (c, id) => c with { Name = id.Value })];
                }

                return (derived.Alias?.Value, new ScopeEntry(new ResolvedRelation(QualifiedName: null, innerColumns), IsViewLayer: false));

            case SchemaObjectFunctionTableReference tvf:
                // A table-valued function invoked in a FROM clause (docs/audit-remediation-
                // plan.md Phase 4.2, audit finding B2). LineageResolver already resolves both
                // inline and multi-statement TVFs into the same resolvedViews dictionary a
                // regular view lands in (keyed by qualified name), so this is the same lookup
                // as the NamedTableReference view-layer case above, not a separate mechanism -
                // an inline TVF's own SELECT is exactly a view for lineage purposes, and a
                // multi-statement TVF's declared RETURNS shape is exactly Declared provenance.
                var tvfQualifiedName = SchemaObjectNameHelper.Qualify(tvf.SchemaObject);
                if (!resolvedViews.TryGetValue(tvfQualifiedName, out var tvfRelation))
                {
                    ledger?.Record(
                        AnalysisPass.Lineage, sourcePath, tvf.StartLine, tvf.StartColumn,
                        "FROM table-valued function", $"'{tvfQualifiedName}' is not a resolved inline/multi-statement TVF");
                    tvfRelation = ResolvedRelation.Empty;
                }

                var tvfAlias = tvf.Alias?.Value ?? SchemaObjectNameHelper.Resolve(tvf.SchemaObject).Name;
                return (tvfAlias, new ScopeEntry(tvfRelation, IsViewLayer: true));

            default:
                // OPENQUERY/OPENROWSET/PIVOT/table-valued function calls etc: not yet resolved.
                // Empty columns means any reference against this alias falls through to "not found".
                ledger?.Record(
                    AnalysisPass.Lineage, sourcePath, tableReference.StartLine, tableReference.StartColumn,
                    "FROM table reference", $"unsupported table reference kind '{tableReference.GetType().Name}' (OPENQUERY/OPENROWSET/PIVOT/table-valued function/etc.)");
                return ((tableReference as TableReferenceWithAlias)?.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }
    }

    private static ResolvedRelation ToResolvedRelation(CatalogTable? table, string qualifiedName)
    {
        if (table is null)
        {
            // Referenced a table/view we have no DDL for - CLAUDE.md precision discipline:
            // never guess. Column lookups against this relation resolve Unknown. The caller
            // already recorded this in the skip ledger.
            return new ResolvedRelation(qualifiedName, []);
        }

        return new ResolvedRelation(qualifiedName, [.. table.Columns.Select(c => new ResolvedColumn(
            c.Name,
            c.Type is { } type
                ? new ColumnProvenance.BaseColumn(qualifiedName, c.Name, type)
                : new ColumnProvenance.Unknown($"column {c.Name} has an unresolved declared type")))]);
    }
}
