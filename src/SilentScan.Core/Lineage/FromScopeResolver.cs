using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Lineage;

/// <summary>Resolves a FROM clause to an alias-&gt;relation scope, flattening the join tree to its leaf table references.</summary>
public static class FromScopeResolver
{
    /// <summary>Skip-ledger construct kind shared by every "this FROM-clause table reference didn't resolve" entry in this class - one label for the whole family (missing DDL, unresolved table variable, unmodeled PIVOT/UNPIVOT shape, genuinely unsupported reference kind).</summary>
    private const string FromTableReferenceConstructKind = "FROM table reference";

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
    /// falls back to the unscoped lookup automatically). <paramref name="CallerScopeByCalleeScope"/>
    /// maps a procedure scope to every OTHER scope known to call it - a #temp table is
    /// session-scoped in real SQL Server (visible to a callee EXEC'd from the proc that created
    /// it), unlike a table variable (always proc-local, never propagated). A specific name is
    /// only ever resolved through more than one caller when every caller that has an entry for
    /// it agrees on its exact shape (<see cref="CatalogTable.HasSameShapeAs"/>) - see
    /// <see cref="ResolveNamedTableReference"/>. Built once, corpus-wide, from
    /// <see cref="Predicates.ProcCallGraph"/> - kept as a plain name-to-names map here rather than
    /// threading the graph type itself into Lineage, to avoid this layer depending on Predicates.
    /// </summary>
    internal readonly record struct ResolutionContext(
        DatabaseCatalog Catalog,
        IReadOnlyDictionary<string, ResolvedRelation> ResolvedViews,
        string SourcePath,
        SkipLedger? Ledger,
        IReadOnlyDictionary<string, ResolvedRelation>? CteRelations,
        string? ProcScope,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? CallerScopeByCalleeScope = null);

    public static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) Resolve(
        FromClause? fromClause,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger = null,
        IReadOnlyDictionary<string, ResolvedRelation>? cteRelations = null,
        string? procScope = null) =>
        Resolve(fromClause, new ResolutionContext(catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope));

    /// <summary>
    /// Same as the flat-parameter overload above, taking an already-built <see
    /// cref="ResolutionContext"/> directly - the only way to also supply <see
    /// cref="ResolutionContext.CallerScopeByCalleeScope"/>, since the flat overload already sits
    /// at the S107 parameter-count ceiling and that field is needed by only one caller
    /// (<c>TypedPredicateExtractor</c>'s own top-level FROM-clause resolution).
    /// </summary>
    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) Resolve(FromClause? fromClause, ResolutionContext context)
    {
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
            return Resolve(extraFromClause, context);
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

    /// <summary>
    /// PIVOT/UNPIVOT's own source is typed as a plain <see cref="TableReference"/> in ScriptDOM's
    /// grammar, which - like any FROM-clause source - can itself be a join tree (<c>FROM A JOIN B
    /// ON ... PIVOT (...) p</c>). <see cref="ResolveTableReference"/>'s own switch has no
    /// <see cref="JoinTableReference"/> case at all (join trees are only ever handled by
    /// <see cref="FlattenJoins"/>/<see cref="AddResolved"/> at the top of ordinary FROM-clause
    /// resolution), so calling it directly on a join source fell to the unsupported-reference
    /// case and dropped the whole source's columns. This flattens the same way an ordinary FROM
    /// clause does and concatenates every leaf's own resolved columns, so a PIVOT/UNPIVOT whose
    /// source happens to be a join no longer loses every passthrough column from it.
    /// </summary>
    private static IReadOnlyList<ResolvedColumn> ResolveFlattenedSourceColumns(TableReference source, ResolutionContext context) =>
        [.. FlattenJoins(source).SelectMany(leaf => ResolveTableReference(leaf, context, aliasOverride: null).Entry.Relation.Columns)];

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

    private static (string? Alias, ScopeEntry Entry) ResolveTableReference(TableReference tableReference, ResolutionContext context, string? aliasOverride) => tableReference switch
    {
        NamedTableReference named => ResolveNamedTableReference(named, context, aliasOverride),
        QueryDerivedTable derived => ResolveDerivedTableReference(derived, context),
        SchemaObjectFunctionTableReference tvf => ResolveTvfTableReference(tvf, context),
        VariableTableReference variableTable => ResolveVariableTableReference(variableTable, context),
        PivotedTableReference pivot => ResolvePivotedTableReference(pivot, context),
        UnpivotedTableReference unpivot => ResolveUnpivotedTableReference(unpivot, context),
        _ => ResolveUnsupportedTableReference(tableReference, context),
    };

    /// <summary>
    /// SQL Server's four built-in system databases - real corpus DBA/admin scripts routinely
    /// query these (msdb.dbo.sysjobs, master.sys.databases, tempdb.sys.tables, ...), and we will
    /// never have DDL for any of them since there is nothing to CREATE. Corpus measurement
    /// (466 three-part references across the pinned corpus, all of them msdb/master/tempdb/
    /// model) proved multi-database catalog support would gain zero real findings - every
    /// occurrence was already one of these four, never a genuine external user database. A
    /// user-named external database (e.g. a cross-server logging DB some corpus repos
    /// reference by name) is NOT in this set and stays the ordinary "no known DDL" case, since
    /// it's a real, nameable gap rather than an intentional scope boundary.
    /// </summary>
    private static readonly HashSet<string> SystemDatabaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "msdb", "tempdb",
    };

    private static bool IsSystemDatabaseReference(SchemaObjectName schemaObject) =>
        schemaObject.DatabaseIdentifier is { Value.Length: > 0 } db && SystemDatabaseNames.Contains(db.Value);

    private static (string? Alias, ScopeEntry Entry) ResolveNamedTableReference(NamedTableReference named, ResolutionContext context, string? aliasOverride)
    {
        var (catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope, callerScopeByCalleeScope) = context;

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

        // SchemaObjectNameHelper.Qualify only reads Database/Schema/Base - it silently drops a
        // ServerIdentifier, so a four-part linked-server reference (LinkedSrv.OtherDb.dbo.T)
        // would otherwise collapse to the same catalog key as an unrelated LOCAL OtherDb.dbo.T,
        // silently inheriting that local table's columns/indexes. CatalogBuilder's own synonym
        // path already guards this identically; the FROM-clause path had no equivalent.
        if (named.SchemaObject.ServerIdentifier is { Value.Length: > 0 })
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, named.StartLine, named.StartColumn,
                FromTableReferenceConstructKind, $"'{SchemaObjectNameHelper.Qualify(named.SchemaObject)}': names a linked server - four-part cross-server table references are not modeled");
            return (named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }

        // Canonicalized through any synonym chain BEFORE either lookup below - a synonym for a
        // VIEW can only ever resolve via the resolvedViews dictionary (views are never in
        // DatabaseCatalog at all), and a finding/probe against a synonym'd table must name the
        // real base table, not the synonym, or SARIF/the Verify oracle's probe end up naming an
        // object the rest of the pipeline never actually resolved anything about.
        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
        var isViewLayer = resolvedViews.TryGetValue(qualifiedName, out var view);
        var catalogTable = catalog.Find(qualifiedName, procScope);

        // #temp tables are session-scoped in real SQL Server, not proc-scoped - a "driver" proc
        // that creates #Results and then EXECs several sub-procs against it is common, real
        // corpus code, not an edge case. Own-scope resolution above already covers the
        // overwhelmingly common same-proc case; this only fires when it found nothing there.
        if (catalogTable is null
            && procScope is not null
            && callerScopeByCalleeScope is not null
            && callerScopeByCalleeScope.TryGetValue(procScope, out var callerScopes))
        {
            catalogTable = TryResolveFromCallerScopes(catalog, qualifiedName, callerScopes);
        }

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
            var reason = IsSystemDatabaseReference(named.SchemaObject)
                ? $"'{qualifiedName}' references a SQL Server system database - intentionally out of scope (no DDL will ever exist to catalog it)"
                : $"'{qualifiedName}' has no known DDL and is not a resolved view/TVF";
            ledger?.Record(AnalysisPass.Lineage, sourcePath, named.StartLine, named.StartColumn, FromTableReferenceConstructKind, reason);
        }

        ResolvedRelation relation;
        if (isViewLayer)
        {
            relation = view!;
        }
        else if (systemCatalogColumns is not null)
        {
            relation = ToSystemCatalogRelation(systemCatalogColumns, qualifiedName);
        }
        else
        {
            relation = ToResolvedRelation(catalogTable, qualifiedName);
        }

        var alias = named.Alias?.Value ?? aliasOverride ?? SchemaObjectNameHelper.Resolve(named.SchemaObject).Name;
        return (alias, new ScopeEntry(relation, isViewLayer));
    }

    /// <summary>
    /// Tries every known caller scope's own entry for <paramref name="qualifiedName"/> (a #temp
    /// table is session-scoped in real SQL Server, so any of several sub-procedure callers could
    /// legitimately be the one that created it) and returns a result only when every caller that
    /// actually HAS an entry agrees on its exact shape (<see cref="CatalogTable.HasSameShapeAs"/>) -
    /// a caller with no entry for this name at all is simply skipped (it never created this
    /// particular #temp table, which says nothing about whether the ones that did agree). Two
    /// callers building genuinely DIFFERENT shapes under the same name is exactly the
    /// same-name-different-shape pattern <c>CatalogBuilderTests</c> already covers for the
    /// same-proc case - resolving to either one here would be a guess, so this returns null
    /// instead, same as if no caller had an entry at all. Shared by <see
    /// cref="ResolveNamedTableReference"/> and <see
    /// cref="Predicates.TypedPredicateExtractor"/>'s own write-target resolution, so a SELECT and
    /// an INSERT against the same cross-proc #temp table apply the identical rule.
    /// </summary>
    internal static CatalogTable? TryResolveFromCallerScopes(DatabaseCatalog catalog, string qualifiedName, IReadOnlyList<string> callerScopes)
    {
        CatalogTable? resolved = null;
        foreach (var callerScope in callerScopes)
        {
            var candidate = catalog.Find(qualifiedName, callerScope);
            if (candidate is null)
            {
                continue;
            }

            if (resolved is null)
            {
                resolved = candidate;
            }
            else if (!resolved.HasSameShapeAs(candidate))
            {
                return null;
            }
        }

        return resolved;
    }

    private static (string? Alias, ScopeEntry Entry) ResolveDerivedTableReference(QueryDerivedTable derived, ResolutionContext context)
    {
        var (catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope, _) = context;

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
    }

    private static (string? Alias, ScopeEntry Entry) ResolveTvfTableReference(SchemaObjectFunctionTableReference tvf, ResolutionContext context)
    {
        var (catalog, resolvedViews, sourcePath, ledger, _, _, _) = context;

        // Same ServerIdentifier guard as the ordinary NamedTableReference case above - see its
        // own comment for why silently dropping it risks colliding with an unrelated local name.
        if (tvf.SchemaObject.ServerIdentifier is { Value.Length: > 0 })
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, tvf.StartLine, tvf.StartColumn,
                "FROM table-valued function", $"'{SchemaObjectNameHelper.Qualify(tvf.SchemaObject)}': names a linked server - four-part cross-server table references are not modeled");
            return (tvf.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }

        // A table-valued function invoked in a FROM clause (docs/audit-remediation-
        // plan.md Phase 4.2, audit finding B2). LineageResolver already resolves both
        // inline and multi-statement TVFs into the same resolvedViews dictionary a
        // regular view lands in (keyed by qualified name), so this is the same lookup
        // as the NamedTableReference view-layer case above, not a separate mechanism -
        // an inline TVF's own SELECT is exactly a view for lineage purposes, and a
        // multi-statement TVF's declared RETURNS shape is exactly Declared provenance.
        // Canonicalized through any synonym chain first, same reasoning as the ordinary
        // table-reference case above.
        var tvfQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(tvf.SchemaObject));
        if (!resolvedViews.TryGetValue(tvfQualifiedName, out var tvfRelation))
        {
            // Not an inline/multi-statement TVF this scan parsed a body for - try a SQLCLR
            // (assembly) TVF next: the engine still exposes its return-table columns as real
            // sys.columns metadata (LiveCatalogReader registers these as CatalogTableKind.
            // ClrTableValuedFunction) even though there is no T-SQL body to resolve. A caller
            // referencing e.g. dbo.Split(...) becomes fully typeable this way; the function's own
            // body remains reported as unanalyzable, unaffected by this fallback.
            var clrTvf = catalog.Find(tvfQualifiedName, scope: null);
            if (clrTvf is { Kind: CatalogTableKind.ClrTableValuedFunction })
            {
                tvfRelation = ToResolvedRelation(clrTvf, tvfQualifiedName);
            }
            else
            {
                ledger?.Record(
                    AnalysisPass.Lineage, sourcePath, tvf.StartLine, tvf.StartColumn,
                    "FROM table-valued function", $"'{tvfQualifiedName}' is not a resolved inline/multi-statement TVF");
                tvfRelation = ResolvedRelation.Empty;
            }
        }

        var tvfAlias = tvf.Alias?.Value ?? SchemaObjectNameHelper.Resolve(tvf.SchemaObject).Name;
        return (tvfAlias, new ScopeEntry(tvfRelation, IsViewLayer: true));
    }

    /// <summary>
    /// <c>FROM (source) PIVOT (Agg(ValueCol) FOR PivotCol IN ([A],[B],...)) AS p</c> -
    /// statically resolvable end to end, unlike OPENQUERY/OPENROWSET: every piece (the pivoted-
    /// out column names, the aggregate function, the source/pivot column identifiers) is right
    /// there in the syntax, no remote schema needed. Every non-value, non-pivot column from the
    /// inner source passes through with its own provenance unchanged (PIVOT's implicit GROUP BY);
    /// each name in <c>InColumns</c> becomes a new <see cref="ColumnProvenance.Expression"/>
    /// column typed through the exact same curated aggregate-function table
    /// (<see cref="BuiltinFunctionTypeResolver"/>) an ordinary <c>SUM(x)</c> in a view's SELECT
    /// list already resolves through - oracle-verified directly: <c>PIVOT(SUM(TinyIntCol) ...)</c>
    /// widens to int, matching plain <c>SUM(TinyIntCol)</c>'s own already-verified widening rule.
    /// Only a single value column is real T-SQL syntax (ScriptDOM's <c>IList</c> shape
    /// notwithstanding) - anything else declines rather than guesses which one applies.
    /// </summary>
    private static (string? Alias, ScopeEntry Entry) ResolvePivotedTableReference(PivotedTableReference pivot, ResolutionContext context)
    {
        var (_, _, sourcePath, ledger, _, _, _) = context;
        var innerColumns = ResolveFlattenedSourceColumns(pivot.TableReference, context);

        if (pivot.ValueColumns.Count != 1)
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, pivot.StartLine, pivot.StartColumn,
                FromTableReferenceConstructKind, "PIVOT with other than exactly one value column is not modeled");
            return (pivot.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }

        var valueColumnName = pivot.ValueColumns[0].MultiPartIdentifier.Identifiers[^1].Value;
        var pivotColumnName = pivot.PivotColumn.MultiPartIdentifier.Identifiers[^1].Value;
        var aggregateFunctionName = pivot.AggregateFunctionIdentifier.Identifiers[^1].Value;

        var valueColumn = innerColumns.FirstOrDefault(c => string.Equals(c.Name, valueColumnName, StringComparison.OrdinalIgnoreCase));
        var aggregateResultType = valueColumn is not null
            ? ResolveAggregateResultType(aggregateFunctionName, ColumnProvenanceAnalysis.TryGetScalarType(valueColumn.Provenance))
            : null;

        var pivotedColumns = new List<ResolvedColumn>();
        foreach (var column in innerColumns)
        {
            if (string.Equals(column.Name, valueColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Name, pivotColumnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pivotedColumns.Add(column);
        }

        var valueInputs = valueColumn is not null ? new[] { valueColumn.Provenance } : [];
        foreach (var inColumn in pivot.InColumns)
        {
            pivotedColumns.Add(new ResolvedColumn(
                inColumn.Value,
                new ColumnProvenance.Expression(aggregateResultType, valueInputs, sourcePath, pivot.StartLine)));
        }

        return (pivot.Alias?.Value, new ScopeEntry(new ResolvedRelation(QualifiedName: null, pivotedColumns), IsViewLayer: false));
    }

    /// <summary>
    /// <c>FROM source UNPIVOT (ValueCol FOR PivotCol IN (ColA, ColB, ...)) AS u</c> - the mirror
    /// of PIVOT above, equally statically resolvable. Oracle-verified: <c>PivotCol</c> always
    /// resolves to nvarchar(128) (the same sysname-family constant this codebase's identity
    /// functions like SUSER_SNAME already use), and <c>ValueCol</c> takes the IN list's shared
    /// column type UNCHANGED - but only when every one of those columns shares the EXACT same
    /// type; the engine outright refuses to compile a type mismatch across the IN list (Msg 8167,
    /// confirmed directly), so a genuine mismatch here declines rather than guesses which type
    /// wins, honestly matching what would be a compile error on the real target anyway.
    /// </summary>
    private static (string? Alias, ScopeEntry Entry) ResolveUnpivotedTableReference(UnpivotedTableReference unpivot, ResolutionContext context)
    {
        var (_, _, sourcePath, ledger, _, _, _) = context;
        var innerColumns = ResolveFlattenedSourceColumns(unpivot.TableReference, context);

        var inColumnNames = unpivot.InColumns
            .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
            .ToList();
        var inColumnTypes = inColumnNames
            .Select(name => innerColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c is not null ? ColumnProvenanceAnalysis.TryGetScalarType(c.Provenance) : null)
            .ToList();

        SqlType? valueType = null;
        if (inColumnTypes.Count > 0 && inColumnTypes.All(t => t is not null) && inColumnTypes.Distinct().Count() == 1)
        {
            valueType = inColumnTypes[0];
        }
        else
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, unpivot.StartLine, unpivot.StartColumn,
                FromTableReferenceConstructKind, "UNPIVOT IN-list columns do not all share one resolved type - the engine itself refuses to compile a genuine mismatch (Msg 8167), and this pass never guesses which type would win");
        }

        var passthroughColumns = innerColumns
            .Where(c => !inColumnNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var valueInputs = inColumnTypes.Any(t => t is not null)
            ? innerColumns.Where(c => inColumnNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).Select(c => c.Provenance).ToArray()
            : [];

        var unpivotedColumns = new List<ResolvedColumn>(passthroughColumns)
        {
            new(unpivot.ValueColumn.Value, new ColumnProvenance.Expression(valueType, valueInputs, sourcePath, unpivot.StartLine)),
            new(unpivot.PivotColumn.Value, new ColumnProvenance.Expression(new SqlType(SqlTypeCategory.NVarChar, Length: 128), [], sourcePath, unpivot.StartLine)),
        };

        return (unpivot.Alias?.Value, new ScopeEntry(new ResolvedRelation(QualifiedName: null, unpivotedColumns), IsViewLayer: false));
    }

    /// <summary>Types a PIVOT aggregate result through the exact same curated table an ordinary aggregate function call resolves through (<see cref="BuiltinFunctionTypeResolver"/>) - fixed-return (COUNT), argument-type passthrough (MIN/MAX), or integer-widening (SUM/AVG). An aggregate not in that table, or an unresolved value column, stays Unknown rather than guessed.</summary>
    private static SqlType? ResolveAggregateResultType(string aggregateFunctionName, SqlType? valueType)
    {
        if (BuiltinFunctionTypeResolver.ResolveFixedReturnType(aggregateFunctionName) is { } fixedType)
        {
            return fixedType;
        }

        if (valueType is null || BuiltinFunctionTypeResolver.TryGetArgumentTypeIndex(aggregateFunctionName) is null)
        {
            return null;
        }

        return BuiltinFunctionTypeResolver.WidensIntegerAggregateArgument(aggregateFunctionName)
            ? BuiltinFunctionTypeResolver.WidenIntegerAggregateResult(valueType)
            : valueType;
    }

    private static (string? Alias, ScopeEntry Entry) ResolveVariableTableReference(VariableTableReference variableTable, ResolutionContext context)
    {
        var (catalog, _, sourcePath, ledger, _, procScope, _) = context;

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
                FromTableReferenceConstructKind, $"table variable '{variableName}' has no known DECLARE/RETURNS in scope");
        }

        var variableTableAlias = variableTable.Alias?.Value ?? variableName;
        return (variableTableAlias, new ScopeEntry(ToResolvedRelation(variableTableCatalog, variableName), IsViewLayer: false));
    }

    private static (string? Alias, ScopeEntry Entry) ResolveUnsupportedTableReference(TableReference tableReference, ResolutionContext context)
    {
        var (_, _, sourcePath, ledger, _, _, _) = context;

        // OPENQUERY (remote server's own dialect, no reachable schema) / a columnless
        // OPENROWSET / etc: not resolvable at all, not just "not yet resolved" - PIVOT/UNPIVOT
        // used to land here too but now have their own dedicated resolvers above. Empty columns
        // means any reference against this alias falls through to "not found".
        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, tableReference.StartLine, tableReference.StartColumn,
            FromTableReferenceConstructKind, $"unsupported table reference kind '{tableReference.GetType().Name}' (OPENQUERY/OPENROWSET/table-valued function/etc.)");
        return ((tableReference as TableReferenceWithAlias)?.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
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
