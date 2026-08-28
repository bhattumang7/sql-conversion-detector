using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class FromScopeResolver
{
    private const string FromTableReferenceConstructKind = "FROM table reference";

    internal readonly record struct ResolutionContext(
        DatabaseCatalog Catalog,
        IReadOnlyDictionary<string, ResolvedRelation> ResolvedViews,
        string SourcePath,
        SkipLedger? Ledger,
        IReadOnlyDictionary<string, ResolvedRelation> CteRelations,
        string? ProcScope,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? CallerScopeByCalleeScope = null);

    public static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) Resolve(
        FromClause? fromClause,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        string sourcePath,
        SkipLedger? ledger,
        IReadOnlyDictionary<string, ResolvedRelation> cteRelations,
        string? procScope) =>
        Resolve(fromClause, new ResolutionContext(catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope));

    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) Resolve(FromClause? fromClause, ResolutionContext context)
    {
        var byAlias = new Dictionary<string, ScopeEntry>(context.Catalog.IdentifierComparer);
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

    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) ResolveForDataModification(
        TableReference target, FromClause? extraFromClause, ResolutionContext context)
    {
        if (extraFromClause is not null)
        {
            return Resolve(extraFromClause, context);
        }

        var byAlias = new Dictionary<string, ScopeEntry>(context.Catalog.IdentifierComparer);
        var ordered = new List<ScopeEntry>();
        AddResolved(target, context, aliasOverride: null, byAlias, ordered);
        return (byAlias, ordered);
    }

    internal static (Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered) ResolveForMerge(
        TableReference target, Identifier? targetAlias, TableReference source, ResolutionContext context)
    {
        var byAlias = new Dictionary<string, ScopeEntry>(context.Catalog.IdentifierComparer);
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

    private static readonly HashSet<string> SystemDatabaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "msdb", "tempdb",
    };

    private static bool IsSystemDatabaseReference(SchemaObjectName schemaObject) =>
        schemaObject.DatabaseIdentifier is { Value.Length: > 0 } db && SystemDatabaseNames.Contains(db.Value);

    private static (string? Alias, ScopeEntry Entry) ResolveNamedTableReference(NamedTableReference named, ResolutionContext context, string? aliasOverride)
    {
        var (catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope, callerScopeByCalleeScope) = context;

        if (named.SchemaObject.SchemaIdentifier is null
            && cteRelations is not null
            && cteRelations.TryGetValue(named.SchemaObject.BaseIdentifier.Value, out var cteRelation))
        {
            var cteAlias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            return (cteAlias, new ScopeEntry(cteRelation, IsViewLayer: false));
        }

        if (named.SchemaObject.ServerIdentifier is { Value.Length: > 0 })
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, named.StartLine, named.StartColumn,
                FromTableReferenceConstructKind, $"'{SchemaObjectNameHelper.Qualify(named.SchemaObject)}': names a linked server - four-part cross-server table references are not modeled");
            return (named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
        var isViewLayer = resolvedViews.TryGetValue(qualifiedName, out var view);
        var catalogTable = catalog.Find(qualifiedName, procScope);

        if (catalogTable is null
            && procScope is not null
            && callerScopeByCalleeScope is not null
            && callerScopeByCalleeScope.TryGetValue(procScope, out var callerScopes))
        {
            catalogTable = TryResolveFromCallerScopes(catalog, qualifiedName, callerScopes);
        }

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

        var innerColumns = QueryExpressionResolver.Resolve(derived.QueryExpression, catalog, resolvedViews, sourcePath, ledger, cteRelations, procScope);
        if (derived.Columns.Count > 0)
        {
            if (innerColumns.Count == derived.Columns.Count)
            {
                innerColumns = [.. innerColumns.Zip(derived.Columns, (c, id) => c with { Name = id.Value })];
            }
            else
            {

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

        if (tvf.SchemaObject.ServerIdentifier is { Value.Length: > 0 })
        {
            ledger?.Record(
                AnalysisPass.Lineage, sourcePath, tvf.StartLine, tvf.StartColumn,
                "FROM table-valued function", $"'{SchemaObjectNameHelper.Qualify(tvf.SchemaObject)}': names a linked server - four-part cross-server table references are not modeled");
            return (tvf.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
        }

        var tvfQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(tvf.SchemaObject));
        if (!resolvedViews.TryGetValue(tvfQualifiedName, out var tvfRelation))
        {

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

    private static (string? Alias, ScopeEntry Entry) ResolvePivotedTableReference(PivotedTableReference pivot, ResolutionContext context)
    {
        var (catalog, _, sourcePath, ledger, _, _, _) = context;
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

        var valueColumn = innerColumns.FirstOrDefault(c => catalog.IdentifierComparer.Equals(c.Name, valueColumnName));
        var aggregateResultType = valueColumn is not null
            ? ResolveAggregateResultType(aggregateFunctionName, ColumnProvenanceAnalysis.TryGetScalarType(valueColumn.Provenance))
            : null;

        var pivotedColumns = new List<ResolvedColumn>();
        foreach (var column in innerColumns)
        {
            if (catalog.IdentifierComparer.Equals(column.Name, valueColumnName)
                || catalog.IdentifierComparer.Equals(column.Name, pivotColumnName))
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

    private static (string? Alias, ScopeEntry Entry) ResolveUnpivotedTableReference(UnpivotedTableReference unpivot, ResolutionContext context)
    {
        var (catalog, _, sourcePath, ledger, _, _, _) = context;
        var innerColumns = ResolveFlattenedSourceColumns(unpivot.TableReference, context);

        var inColumnNames = unpivot.InColumns
            .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
            .ToList();
        var inColumnTypes = inColumnNames
            .Select(name => innerColumns.FirstOrDefault(c => catalog.IdentifierComparer.Equals(c.Name, name)))
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
            .Where(c => !inColumnNames.Contains(c.Name, catalog.IdentifierComparer))
            .ToList();

        var valueInputs = inColumnTypes.Any(t => t is not null)
            ? innerColumns.Where(c => inColumnNames.Contains(c.Name, catalog.IdentifierComparer)).Select(c => c.Provenance).ToArray()
            : [];

        var unpivotedColumns = new List<ResolvedColumn>(passthroughColumns)
        {
            new(unpivot.ValueColumn.Value, new ColumnProvenance.Expression(valueType, valueInputs, sourcePath, unpivot.StartLine)),
            new(unpivot.PivotColumn.Value, new ColumnProvenance.Expression(new SqlType(SqlTypeCategory.NVarChar, Length: 128), [], sourcePath, unpivot.StartLine)),
        };

        return (unpivot.Alias?.Value, new ScopeEntry(new ResolvedRelation(QualifiedName: null, unpivotedColumns), IsViewLayer: false));
    }

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

        ledger?.Record(
            AnalysisPass.Lineage, sourcePath, tableReference.StartLine, tableReference.StartColumn,
            FromTableReferenceConstructKind, $"unsupported table reference kind '{tableReference.GetType().Name}' (OPENQUERY/OPENROWSET/table-valued function/etc.)");
        return ((tableReference as TableReferenceWithAlias)?.Alias?.Value, new ScopeEntry(ResolvedRelation.Empty, IsViewLayer: false));
    }

    internal static ResolvedRelation ToResolvedRelation(CatalogTable? table, string qualifiedName)
    {
        if (table is null)
        {

            return new ResolvedRelation(qualifiedName, []);
        }

        return new ResolvedRelation(qualifiedName, [.. table.Columns.Select(c => new ResolvedColumn(
            c.Name,
            c.Type is { } type
                ? new ColumnProvenance.BaseColumn(qualifiedName, c.Name, type)
                : new ColumnProvenance.Unknown($"column {c.Name} has an unresolved declared type")))]);
    }

    private static ResolvedRelation ToSystemCatalogRelation(IReadOnlyList<(string Name, SqlType Type)> columns, string qualifiedName) =>
        new(qualifiedName, [.. columns.Select(c => new ResolvedColumn(c.Name, new ColumnProvenance.BaseColumn(qualifiedName, c.Name, c.Type)))]);

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

    internal static ResolvedRelation ToPseudoTableRelation(ResolvedRelation viewRelation, string qualifiedName) =>
        new(qualifiedName, [.. viewRelation.Columns.Select(c => c.Provenance switch
        {
            ColumnProvenance.BaseColumn { Type: { } type } => c with { Provenance = new ColumnProvenance.Declared(type, qualifiedName) },
            ColumnProvenance.BaseColumn => c with { Provenance = new ColumnProvenance.Unknown("pseudo-table column type could not be resolved") },
            _ => c,
        })]);
}
