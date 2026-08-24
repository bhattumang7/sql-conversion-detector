using SilentScan.Core.Diagnostics;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public sealed record ProcedureParameterInfo(string Name, SqlType? Type, bool IsOutput);

public sealed class DatabaseCatalog
{
    private readonly Dictionary<string, CatalogTable> _tablesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType> _typeAliasesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType?> _scalarFunctionReturnTypesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, TableValuedFunctionKind> _tableValuedFunctionKindsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ScalarUdfInfo> _scalarUdfInfoByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SchemaExpressionReference> _schemaExpressions = [];

    private readonly List<ForeignKeyRelationship> _foreignKeys = [];

    private readonly List<CatalogCheckConstraint> _checkConstraints = [];
    private readonly List<CatalogTriggerEvent> _triggerEvents = [];

    private readonly List<CatalogSecurityPredicate> _securityPredicates = [];

    private readonly List<TemporalTablePair> _temporalTablePairs = [];

    private readonly List<CatalogSelectiveXmlIndexPromotedPath> _selectiveXmlIndexPromotedPaths = [];

    private readonly List<CatalogSecondarySelectiveXmlIndexReference> _secondarySelectiveXmlIndexReferences = [];

    private readonly Dictionary<(string SchemeName, int PartitionNumber), string> _partitionFilegroupsBySchemeAndNumber = [];

    private readonly Dictionary<string, IReadOnlyList<CatalogIndex>> _indexedViewIndexesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<string>> _viewCompiledColumnsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesQuotedIdentifierByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesAnsiNullsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleIsRecompiledByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesDatabaseCollationByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleIsSchemaBoundByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _synonymTargetsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<ProcedureParameterInfo>> _procedureParametersByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MaxSynonymHops = 8;

    public IReadOnlyCollection<CatalogTable> Tables => _tablesByQualifiedName.Values;

    public IReadOnlyDictionary<string, SqlType> TypeAliases => _typeAliasesByQualifiedName;

    public void AddTypeAlias(string qualifiedName, SqlType underlyingType) =>
        _typeAliasesByQualifiedName[qualifiedName] = underlyingType;

    public void AddScalarFunctionReturnType(string qualifiedName, SqlType? returnType) =>
        _scalarFunctionReturnTypesByQualifiedName[qualifiedName] = returnType;

    public void RemoveScalarFunctionReturnType(string qualifiedName) =>
        _scalarFunctionReturnTypesByQualifiedName.Remove(qualifiedName);

    public bool TryGetScalarFunctionReturnType(string qualifiedName, out SqlType? returnType) =>
        _scalarFunctionReturnTypesByQualifiedName.TryGetValue(qualifiedName, out returnType);

    public void AddTableValuedFunctionKind(string qualifiedName, TableValuedFunctionKind kind) =>
        _tableValuedFunctionKindsByQualifiedName[qualifiedName] = kind;

    public void RemoveTableValuedFunctionKind(string qualifiedName) =>
        _tableValuedFunctionKindsByQualifiedName.Remove(qualifiedName);

    public bool TryGetTableValuedFunctionKind(string qualifiedName, out TableValuedFunctionKind kind) =>
        _tableValuedFunctionKindsByQualifiedName.TryGetValue(qualifiedName, out kind);

    public void AddScalarUdfInfo(string qualifiedName, ScalarUdfInfo info) =>
        _scalarUdfInfoByQualifiedName[qualifiedName] = info;

    public void RemoveScalarUdfInfo(string qualifiedName) =>
        _scalarUdfInfoByQualifiedName.Remove(qualifiedName);

    public bool TryGetScalarUdfInfo(string qualifiedName, out ScalarUdfInfo? info) =>
        _scalarUdfInfoByQualifiedName.TryGetValue(qualifiedName, out info);

    public void AddSchemaExpression(SchemaExpressionReference reference) => _schemaExpressions.Add(reference);

    public IReadOnlyList<SchemaExpressionReference> SchemaExpressions => _schemaExpressions;

    public void AddForeignKey(ForeignKeyRelationship relationship) => _foreignKeys.Add(relationship);

    public IReadOnlyList<ForeignKeyRelationship> ForeignKeys => _foreignKeys;

    public void AddCheckConstraint(CatalogCheckConstraint constraint) => _checkConstraints.Add(constraint);

    public IReadOnlyList<CatalogCheckConstraint> CheckConstraints => _checkConstraints;

    public void AddSelectiveXmlIndexPromotedPath(CatalogSelectiveXmlIndexPromotedPath path) => _selectiveXmlIndexPromotedPaths.Add(path);

    public IReadOnlyList<CatalogSelectiveXmlIndexPromotedPath> SelectiveXmlIndexPromotedPaths => _selectiveXmlIndexPromotedPaths;

    public void AddSecondarySelectiveXmlIndexReference(CatalogSecondarySelectiveXmlIndexReference reference) => _secondarySelectiveXmlIndexReferences.Add(reference);

    public IReadOnlyList<CatalogSecondarySelectiveXmlIndexReference> SecondarySelectiveXmlIndexReferences => _secondarySelectiveXmlIndexReferences;

    public void AddSecurityPredicate(CatalogSecurityPredicate predicate) => _securityPredicates.Add(predicate);

    public IReadOnlyList<CatalogSecurityPredicate> SecurityPredicates => _securityPredicates;

    public void AddTriggerEvent(CatalogTriggerEvent triggerEvent) => _triggerEvents.Add(triggerEvent);

    public IReadOnlyList<CatalogTriggerEvent> TriggerEvents => _triggerEvents;

    public void AddTemporalTablePair(TemporalTablePair pair) => _temporalTablePairs.Add(pair);

    public IReadOnlyList<TemporalTablePair> TemporalTablePairs => _temporalTablePairs;

    public void AddPartitionFilegroup(string schemeName, int partitionNumber, string filegroupName) =>
        _partitionFilegroupsBySchemeAndNumber[(schemeName.ToUpperInvariant(), partitionNumber)] = filegroupName;

    public string? FindPartitionFilegroup(string schemeName, int partitionNumber) =>
        _partitionFilegroupsBySchemeAndNumber.GetValueOrDefault((schemeName.ToUpperInvariant(), partitionNumber));

    public void AddIndexedView(string qualifiedName, IReadOnlyList<CatalogIndex> indexes) =>
        _indexedViewIndexesByQualifiedName[qualifiedName] = indexes;

    public bool IsIndexedView(string qualifiedName) => _indexedViewIndexesByQualifiedName.ContainsKey(qualifiedName);

    public void AddViewCompiledColumns(string qualifiedName, IReadOnlyList<string> columnNames) =>
        _viewCompiledColumnsByQualifiedName[qualifiedName] = columnNames;

    public bool TryGetViewCompiledColumns(string qualifiedName, out IReadOnlyList<string> columnNames) =>
        _viewCompiledColumnsByQualifiedName.TryGetValue(qualifiedName, out columnNames!);

    public void AddModuleUsesQuotedIdentifier(string qualifiedName, bool usesQuotedIdentifier) =>
        _moduleUsesQuotedIdentifierByQualifiedName[qualifiedName] = usesQuotedIdentifier;

    public bool TryGetModuleUsesQuotedIdentifier(string qualifiedName, out bool usesQuotedIdentifier) =>
        _moduleUsesQuotedIdentifierByQualifiedName.TryGetValue(qualifiedName, out usesQuotedIdentifier);

    public void AddModuleUsesAnsiNulls(string qualifiedName, bool usesAnsiNulls) =>
        _moduleUsesAnsiNullsByQualifiedName[qualifiedName] = usesAnsiNulls;

    public bool TryGetModuleUsesAnsiNulls(string qualifiedName, out bool usesAnsiNulls) =>
        _moduleUsesAnsiNullsByQualifiedName.TryGetValue(qualifiedName, out usesAnsiNulls);

    public void AddModuleIsRecompiled(string qualifiedName, bool isRecompiled) =>
        _moduleIsRecompiledByQualifiedName[qualifiedName] = isRecompiled;

    public bool TryGetModuleIsRecompiled(string qualifiedName, out bool isRecompiled) =>
        _moduleIsRecompiledByQualifiedName.TryGetValue(qualifiedName, out isRecompiled);

    public void AddModuleUsesDatabaseCollation(string qualifiedName, bool usesDatabaseCollation) =>
        _moduleUsesDatabaseCollationByQualifiedName[qualifiedName] = usesDatabaseCollation;

    public bool TryGetModuleUsesDatabaseCollation(string qualifiedName, out bool usesDatabaseCollation) =>
        _moduleUsesDatabaseCollationByQualifiedName.TryGetValue(qualifiedName, out usesDatabaseCollation);

    public void AddModuleIsSchemaBound(string qualifiedName, bool isSchemaBound) =>
        _moduleIsSchemaBoundByQualifiedName[qualifiedName] = isSchemaBound;

    public bool TryGetModuleIsSchemaBound(string qualifiedName, out bool isSchemaBound) =>
        _moduleIsSchemaBoundByQualifiedName.TryGetValue(qualifiedName, out isSchemaBound);

    public void AddProcedureParameters(string qualifiedName, IReadOnlyList<ProcedureParameterInfo> parameters)
    {
        if (parameters.Count == 0 && _procedureParametersByQualifiedName.TryGetValue(qualifiedName, out var existing) && existing.Count > 0)
        {
            return;
        }

        _procedureParametersByQualifiedName[qualifiedName] = parameters;
    }

    public bool TryGetProcedureParameters(string qualifiedName, out IReadOnlyList<ProcedureParameterInfo> parameters) =>
        _procedureParametersByQualifiedName.TryGetValue(qualifiedName, out parameters!);

    public void AddSynonym(string qualifiedName, string targetQualifiedName) =>
        _synonymTargetsByQualifiedName[qualifiedName] = targetQualifiedName;

    public void RemoveSynonym(string qualifiedName) =>
        _synonymTargetsByQualifiedName.Remove(qualifiedName);

    public string ResolveSynonymName(string qualifiedName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = qualifiedName;

        while (_synonymTargetsByQualifiedName.TryGetValue(current, out var next))
        {
            if (!seen.Add(current) || seen.Count > MaxSynonymHops)
            {
                return qualifiedName;
            }

            current = next;
        }

        return current;
    }

    public string? CurrentDatabaseName { get; set; }

    public Collation? DefaultCollation { get; set; }

    public Collation? TempdbCollation { get; set; }

    public Collation? EffectiveTempdbCollation => TempdbCollation ?? DefaultCollation;

    public int? CompatibilityLevel { get; set; }

    public bool? IsRecursiveTriggersEnabled { get; set; }

    public bool? IsNestedTriggersEnabled { get; set; }

    public bool? IsAutoCreateStatsOn { get; set; }

    public SkipLedger Skipped { get; } = new();

    public void AddOrReplace(CatalogTable table) => AddOrReplace(table, scope: null);

    public void AddOrReplace(CatalogTable table, string? scope) =>
        _tablesByQualifiedName[Key(table.QualifiedName, scope)] = table;

    public CatalogTable? Find(string qualifiedName)
    {
        if (_tablesByQualifiedName.TryGetValue(qualifiedName, out var table))
        {
            return table;
        }

        return TryStripSelfReferencingDatabasePrefix(qualifiedName) is { } normalized
            ? _tablesByQualifiedName.GetValueOrDefault(normalized)
            : null;
    }

    public CatalogTable? Find(string qualifiedName, string? scope)
    {
        if (scope is not null && _tablesByQualifiedName.TryGetValue(Key(qualifiedName, scope), out var scoped))
        {
            return scoped;
        }

        if (qualifiedName.StartsWith('@'))
        {
            return null;
        }

        return Find(qualifiedName);
    }

    public (CatalogTable? Table, string? ActualScope) FindForMutation(string qualifiedName, string? scope)
    {
        if (scope is not null && _tablesByQualifiedName.TryGetValue(Key(qualifiedName, scope), out var scoped))
        {
            return (scoped, scope);
        }

        return (Find(qualifiedName, scope: null), null);
    }

    private string? TryStripSelfReferencingDatabasePrefix(string qualifiedName)
    {
        if (CurrentDatabaseName is not { Length: > 0 } currentDatabaseName)
        {
            return null;
        }

        var prefix = currentDatabaseName + ".";
        return qualifiedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? qualifiedName[prefix.Length..] : null;
    }

    public void Remove(string qualifiedName, string? scope)
    {
        if (scope is not null)
        {
            _tablesByQualifiedName.Remove(Key(qualifiedName, scope));
        }

        _tablesByQualifiedName.Remove(qualifiedName);
    }

    private static string Key(string qualifiedName, string? scope) =>
        scope is null ? qualifiedName : $"{scope}::{qualifiedName}";

    public void MergeFileModeExtras(DatabaseCatalog fileModeCatalog)
    {
        foreach (var (key, table) in fileModeCatalog._tablesByQualifiedName)
        {
            if (table.Kind is CatalogTableKind.TemporaryTable or CatalogTableKind.TableVariable or CatalogTableKind.TableType)
            {
                _tablesByQualifiedName[key] = table;
            }
        }

        foreach (var (qualifiedName, returnType) in fileModeCatalog._scalarFunctionReturnTypesByQualifiedName)
        {
            _scalarFunctionReturnTypesByQualifiedName[qualifiedName] = returnType;
        }

        foreach (var (qualifiedName, parameters) in fileModeCatalog._procedureParametersByQualifiedName)
        {
            AddProcedureParameters(qualifiedName, parameters);
        }

        foreach (var (qualifiedName, kind) in fileModeCatalog._tableValuedFunctionKindsByQualifiedName)
        {
            _tableValuedFunctionKindsByQualifiedName.TryAdd(qualifiedName, kind);
        }

        foreach (var (qualifiedName, fileInfo) in fileModeCatalog._scalarUdfInfoByQualifiedName)
        {
            _scalarUdfInfoByQualifiedName[qualifiedName] = _scalarUdfInfoByQualifiedName.TryGetValue(qualifiedName, out var liveInfo)
                ? liveInfo with
                {
                    InlineabilityBlocker = liveInfo.InlineabilityBlocker ?? fileInfo.InlineabilityBlocker,
                    InlineabilityTableReferenceCount = liveInfo.InlineabilityTableReferenceCount ?? fileInfo.InlineabilityTableReferenceCount,
                }
                : fileInfo;
        }

        Skipped.AddRange(fileModeCatalog.Skipped.Entries);
    }
}
