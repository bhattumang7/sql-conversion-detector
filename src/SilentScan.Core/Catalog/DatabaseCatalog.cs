using SilentScan.Core.Diagnostics;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public sealed record ProcedureParameterInfo(string Name, SqlType? Type, bool IsOutput);

public sealed class DatabaseCatalog
{
    private StringComparer _identifierComparer = StringComparer.OrdinalIgnoreCase;

    private Dictionary<string, CatalogTable> _tablesByQualifiedName;

    private Dictionary<string, SqlType> _typeAliasesByQualifiedName;

    private Dictionary<string, SqlType?> _scalarFunctionReturnTypesByQualifiedName;

    private Dictionary<string, TableValuedFunctionKind> _tableValuedFunctionKindsByQualifiedName;

    private Dictionary<string, ScalarUdfInfo> _scalarUdfInfoByQualifiedName;

    private readonly List<SchemaExpressionReference> _schemaExpressions = [];

    private readonly List<ForeignKeyRelationship> _foreignKeys = [];

    private readonly List<CatalogCheckConstraint> _checkConstraints = [];
    private readonly List<CatalogTriggerEvent> _triggerEvents = [];
    private readonly List<CatalogAlterColumnEvent> _alterColumnEvents = [];

    private readonly List<CatalogSecurityPredicate> _securityPredicates = [];

    private readonly List<TemporalTablePair> _temporalTablePairs = [];

    private readonly List<CatalogSelectiveXmlIndexPromotedPath> _selectiveXmlIndexPromotedPaths = [];

    private readonly List<CatalogSecondarySelectiveXmlIndexReference> _secondarySelectiveXmlIndexReferences = [];

    private readonly Dictionary<(string SchemeName, int PartitionNumber), string> _partitionFilegroupsBySchemeAndNumber = [];

    private Dictionary<string, IReadOnlyList<CatalogIndex>> _indexedViewIndexesByQualifiedName;

    private Dictionary<string, IReadOnlyList<string>> _viewCompiledColumnsByQualifiedName;

    private Dictionary<string, bool> _moduleUsesQuotedIdentifierByQualifiedName;

    private Dictionary<string, bool> _moduleUsesAnsiNullsByQualifiedName;

    private Dictionary<string, bool> _moduleIsRecompiledByQualifiedName;

    private Dictionary<string, bool> _moduleUsesDatabaseCollationByQualifiedName;

    private Dictionary<string, bool> _moduleIsSchemaBoundByQualifiedName;

    private Dictionary<string, string> _synonymTargetsByQualifiedName;

    private Dictionary<string, IReadOnlyList<ProcedureParameterInfo>> _procedureParametersByQualifiedName;

    private Dictionary<string, bool> _columnMasterKeyEnclaveSupportByName;

    private Dictionary<string, IReadOnlyList<string>> _columnEncryptionKeyMasterKeysByName;

    private const int MaxSynonymHops = 8;

    public DatabaseCatalog()
    {
        _tablesByQualifiedName = new(_identifierComparer);
        _typeAliasesByQualifiedName = new(_identifierComparer);
        _scalarFunctionReturnTypesByQualifiedName = new(_identifierComparer);
        _tableValuedFunctionKindsByQualifiedName = new(_identifierComparer);
        _scalarUdfInfoByQualifiedName = new(_identifierComparer);
        _indexedViewIndexesByQualifiedName = new(_identifierComparer);
        _viewCompiledColumnsByQualifiedName = new(_identifierComparer);
        _moduleUsesQuotedIdentifierByQualifiedName = new(_identifierComparer);
        _moduleUsesAnsiNullsByQualifiedName = new(_identifierComparer);
        _moduleIsRecompiledByQualifiedName = new(_identifierComparer);
        _moduleUsesDatabaseCollationByQualifiedName = new(_identifierComparer);
        _moduleIsSchemaBoundByQualifiedName = new(_identifierComparer);
        _synonymTargetsByQualifiedName = new(_identifierComparer);
        _procedureParametersByQualifiedName = new(_identifierComparer);
        _columnMasterKeyEnclaveSupportByName = new(_identifierComparer);
        _columnEncryptionKeyMasterKeysByName = new(_identifierComparer);
    }

    public StringComparer IdentifierComparer => _identifierComparer;

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

    public void AddAlterColumnEvent(CatalogAlterColumnEvent alterColumnEvent) => _alterColumnEvents.Add(alterColumnEvent);

    public IReadOnlyList<CatalogAlterColumnEvent> AlterColumnEvents => _alterColumnEvents;

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

    public bool ResolveDynamicSqlQuotedIdentifier(string? enclosingModuleQualifiedName)
    {
        if (enclosingModuleQualifiedName is not null && TryGetModuleUsesQuotedIdentifier(enclosingModuleQualifiedName, out var usesQuotedIdentifier))
        {
            return usesQuotedIdentifier;
        }

        return true;
    }

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

    public void AddColumnMasterKey(string name, bool supportsEnclaveComputations) =>
        _columnMasterKeyEnclaveSupportByName[name] = supportsEnclaveComputations;

    public void AddColumnEncryptionKey(string name, IReadOnlyList<string> columnMasterKeyNames) =>
        _columnEncryptionKeyMasterKeysByName[name] = columnMasterKeyNames;

    public ColumnEncryptionEnclaveSupport ResolveColumnEncryptionKeyEnclaveSupport(string columnEncryptionKeyName)
    {
        if (!_columnEncryptionKeyMasterKeysByName.TryGetValue(columnEncryptionKeyName, out var masterKeyNames) || masterKeyNames.Count == 0)
        {
            return ColumnEncryptionEnclaveSupport.Unknown;
        }

        bool? supportsEnclave = null;
        foreach (var masterKeyName in masterKeyNames)
        {
            if (!_columnMasterKeyEnclaveSupportByName.TryGetValue(masterKeyName, out var masterKeySupportsEnclave))
            {
                return ColumnEncryptionEnclaveSupport.Unknown;
            }

            if (supportsEnclave is null)
            {
                supportsEnclave = masterKeySupportsEnclave;
            }
            else if (supportsEnclave != masterKeySupportsEnclave)
            {
                return ColumnEncryptionEnclaveSupport.Unknown;
            }
        }

        return supportsEnclave == true ? ColumnEncryptionEnclaveSupport.Enabled : ColumnEncryptionEnclaveSupport.Disabled;
    }

    public void AddSynonym(string qualifiedName, string targetQualifiedName) =>
        _synonymTargetsByQualifiedName[qualifiedName] = targetQualifiedName;

    public void RemoveSynonym(string qualifiedName) =>
        _synonymTargetsByQualifiedName.Remove(qualifiedName);

    public string ResolveSynonymName(string qualifiedName)
    {
        var seen = new HashSet<string>(_identifierComparer);
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

    private Collation? _defaultCollation;

    public Collation? DefaultCollation
    {
        get => _defaultCollation;
        set
        {
            _defaultCollation = value;
            SyncIdentifierComparer();
        }
    }

    private Collation? _tempdbCollation;

    public Collation? TempdbCollation
    {
        get => _tempdbCollation;
        set
        {
            _tempdbCollation = value;
            SyncIdentifierComparer();
        }
    }

    public Collation? EffectiveTempdbCollation => TempdbCollation ?? DefaultCollation;

    private StringComparer? _activeDefaultComparer;
    private StringComparer? _activeTempComparer;

    private void SyncIdentifierComparer()
    {
        var defaultComparer = Collation.IdentifierComparer(_defaultCollation);
        var tempComparer = Collation.IdentifierComparer(EffectiveTempdbCollation);
        if (ReferenceEquals(defaultComparer, _activeDefaultComparer) && ReferenceEquals(tempComparer, _activeTempComparer))
        {
            return;
        }

        _activeDefaultComparer = defaultComparer;
        _activeTempComparer = tempComparer;

        var comparer = ReferenceEquals(defaultComparer, tempComparer)
            ? defaultComparer
            : new TempScopedIdentifierComparer(defaultComparer, tempComparer);

        _identifierComparer = comparer;
        _tablesByQualifiedName = new(_tablesByQualifiedName, comparer);
        _typeAliasesByQualifiedName = new(_typeAliasesByQualifiedName, comparer);
        _scalarFunctionReturnTypesByQualifiedName = new(_scalarFunctionReturnTypesByQualifiedName, comparer);
        _tableValuedFunctionKindsByQualifiedName = new(_tableValuedFunctionKindsByQualifiedName, comparer);
        _scalarUdfInfoByQualifiedName = new(_scalarUdfInfoByQualifiedName, comparer);
        _indexedViewIndexesByQualifiedName = new(_indexedViewIndexesByQualifiedName, comparer);
        _viewCompiledColumnsByQualifiedName = new(_viewCompiledColumnsByQualifiedName, comparer);
        _moduleUsesQuotedIdentifierByQualifiedName = new(_moduleUsesQuotedIdentifierByQualifiedName, comparer);
        _moduleUsesAnsiNullsByQualifiedName = new(_moduleUsesAnsiNullsByQualifiedName, comparer);
        _moduleIsRecompiledByQualifiedName = new(_moduleIsRecompiledByQualifiedName, comparer);
        _moduleUsesDatabaseCollationByQualifiedName = new(_moduleUsesDatabaseCollationByQualifiedName, comparer);
        _moduleIsSchemaBoundByQualifiedName = new(_moduleIsSchemaBoundByQualifiedName, comparer);
        _synonymTargetsByQualifiedName = new(_synonymTargetsByQualifiedName, comparer);
        _procedureParametersByQualifiedName = new(_procedureParametersByQualifiedName, comparer);
        _columnMasterKeyEnclaveSupportByName = new(_columnMasterKeyEnclaveSupportByName, comparer);
        _columnEncryptionKeyMasterKeysByName = new(_columnEncryptionKeyMasterKeysByName, comparer);
    }

    private sealed class TempScopedIdentifierComparer(StringComparer defaultComparer, StringComparer tempComparer) : StringComparer
    {
        private StringComparer ComparerFor(string? value)
        {
            if (value is null)
            {
                return defaultComparer;
            }

            var scopeSeparator = value.LastIndexOf("::", StringComparison.Ordinal);
            var name = scopeSeparator < 0 ? value : value[(scopeSeparator + 2)..];
            return name is { Length: > 0 } && name[0] == '#' ? tempComparer : defaultComparer;
        }

        public override int Compare(string? x, string? y) => ComparerFor(x).Compare(x, y);

        public override bool Equals(string? x, string? y) => ComparerFor(x).Equals(x, y);

        public override int GetHashCode(string obj) => ComparerFor(obj).GetHashCode(obj);
    }

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
        var comparison = _identifierComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return qualifiedName.StartsWith(prefix, comparison) ? qualifiedName[prefix.Length..] : null;
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
