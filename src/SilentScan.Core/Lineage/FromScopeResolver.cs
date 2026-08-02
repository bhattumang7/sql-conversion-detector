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
                if (byAlias.ContainsKey(alias))
                {
                    // A legal, if unusual, query: `FROM dbo.T JOIN audit.T ON ...` exposes two
                    // leaves under the same unqualified name (T). Silently last-wins here would
                    // make every later `T.Col` reference - including star expansion - resolve
                    // against whichever leaf happened to be flattened last, a wrong-base-column
                    // risk identical in kind to the column-list Zip misalignment above. Neither
                    // leaf's identity can be trusted once this happens, so poison the entry
                    // rather than guess which one a bare reference meant.
                    context.Ledger?.Record(
                        AnalysisPass.Lineage, context.SourcePath, tableReference.StartLine, tableReference.StartColumn,
                        "FROM alias", $"'{alias}' is exposed by more than one table reference in this FROM clause - ambiguous, references to it resolve Unknown");
                    byAlias[alias] = new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false);
                }
                else
                {
                    byAlias[alias] = entry;
                }
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

                // A well-known built-in system catalog view (sys.objects, sysobjects, ...) -
                // never appears in a repo's own DDL (there's nothing to CREATE), so it always
                // fails the ordinary catalogTable/isViewLayer lookups above; without this it was
                // reported as "no known DDL" alongside a genuine unresolvable reference, when it
                // is in fact a fully known, if external, shape (an audit finding: this was the
                // single dominant cause of unresolved FROM-table skips across the corpus, since
                // DBA/admin scripts - a large share of the pinned corpus - query these constantly).
                var systemCatalogColumns = !isViewLayer && catalogTable is null
                    ? SystemCatalogViewRegistry.TryResolve(qualifiedName)
                    : null;

                if (!isViewLayer && catalogTable is null && systemCatalogColumns is null)
                {
                    ledger?.Record(
                        AnalysisPass.Lineage, sourcePath, named.StartLine, named.StartColumn,
                        "FROM table reference", $"'{qualifiedName}' has no known DDL and is not a resolved view/TVF");
                }

                var relation = isViewLayer
                    ? view!
                    : systemCatalogColumns is not null
                        ? ToSystemCatalogRelation(systemCatalogColumns, qualifiedName)
                        : ToResolvedRelation(catalogTable, qualifiedName);
                var alias = named.Alias?.Value ?? aliasOverride ?? SchemaObjectNameHelper.Resolve(named.SchemaObject).Name;
                return (alias, new ScopeEntry(relation, isViewLayer));

            case QueryDerivedTable derived:
                // A derived-table subquery is inline, local to this statement - not a
                // persisted view/TVF, so it does not add view-layer depth. The enclosing
                // statement's CTEs stay visible inside it.
                var innerColumns = QueryExpressionResolver.Resolve(derived.QueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
                if (derived.Columns.Count > 0)
                {
                    if (innerColumns.Count == derived.Columns.Count)
                    {
                        innerColumns = [.. innerColumns.Zip(derived.Columns, (c, id) => c with { Name = id.Value })];
                    }
                    else
                    {
                        // Same wrong-base-column risk as the view/CTE column-list cases.
                        ledger?.Record(
                            AnalysisPass.Lineage, sourcePath, derived.StartLine, derived.StartColumn, "derived table column list",
                            $"declares {derived.Columns.Count} column name(s) but its query resolved {innerColumns.Count} - column identity can't be trusted");
                        innerColumns = [.. innerColumns.Select((c, i) => new ResolvedColumn(
                            i < derived.Columns.Count ? derived.Columns[i].Value : c.Name,
                            new ColumnProvenance.Unknown("derived table's declared column count does not match its resolved query")))];
                    }
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

            case VariableTableReference variableTable:
                // FROM @t - a table variable, declared either by an ordinary DECLARE @t TABLE(...)
                // in the enclosing body or by a multi-statement TVF's own RETURNS @t TABLE(...)
                // (coverage-remediation-plan.md Phase 3.4) - both are cataloged under the same
                // scope-keyed lookup a temp table uses, so this is the identical NamedTableReference
                // resolution path, keyed by the variable's own name instead of a schema object name.
                // A real inline/PK/UNIQUE index on the table variable is a genuine index, unlike a
                // trigger pseudo-table, so the ordinary ToResolvedRelation (BaseColumn) applies.
                var variableName = variableTable.Variable.Name;
                var variableTableCatalog = catalog.Find(variableName, procScope);
                if (variableTableCatalog is null)
                {
                    ledger?.Record(
                        AnalysisPass.Lineage, sourcePath, variableTable.StartLine, variableTable.StartColumn,
                        "FROM table reference", $"table variable '{variableName}' has no known DECLARE/RETURNS in scope");
                }

                var variableTableAlias = variableTable.Alias?.Value ?? variableName;
                return (variableTableAlias, new ScopeEntry(ToResolvedRelation(variableTableCatalog, variableName), IsViewLayer: false));

            default:
                // OPENQUERY/OPENROWSET/PIVOT/table-valued function calls etc: not yet resolved.
                // Empty columns means any reference against this alias falls through to "not found".
                ledger?.Record(
                    AnalysisPass.Lineage, sourcePath, tableReference.StartLine, tableReference.StartColumn,
                    "FROM table reference", $"unsupported table reference kind '{tableReference.GetType().Name}' (OPENQUERY/OPENROWSET/PIVOT/table-valued function/etc.)");
                return ((tableReference as TableReferenceWithAlias)?.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }
    }

    /// <summary>Real catalog table -&gt; <see cref="ResolvedRelation"/>, used for an ordinary FROM-clause table reference. Columns carry <see cref="ColumnProvenance.BaseColumn"/>, so a predicate against one can report the table's real index.</summary>
    internal static ResolvedRelation ToResolvedRelation(CatalogTable? table, string qualifiedName)
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

    /// <summary>
    /// A well-known system catalog view's fixed column shape (<see
    /// cref="SystemCatalogViewRegistry"/>) - carries <see cref="ColumnProvenance.BaseColumn"/>
    /// like an ordinary table, since a real predicate against it types and classifies exactly
    /// the same way. <see cref="Catalog.DatabaseCatalog"/>'s own lookup never has an entry for it (there
    /// is no CREATE DDL for a built-in system view), so <c>TypedPredicateExtractor</c>'s own
    /// index lookup for a BaseColumn's table naturally resolves Indexed=false - the honest "no
    /// evidence of an index" default this codebase already uses everywhere else, not a claim
    /// about whatever real index SQL Server's internal storage for this view may or may not have.
    /// </summary>
    private static ResolvedRelation ToSystemCatalogRelation(IReadOnlyList<(string Name, SqlType Type)> columns, string qualifiedName) =>
        new(qualifiedName, [.. columns.Select(c => new ResolvedColumn(c.Name, new ColumnProvenance.BaseColumn(qualifiedName, c.Name, c.Type)))]);

    /// <summary>
    /// Exposed for <see cref="Predicates.TypedPredicateExtractor"/>: a trigger's inserted/deleted
    /// pseudo-tables are shaped exactly like the trigger's own target table - same column names
    /// and types - but they are a rowset materialised from the version store, not a real catalog
    /// object, so a predicate against inserted.Col MUST NOT report an index the pseudo-table does
    /// not have (coverage-remediation-plan.md Phase 1.1 - a ScanForced+indexed finding here would
    /// wrongly rank first under CLAUDE.md's ranking rule while not being an index-killing
    /// conversion at all: the conversion is real, the seek loss claim is not). Columns therefore
    /// carry <see cref="ColumnProvenance.Declared"/> rather than <see cref="ColumnProvenance.BaseColumn"/>
    /// - the same "known type, not traceable to a real index" provenance a multi-statement TVF's
    /// declared RETURNS TABLE(...) column already uses, reusing that established pattern rather
    /// than inventing a parallel one. <paramref name="qualifiedName"/> is still the real target
    /// table's name, kept so a finding against inserted/deleted stays attributable to where the
    /// data actually lives - only the index claim changes, not the reported location.
    /// </summary>
    internal static ResolvedRelation ToPseudoTableRelation(CatalogTable? table, string qualifiedName)
    {
        if (table is null)
        {
            return new ResolvedRelation(qualifiedName, []);
        }

        return new ResolvedRelation(qualifiedName, [.. table.Columns.Select(c => new ResolvedColumn(
            c.Name,
            c.Type is { } type
                ? new ColumnProvenance.Declared(type, qualifiedName)
                : new ColumnProvenance.Unknown($"column {c.Name} has an unresolved declared type")))]);
    }

    /// <summary>
    /// Overload for an INSTEAD OF trigger whose target is a VIEW rather than a table (coverage-
    /// remediation-plan.md Phase 3.3) - <paramref name="viewRelation"/> is the view's own already-
    /// resolved <see cref="ResolvedRelation"/> (from <c>LineageCatalog.AllRelations</c>), which can
    /// carry <see cref="ColumnProvenance.BaseColumn"/> for any column that passes a base table's
    /// column straight through the view. That's correct for an ordinary SELECT against the view
    /// (SQL Server really can seek through a simple view), but inserted/deleted on an INSTEAD OF
    /// trigger is not a query against the view's rows - same as the table case, it has no index of
    /// its own, so any BaseColumn provenance is downgraded to Declared here (dropping the index
    /// claim, keeping the type) before being reused. Cast/Expression/Union/Unknown/Declared
    /// columns are untouched - only BaseColumn's top-level index lookup in
    /// <c>TypedPredicateExtractor.ResolveColumnOperand</c> is the thing this must neutralise.
    /// </summary>
    internal static ResolvedRelation ToPseudoTableRelation(ResolvedRelation viewRelation, string qualifiedName) =>
        new(qualifiedName, [.. viewRelation.Columns.Select(c => c.Provenance switch
        {
            ColumnProvenance.BaseColumn { Type: { } type } => c with { Provenance = new ColumnProvenance.Declared(type, qualifiedName) },
            ColumnProvenance.BaseColumn => c with { Provenance = new ColumnProvenance.Unknown("pseudo-table column type could not be resolved") },
            _ => c,
        })]);
}
