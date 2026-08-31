using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public static class CatalogBuilder
{
    private const string SpRenameConstructKind = "sp_rename";

    public static DatabaseCatalog Build(
        IEnumerable<SqlParseResult> parseResults, string? manifestDeclaredCollation = null, string? manifestTempdbCollation = null,
        bool? manifestAnsiNullDefaultOn = null, IScanStage? stage = null, IEnumerable<CatalogTable>? knownTables = null)
    {
        var catalog = new DatabaseCatalog();
        var results = parseResults as IReadOnlyList<SqlParseResult> ?? parseResults.ToList();

        catalog.DefaultCollation = ResolveDefaultCollation(results, manifestDeclaredCollation);
        catalog.TempdbCollation = manifestTempdbCollation is { Length: > 0 }
            ? new Collation(manifestTempdbCollation, CollationSource.DatabaseDefaultFromManifest)
            : null;
        catalog.IsAnsiNullDefaultOn = manifestAnsiNullDefaultOn;

        if (knownTables is not null)
        {
            foreach (var table in knownTables)
            {
                catalog.AddOrReplace(table);
            }
        }

        var pendingDrops = new List<PendingDrop>();

        foreach (var result in results)
        {
            stage?.Advance(currentItem: result.SourcePath);
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.CollectTypeAliases, result.Fragment, pendingDrops));
        }

        foreach (var result in results)
        {
            stage?.Advance(currentItem: result.SourcePath);
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.CollectTables, result.Fragment, pendingDrops));
        }

        var stillPending = new List<PendingDrop>();
        foreach (var pending in pendingDrops)
        {
            var match = catalog.Find(pending.QualifiedName, pending.Scope);
            if (match is null)
            {
                stillPending.Add(pending);
            }
            else if (!string.Equals(match.SourcePath, pending.SourcePath, StringComparison.Ordinal))
            {
                catalog.Remove(pending.QualifiedName, pending.Scope);
            }
            else
            {
                RecordUnresolvedDrop(catalog, pending);
            }
        }

        var pendingByNode = stillPending
            .GroupBy(p => p.Node)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PendingDrop>)g.ToList());

        foreach (var result in results)
        {
            stage?.Advance(currentItem: result.SourcePath);
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.ApplyEverythingElse, result.Fragment, pendingDrops, pendingByNode));
        }

        return catalog;
    }

    private readonly record struct PendingDrop(DropTableStatement Node, string QualifiedName, string? Scope, string SourcePath);

    private static void RecordUnresolvedDrop(DatabaseCatalog catalog, PendingDrop pending) =>
        catalog.Skipped.Record(
            AnalysisPass.Catalog, pending.SourcePath, pending.Node.StartLine, pending.Node.StartColumn,
            "DROP TABLE", $"target table '{pending.QualifiedName}' not found in catalog (cross-file/cross-database reference, or failed base CREATE TABLE)");

    private static void Walk(SqlParseResult result, Visitor visitor)
    {
        if (result.Fragment is not TSqlScript script)
        {
            return;
        }

        foreach (var batch in script.Batches)
        {
            batch.Accept(visitor);
        }
    }

    private static Collation? ResolveDefaultCollation(IReadOnlyList<SqlParseResult> results, string? manifestDeclaredCollation)
    {
        if (FindExplicitDatabaseCollation(results) is { } explicitName)
        {
            return new Collation(explicitName, CollationSource.DatabaseDefaultFromDdl);
        }

        return manifestDeclaredCollation is { Length: > 0 }
            ? new Collation(manifestDeclaredCollation, CollationSource.DatabaseDefaultFromManifest)
            : null;
    }

    private static string? FindExplicitDatabaseCollation(IReadOnlyList<SqlParseResult> results)
    {
        string? found = null;
        foreach (var result in results)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                var collation = statement switch
                {
                    CreateDatabaseStatement { Collation.Value: { } name } => name,
                    AlterDatabaseCollateStatement { Collation.Value: { } name } => name,
                    _ => null,
                };

                if (collation is not null)
                {
                    found = collation;
                }
            }
        }

        return found;
    }

    private enum BuildPhase
    {
        CollectTypeAliases,
        CollectTables,
        ApplyEverythingElse,
    }

    private sealed class Visitor(
        DatabaseCatalog catalog, string sourcePath, BuildPhase phase, TSqlFragment fragment,
        List<PendingDrop> pendingDrops, IReadOnlyDictionary<DropTableStatement, IReadOnlyList<PendingDrop>>? pendingDropsByNode = null) : TSqlFragmentVisitor
    {
        private readonly Dictionary<TSqlStatement, bool?> _ansiNullDfltByStatement = AnsiNullDfltFlowResolver.Resolve(fragment);

        private string? _currentScope;

        private bool ResolveDefaultNullable(TSqlStatement node) =>
            (_ansiNullDfltByStatement.TryGetValue(node, out var value) ? value : null) ?? catalog.IsAnsiNullDefaultOn ?? true;

        public override void ExplicitVisit(CreateTypeUddtStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                VisitCreateTypeAlias(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateTypeTableStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                var (schema, name) = SchemaObjectNameHelper.Resolve(node.Name);
                var (columns, indexesFromColumns) = BuildColumns(node.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath, catalog, defaultNullable: true);
                var indexesFromConstraints = BuildIndexesFromTableConstraints(node.Definition.TableConstraints);

                catalog.AddOrReplace(new CatalogTable(
                    schema,
                    name,
                    CatalogTableKind.TableType,
                    columns,
                    [.. indexesFromColumns, .. indexesFromConstraints],
                    sourcePath,
                    node.StartLine));
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateColumnMasterKeyStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                var supportsEnclave = node.Parameters.OfType<ColumnMasterKeyEnclaveComputationsParameter>().Any();
                catalog.AddColumnMasterKey(node.Name.Value, supportsEnclave);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateColumnEncryptionKeyStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                var masterKeyNames = node.ColumnEncryptionKeyValues
                    .SelectMany(value => value.Parameters.OfType<ColumnMasterKeyNameParameter>())
                    .Select(parameter => parameter.Name.Value)
                    .ToList();
                catalog.AddColumnEncryptionKey(node.Name.Value, masterKeyNames);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateSynonymStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                VisitCreateSynonym(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DropSynonymStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                foreach (var target in node.Objects)
                {
                    catalog.RemoveSynonym(SchemaObjectNameHelper.Qualify(target));
                }
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateAssemblyStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR assembly", $"'{node.Name.Value}': CREATE ASSEMBLY is not modeled - CLR types/functions/aggregates it backs resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateAggregateStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR aggregate", $"'{SchemaObjectNameHelper.Qualify(node.Name)}': CREATE AGGREGATE is not modeled - not usable in typed comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateTypeUdtStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR user-defined type", $"'{SchemaObjectNameHelper.Qualify(node.Name)}': CREATE TYPE ... EXTERNAL NAME is not modeled - columns of this type resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateFullTextIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "full-text index", $"'{SchemaObjectNameHelper.Qualify(node.OnName)}': CREATE FULLTEXT INDEX is not modeled - supports CONTAINS/FREETEXT, not the comparison operators this tool classifies");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterFullTextIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "full-text index", $"'{SchemaObjectNameHelper.Qualify(node.OnName)}': ALTER FULLTEXT INDEX is not modeled - supports CONTAINS/FREETEXT, not the comparison operators this tool classifies");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateSpatialIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "spatial index", $"'{SchemaObjectNameHelper.Qualify(node.Object)}': CREATE SPATIAL INDEX is not modeled - supports spatial predicates (STIntersects etc.), not equality/range comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateXmlIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "XML index", $"CREATE XML INDEX on column '{node.XmlColumn.Value}' is not modeled - supports XQuery, not scalar comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateSelectiveXmlIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitCreateSelectiveXmlIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateExternalTableStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "external table", $"'{SchemaObjectNameHelper.Qualify(node.SchemaObjectName)}': CREATE EXTERNAL TABLE is not modeled - column shapes live in the external data source, which this tool never connects to");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterAssemblyStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR assembly", $"'{node.Name.Value}': ALTER ASSEMBLY is not modeled - CLR types/functions/aggregates it backs resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (phase == BuildPhase.CollectTables)
            {
                VisitCreateTable(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DropTableStatement node)
        {
            if (phase == BuildPhase.CollectTables)
            {
                VisitDropTable(node, isFinalAttempt: false);
            }
            else if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDropTable(node, isFinalAttempt: true);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DropIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDropIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DropFunctionStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                foreach (var target in node.Objects)
                {
                    var qualifiedName = SchemaObjectNameHelper.Qualify(target);
                    catalog.RemoveScalarFunctionReturnType(qualifiedName);
                    catalog.RemoveTableValuedFunctionKind(qualifiedName);
                    catalog.RemoveScalarUdfInfo(qualifiedName);
                }
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitPossibleSpRename(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(UseStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "USE", $"'{node.DatabaseName.Value}': database context switching is not modeled - every scanned object still resolves against the single implicit target database");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterTableAdd(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableAlterColumnStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterColumn(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableDropTableElementStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDropTableElements(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitCreateIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateColumnStoreIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitCreateColumnStoreIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDeclareTableVariable(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse && node.Into is not null)
            {
                VisitSelectInto(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        private void VisitScopedBody(TSqlFragment node, SchemaObjectName name)
        {
            var previous = _currentScope;
            _currentScope = SchemaObjectNameHelper.Qualify(name);

            if (phase == BuildPhase.ApplyEverythingElse && node is ProcedureStatementBodyBase { Parameters: { } parameters })
            {
                RegisterTableValuedParameters(parameters);

                if (node is CreateProcedureStatement or AlterProcedureStatement or CreateOrAlterProcedureStatement)
                {
                    RegisterProcedureParameters(parameters);
                }
            }

            node.AcceptChildren(this);
            _currentScope = previous;
        }

        private void RegisterTableValuedParameters(IList<ProcedureParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.DataType is not UserDataTypeReference userType)
                {
                    continue;
                }

                var typeQualifiedName = SchemaObjectNameHelper.Qualify(userType.Name);
                if (catalog.Find(typeQualifiedName) is not { Kind: CatalogTableKind.TableType } tableType)
                {
                    continue;
                }

                catalog.AddOrReplace(
                    tableType with { SchemaName = null, Name = parameter.VariableName.Value, Kind = CatalogTableKind.TableVariable },
                    _currentScope);
            }
        }

        private void RegisterProcedureParameters(IList<ProcedureParameter> parameters)
        {
            var registered = new List<ProcedureParameterInfo>(parameters.Count);
            foreach (var parameter in parameters)
            {
                var isTableValued = parameter.DataType is UserDataTypeReference userType
                    && catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is { Kind: CatalogTableKind.TableType };
                var resolvedType = isTableValued ? null : SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
                registered.Add(new ProcedureParameterInfo(parameter.VariableName.Value, resolvedType, parameter.Modifier == ParameterModifier.Output));
            }

            catalog.AddProcedureParameters(_currentScope!, registered);
        }

        private void VisitFunctionBody(FunctionStatementBody node, SchemaObjectName name, FunctionReturnType returnType)
        {
            if (phase == BuildPhase.ApplyEverythingElse && returnType is ScalarFunctionReturnType scalarReturn)
            {
                var resolvedType = SqlTypeReferenceResolver.Resolve(scalarReturn.DataType, columnCollation: null, catalog.TypeAliases);
                if (resolvedType is { IsStringFamily: true, Collation: null } && catalog.DefaultCollation is not null)
                {
                    resolvedType = resolvedType with { Collation = catalog.DefaultCollation };
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(name);
                catalog.AddScalarFunctionReturnType(qualifiedName, resolvedType);
                RegisterScalarUdfInfo(node, qualifiedName);
            }

            if (phase == BuildPhase.ApplyEverythingElse && returnType is not ScalarFunctionReturnType)
            {
                var tvfKind = returnType switch
                {
                    SelectFunctionReturnType => (TableValuedFunctionKind?)TableValuedFunctionKind.Inline,
                    TableValuedFunctionReturnType { DeclareTableVariableBody.VariableName: not null } => TableValuedFunctionKind.MultiStatement,
                    TableValuedFunctionReturnType => TableValuedFunctionKind.Clr,
                    _ => null,
                };

                if (tvfKind is { } resolvedKind)
                {
                    catalog.AddTableValuedFunctionKind(SchemaObjectNameHelper.Qualify(name), resolvedKind);
                }
            }

            if (phase == BuildPhase.ApplyEverythingElse
                && returnType is TableValuedFunctionReturnType { DeclareTableVariableBody: { VariableName: { } variableName, Definition: { } definition } body })
            {
                var (columns, indexesFromColumns) = BuildColumns(definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath, catalog, defaultNullable: true);
                var indexesFromConstraints = BuildIndexesFromTableConstraints(definition.TableConstraints);

                var returnTable = new CatalogTable(
                    SchemaName: null,
                    variableName.Value,
                    CatalogTableKind.TableVariable,
                    columns,
                    [.. indexesFromColumns, .. indexesFromConstraints],
                    sourcePath,
                    body.StartLine);

                catalog.AddOrReplace(returnTable, SchemaObjectNameHelper.Qualify(name));
            }

            VisitScopedBody(node, name);
        }

        private void RegisterScalarUdfInfo(FunctionStatementBody node, string qualifiedName)
        {
            var isClr = node.MethodSpecifier is not null;
            var isSchemaBound = node.Options.Any(option => option.OptionKind == FunctionOptionKind.SchemaBinding);
            var (blocker, tableReferenceCount) = isClr
                ? (null, 0)
                : ScalarUdfInlineabilityScanner.FindBlocker(node.StatementList, qualifiedName, catalog, node.Parameters);

            catalog.AddScalarUdfInfo(
                qualifiedName,
                new ScalarUdfInfo(
                    Kind: isClr ? ScalarUdfKind.Clr : ScalarUdfKind.TSql,
                    IsSchemaBound: isSchemaBound,
                    EngineIsInlineable: null,
                    InlineabilityBlocker: blocker,
                    ClrDataAccess: null,
                    InlineabilityTableReferenceCount: isClr ? null : tableReferenceCount));
        }

        private void VisitCreateTypeAlias(CreateTypeUddtStatement createType)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createType.Name);

            var underlyingType = SqlTypeReferenceResolver.Resolve(createType.DataType, columnCollation: null, typeAliases: null);
            if (underlyingType is null)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createType.StartLine, createType.StartColumn,
                    "CREATE TYPE ... FROM", $"'{qualifiedName}': underlying type could not be resolved");
                return;
            }

            catalog.AddTypeAlias(qualifiedName, underlyingType);
        }

        private void VisitCreateSynonym(CreateSynonymStatement createSynonym)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createSynonym.Name);

            if (createSynonym.ForName.ServerIdentifier is { Value.Length: > 0 })
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createSynonym.StartLine, createSynonym.StartColumn,
                    "CREATE SYNONYM", $"'{qualifiedName}': FOR target names a linked server - four-part cross-server synonyms are not modeled");
                return;
            }

            catalog.AddSynonym(qualifiedName, SchemaObjectNameHelper.Qualify(createSynonym.ForName));
        }

        private void VisitAlterIndex(AlterIndexStatement alterIndex)
        {
            if (alterIndex.AlterIndexType is not (AlterIndexType.Disable or AlterIndexType.Rebuild))
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, alterIndex.StartLine, alterIndex.StartColumn,
                    "ALTER INDEX", $"'{alterIndex.Name?.Value ?? "ALL"}' on '{SchemaObjectNameHelper.Qualify(alterIndex.OnName)}': {alterIndex.AlterIndexType} does not affect seekability and is not modeled");
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(alterIndex.OnName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER INDEX", qualifiedName, alterIndex);
                return;
            }

            var targetDisabledState = alterIndex.AlterIndexType == AlterIndexType.Disable;
            var indexName = alterIndex.Name?.Value;

            var updatedIndexes = existing.Indexes
                .Select(i => indexName is null || string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase)
                    ? i with { IsDisabled = targetDisabledState }
                    : i)
                .ToList();

            catalog.AddOrReplace(existing with { Indexes = updatedIndexes }, writeScope);
        }

        private void VisitDropTable(DropTableStatement dropTable, bool isFinalAttempt)
        {
            if (isFinalAttempt)
            {
                if (pendingDropsByNode is null || !pendingDropsByNode.TryGetValue(dropTable, out var pending))
                {
                    return;
                }

                foreach (var entry in pending)
                {
                    if (catalog.Find(entry.QualifiedName, entry.Scope) is null)
                    {
                        RecordUnresolvedTarget("DROP TABLE", entry.QualifiedName, dropTable);
                        continue;
                    }

                    catalog.Remove(entry.QualifiedName, entry.Scope);
                }

                return;
            }

            foreach (var target in dropTable.Objects)
            {
                var qualifiedName = SchemaObjectNameHelper.Qualify(target);
                if (catalog.Find(qualifiedName, _currentScope) is null)
                {
                    pendingDrops.Add(new PendingDrop(dropTable, qualifiedName, _currentScope, sourcePath));
                    continue;
                }

                catalog.Remove(qualifiedName, _currentScope);
            }
        }

        private void VisitDropIndex(DropIndexStatement dropIndex)
        {
            foreach (var clause in dropIndex.DropIndexClauses)
            {

                var (tableName, indexName) = clause switch
                {
                    DropIndexClause modern => (modern.Object, modern.Index.Value),
                    BackwardsCompatibleDropIndexClause legacy => (legacy.Index, legacy.Index.ChildIdentifier.Value),
                    _ => (null, null),
                };

                if (tableName is null || indexName is null)
                {
                    catalog.Skipped.Record(
                        AnalysisPass.Catalog, sourcePath, dropIndex.StartLine, dropIndex.StartColumn,
                        "DROP INDEX", $"clause of kind '{clause.GetType().Name}' is not modeled");
                    continue;
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(tableName);
                var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
                if (existing is null)
                {
                    RecordUnresolvedTarget("DROP INDEX", qualifiedName, dropIndex);
                    continue;
                }

                var remainingIndexes = existing.Indexes
                    .Where(i => !string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (remainingIndexes.Count == existing.Indexes.Count)
                {
                    catalog.Skipped.Record(
                        AnalysisPass.Catalog, sourcePath, dropIndex.StartLine, dropIndex.StartColumn,
                        "DROP INDEX", $"index '{indexName}' on '{qualifiedName}' not found in catalog");
                    continue;
                }

                catalog.AddOrReplace(existing with { Indexes = remainingIndexes }, writeScope);
            }
        }

        private void VisitPossibleSpRename(ExecuteStatement execute)
        {
            if (execute.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: { } procName,
                } procedureReference
                || !string.Equals(procName, SpRenameConstructKind, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryResolveSpRenameArguments(procedureReference.Parameters, out var objName, out var newName, out var objType))
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, execute.StartLine, execute.StartColumn,
                    SpRenameConstructKind, "objname/newname argument is not a literal string - rename not applied, catalog keeps the pre-rename definition");
                return;
            }

            if (objType is null || string.Equals(objType, "OBJECT", StringComparison.OrdinalIgnoreCase))
            {
                RenameTable(objName, newName, execute);
            }
            else if (string.Equals(objType, "COLUMN", StringComparison.OrdinalIgnoreCase))
            {
                RenameColumn(objName, newName, execute);
            }
            else if (string.Equals(objType, "INDEX", StringComparison.OrdinalIgnoreCase))
            {
                RenameIndex(objName, newName, execute);
            }
            else
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, execute.StartLine, execute.StartColumn,
                    SpRenameConstructKind, $"object type '{objType}' is not modeled");
            }
        }

        private static bool TryResolveSpRenameArguments(IList<ExecuteParameter> parameters, out string objName, out string newName, out string? objType)
        {
            objName = string.Empty;
            newName = string.Empty;
            objType = null;

            string? Resolve(int position, string parameterName) =>
                parameters
                    .Select((p, i) => (Param: p, Index: i))
                    .Where(x => string.Equals(x.Param.Variable?.Name, parameterName, StringComparison.OrdinalIgnoreCase)
                        || (x.Param.Variable is null && x.Index == position))
                    .Select(x => (x.Param.ParameterValue as StringLiteral)?.Value)
                    .FirstOrDefault(v => v is not null);

            if (Resolve(0, "@objname") is not { } resolvedObjName || Resolve(1, "@newname") is not { } resolvedNewName)
            {
                return false;
            }

            objName = resolvedObjName;
            newName = resolvedNewName;
            objType = Resolve(2, "@objtype");
            return true;
        }

        private void RenameTable(string objName, string newName, TSqlFragment node)
        {
            var (schema, oldQualifiedName) = SplitTableTarget(objName);
            if (schema is UnresolvableSchema)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    SpRenameConstructKind, $"'{objName}': three-part (database-qualified) rename target is not modeled");
                return;
            }

            var (existing, writeScope) = catalog.FindForMutation(oldQualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget(SpRenameConstructKind, oldQualifiedName, node);
                return;
            }

            catalog.Remove(oldQualifiedName, writeScope);
            catalog.AddOrReplace(existing with { SchemaName = schema, Name = newName }, writeScope);
        }

        private void RenameColumn(string objName, string newName, TSqlFragment node)
        {
            var (containerName, oldColumnName) = SplitLastSegment(objName);
            var (schema, tableQualifiedName) = SplitTableTarget(containerName);
            if (schema is UnresolvableSchema)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "sp_rename (COLUMN)", $"'{containerName}': three-part (database-qualified) rename target is not modeled");
                return;
            }

            var (existing, writeScope) = catalog.FindForMutation(tableQualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("sp_rename (COLUMN)", tableQualifiedName, node);
                return;
            }

            var updatedColumns = existing.Columns
                .Select(c => string.Equals(c.Name, oldColumnName, StringComparison.OrdinalIgnoreCase) ? c with { Name = newName } : c)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = updatedColumns }, writeScope);
        }

        private void RenameIndex(string objName, string newName, TSqlFragment node)
        {
            var (containerName, oldIndexName) = SplitLastSegment(objName);
            var (schema, tableQualifiedName) = SplitTableTarget(containerName);
            if (schema is UnresolvableSchema)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "sp_rename (INDEX)", $"'{containerName}': three-part (database-qualified) rename target is not modeled");
                return;
            }

            var (existing, writeScope) = catalog.FindForMutation(tableQualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("sp_rename (INDEX)", tableQualifiedName, node);
                return;
            }

            var updatedIndexes = existing.Indexes
                .Select(i => string.Equals(i.Name, oldIndexName, StringComparison.OrdinalIgnoreCase) ? i with { Name = newName } : i)
                .ToList();

            catalog.AddOrReplace(existing with { Indexes = updatedIndexes }, writeScope);
        }

        private const string UnresolvableSchema = "\0unresolvable";

        private static (string? Schema, string QualifiedName) SplitTableTarget(string name)
        {
            if (name.StartsWith('#'))
            {
                return (null, name);
            }

            var parts = name.Split('.');
            return parts.Length switch
            {
                1 => (SchemaObjectNameHelper.DefaultSchema, $"{SchemaObjectNameHelper.DefaultSchema}.{parts[0]}"),
                2 => (parts[0], name),
                _ => (UnresolvableSchema, name),
            };
        }

        private static (string Container, string Element) SplitLastSegment(string name)
        {
            var lastDot = name.LastIndexOf('.');
            return lastDot < 0 ? (name, name) : (name[..lastDot], name[(lastDot + 1)..]);
        }

        private void VisitCreateTable(CreateTableStatement createTable)
        {
            if (createTable.Definition is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createTable.StartLine, createTable.StartColumn,
                    "CREATE TABLE", $"'{SchemaObjectNameHelper.Qualify(createTable.SchemaObjectName)}': no inline column list (CTAS / AS CLONE OF form) - column shape not modeled");
                return;
            }

            var (schema, name) = SchemaObjectNameHelper.Resolve(createTable.SchemaObjectName);
            var isTemp = schema is null;
            var kind = isTemp ? CatalogTableKind.TemporaryTable : CatalogTableKind.Table;

            var (columns, indexesFromColumns) = BuildColumns(createTable.Definition, isTemp ? catalog.EffectiveTempdbCollation : catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath, catalog, ResolveDefaultNullable(createTable));
            var indexesFromConstraints = BuildIndexesFromTableConstraints(createTable.Definition.TableConstraints);
            var allIndexes = (List<CatalogIndex>)[.. indexesFromColumns, .. indexesFromConstraints];
            columns = ApplyPrimaryKeyNotNull(columns, allIndexes);

            var table = new CatalogTable(
                schema,
                name,
                kind,
                columns,
                allIndexes,
                sourcePath,
                createTable.StartLine,
                IsMemoryOptimized: IsMemoryOptimizedTable(createTable.Options));

            catalog.AddOrReplace(table, isTemp && SchemaObjectNameHelper.IsLocalTempName(name) ? _currentScope : null);

            if (!isTemp)
            {
                var qualifiedName = SchemaObjectNameHelper.Qualify(createTable.SchemaObjectName);
                foreach (var reference in SchemaExpressionCollector.Collect(createTable.Definition, qualifiedName, sourcePath))
                {
                    catalog.AddSchemaExpression(reference);
                }
            }
        }

        private void VisitAlterTableAdd(AlterTableAddTableElementStatement alterTable)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(alterTable.SchemaObjectName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE ADD", qualifiedName, alterTable);
                return;
            }

            var (newColumns, indexesFromColumns) = BuildColumns(alterTable.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath, catalog, defaultNullable: true);
            var newIndexes = BuildIndexesFromTableConstraints(alterTable.Definition.TableConstraints);
            var mergedColumns = (List<CatalogColumn>)[.. existing.Columns, .. newColumns];
            var mergedIndexes = (List<CatalogIndex>)[.. existing.Indexes, .. indexesFromColumns, .. newIndexes];

            catalog.AddOrReplace(
                existing with
                {
                    Columns = ApplyPrimaryKeyNotNull(mergedColumns, mergedIndexes),
                    Indexes = mergedIndexes,
                },
                writeScope);

            if (existing.Kind == CatalogTableKind.Table)
            {
                foreach (var reference in SchemaExpressionCollector.Collect(alterTable.Definition, qualifiedName, sourcePath))
                {
                    catalog.AddSchemaExpression(reference);
                }
            }
        }

        private void VisitAlterColumn(AlterTableAlterColumnStatement alterColumn)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(alterColumn.SchemaObjectName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE ALTER COLUMN", qualifiedName, alterColumn);
                return;
            }

            var columnName = alterColumn.ColumnIdentifier.Value;
            var existingColumn = existing.FindColumn(columnName, catalog.IdentifierComparer);
            if (existingColumn is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, alterColumn.StartLine, alterColumn.StartColumn,
                    "ALTER TABLE ALTER COLUMN", $"column '{columnName}' on '{qualifiedName}' not found in catalog");
                return;
            }

            var newType = SqlTypeReferenceResolver.Resolve(alterColumn.DataType, alterColumn.Collation, catalog.TypeAliases);
            if (newType is { IsStringFamily: true, Collation: null } && catalog.DefaultCollation is not null)
            {
                newType = newType with { Collation = catalog.DefaultCollation };
            }

            catalog.AddAlterColumnEvent(new CatalogAlterColumnEvent(
                qualifiedName, columnName, existingColumn.Type, newType, sourcePath, alterColumn.StartLine));

            var updatedColumns = existing.Columns
                .Select(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase) ? c with { Type = newType } : c)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = updatedColumns }, writeScope);
        }

        private void VisitDropTableElements(AlterTableDropTableElementStatement dropStatement)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(dropStatement.SchemaObjectName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE DROP", qualifiedName, dropStatement);
                return;
            }

            var droppedColumnNames = dropStatement.AlterTableDropTableElements
                .Where(e => e.TableElementType == TableElementType.Column)
                .Select(e => e.Name.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var droppedConstraintOrIndexNames = dropStatement.AlterTableDropTableElements
                .Where(e => e.TableElementType is TableElementType.Constraint or TableElementType.Index)
                .Select(e => e.Name.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (droppedColumnNames.Count == 0 && droppedConstraintOrIndexNames.Count == 0)
            {
                return;
            }

            var remainingColumns = droppedColumnNames.Count == 0
                ? existing.Columns
                : existing.Columns.Where(c => !droppedColumnNames.Contains(c.Name)).ToList();

            var remainingIndexes = droppedConstraintOrIndexNames.Count == 0
                ? existing.Indexes
                : existing.Indexes.Where(i => i.Name is null || !droppedConstraintOrIndexNames.Contains(i.Name)).ToList();

            catalog.AddOrReplace(existing with { Columns = remainingColumns, Indexes = remainingIndexes }, writeScope);
        }

        private void VisitCreateIndex(CreateIndexStatement createIndex)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createIndex.OnName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("CREATE INDEX", qualifiedName, createIndex);
                return;
            }

            var index = new CatalogIndex(
                createIndex.Name?.Value,
                CatalogIndexKind.Index,
                createIndex.Unique,
                [.. createIndex.Columns.Select(ColumnName)],
                [.. createIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
                IsFiltered: createIndex.FilterPredicate is not null);

            catalog.AddOrReplace(existing with { Indexes = [.. existing.Indexes, index] }, writeScope);
        }

        private void VisitCreateSelectiveXmlIndex(CreateSelectiveXmlIndexStatement node)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(node.OnName);

            if (node.IsSecondary)
            {
                if (node.Name is null || node.UsingXmlIndexName is null || node.PathName is null)
                {
                    return;
                }

                catalog.AddSecondarySelectiveXmlIndexReference(new CatalogSecondarySelectiveXmlIndexReference(
                    qualifiedName, node.Name.Value, node.UsingXmlIndexName.Value, node.PathName.Value,
                    sourcePath, node.StartLine));
                return;
            }

            if (node.Name is null)
            {
                return;
            }

            foreach (var promotedPath in node.PromotedPaths)
            {
                if (promotedPath.Name is null || promotedPath.SQLDataType is null)
                {
                    continue;
                }

                var type = SqlTypeReferenceResolver.Resolve(promotedPath.SQLDataType, columnCollation: null, catalog.TypeAliases);
                catalog.AddSelectiveXmlIndexPromotedPath(new CatalogSelectiveXmlIndexPromotedPath(
                    qualifiedName, node.Name.Value, promotedPath.Name.Value, type));
            }
        }

        private void VisitCreateColumnStoreIndex(CreateColumnStoreIndexStatement createIndex)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createIndex.OnName);
            var (existing, writeScope) = catalog.FindForMutation(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("CREATE COLUMNSTORE INDEX", qualifiedName, createIndex);
                return;
            }

            var index = new CatalogIndex(
                createIndex.Name?.Value,
                CatalogIndexKind.Index,
                IsUnique: false,
                [.. createIndex.Columns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
                IncludedColumns: [],
                IsColumnstore: true);

            catalog.AddOrReplace(existing with { Indexes = [.. existing.Indexes, index] }, writeScope);
        }

        private void VisitDeclareTableVariable(DeclareTableVariableStatement declareTableVar)
        {
            var body = declareTableVar.Body;
            if (body.Definition is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, declareTableVar.StartLine, declareTableVar.StartColumn,
                    "table variable", $"'{body.VariableName?.Value}' has no table definition to catalog");
                return;
            }

            var (columns, indexesFromColumns) = BuildColumns(body.Definition, catalog.EffectiveTempdbCollation, catalog.TypeAliases, catalog.Skipped, sourcePath, catalog, defaultNullable: true);
            var indexesFromConstraints = BuildIndexesFromTableConstraints(body.Definition.TableConstraints);

            var table = new CatalogTable(
                SchemaName: null,
                body.VariableName.Value,
                CatalogTableKind.TableVariable,
                columns,
                [.. indexesFromColumns, .. indexesFromConstraints],
                sourcePath,
                declareTableVar.StartLine);

            catalog.AddOrReplace(table, _currentScope);
        }

        private void VisitSelectInto(SelectStatement select)
        {
            var targetName = select.Into!;
            var (schema, name) = SchemaObjectNameHelper.Resolve(targetName);
            var isTemp = schema is null;

            var columns = SelectIntoColumnResolver.Resolve(select, catalog, _currentScope, sourcePath, catalog.Skipped);

            var table = new CatalogTable(
                schema,
                name,
                isTemp ? CatalogTableKind.TemporaryTable : CatalogTableKind.Table,
                columns,
                Indexes: [],
                sourcePath,
                select.StartLine);

            catalog.AddOrReplace(table, isTemp && SchemaObjectNameHelper.IsLocalTempName(name) ? _currentScope : null);
        }

        private void RecordUnresolvedTarget(string constructKind, string qualifiedName, TSqlFragment node) =>
            catalog.Skipped.Record(
                AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                constructKind, $"target table '{qualifiedName}' not found in catalog (cross-file/cross-database reference, or failed base CREATE TABLE)");

        private static List<CatalogIndex> BuildIndexesFromTableConstraints(IList<ConstraintDefinition> tableConstraints)
        {
            var indexes = new List<CatalogIndex>();

            foreach (var constraint in tableConstraints.OfType<UniqueConstraintDefinition>())
            {
                indexes.Add(new CatalogIndex(
                    constraint.ConstraintIdentifier?.Value,
                    constraint.IsPrimaryKey ? CatalogIndexKind.PrimaryKey : CatalogIndexKind.UniqueConstraint,
                    IsUnique: true,
                    [.. constraint.Columns.Select(ColumnName)],
                    IncludedColumns: []));
            }

            return indexes;
        }

        private static List<CatalogColumn> ApplyPrimaryKeyNotNull(List<CatalogColumn> columns, List<CatalogIndex> indexes)
        {
            var primaryKeyColumns = indexes
                .Where(i => i.Kind == CatalogIndexKind.PrimaryKey)
                .SelectMany(i => i.KeyColumns)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (primaryKeyColumns.Count == 0)
            {
                return columns;
            }

            return [.. columns.Select(c => primaryKeyColumns.Contains(c.Name) ? c with { IsNullable = false } : c)];
        }

        private static bool IsMemoryOptimizedTable(IList<TableOption> options) =>
            options.OfType<MemoryOptimizedTableOption>().Any(o => o.OptionState == OptionState.On);
    }

    public static IReadOnlyList<CatalogColumn> BuildColumnsForExternalUse(
        TableDefinition definition, Collation? defaultCollation, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null, string? sourcePath = null) =>
        BuildColumns(definition, defaultCollation, typeAliases, ledger, sourcePath, catalog: null, defaultNullable: true).Columns;

    private static (List<CatalogColumn> Columns, List<CatalogIndex> InlineIndexes) BuildColumns(
        TableDefinition definition, Collation? defaultCollation, IReadOnlyDictionary<string, SqlType>? typeAliases, SkipLedger? ledger, string? sourcePath,
        DatabaseCatalog? catalog, bool defaultNullable)
    {
        var columns = new List<CatalogColumn>();
        var inlineIndexes = new List<CatalogIndex>();
        var computedExpressions = new Dictionary<string, ScalarExpression>(StringComparer.OrdinalIgnoreCase);
        var computedColumnLines = new Dictionary<string, (int Line, int Column)>(StringComparer.OrdinalIgnoreCase);
        var context = new ColumnBuildContext(defaultCollation, typeAliases, ledger, sourcePath, catalog, defaultNullable);

        foreach (var columnDefinition in definition.ColumnDefinitions)
        {
            columns.Add(BuildColumn(columnDefinition, context, inlineIndexes, computedExpressions, computedColumnLines));
        }

        columns = ResolveComputedColumnTypes(columns, computedExpressions, computedColumnLines, context);

        inlineIndexes.AddRange(definition.Indexes.Select(i => BuildInlineIndex(i, columnName: string.Empty)));

        return (columns, inlineIndexes);
    }

    private readonly record struct ColumnBuildContext(
        Collation? DefaultCollation, IReadOnlyDictionary<string, SqlType>? TypeAliases, SkipLedger? Ledger, string? SourcePath,
        DatabaseCatalog? Catalog, bool DefaultNullable);

    private static CatalogColumn BuildColumn(
        ColumnDefinition columnDefinition, ColumnBuildContext context,
        List<CatalogIndex> inlineIndexes, Dictionary<string, ScalarExpression> computedExpressions, Dictionary<string, (int Line, int Column)> computedColumnLines)
    {
        var name = columnDefinition.ColumnIdentifier.Value;
        var isNullable = BuildColumnConstraints(columnDefinition, name, inlineIndexes, context.DefaultNullable);

        if (columnDefinition.Index is { } inlineIndex)
        {
            inlineIndexes.Add(BuildInlineIndex(inlineIndex, name));
        }

        var declaredType = columnDefinition.DataType;
        var resolvedType = declaredType is null ? null : SqlTypeReferenceResolver.Resolve(declaredType, columnDefinition.Collation, context.TypeAliases);
        if (resolvedType is null && context.SourcePath is not null && declaredType is not null)
        {

            context.Ledger?.Record(
                AnalysisPass.Catalog, context.SourcePath, columnDefinition.StartLine, columnDefinition.StartColumn,
                "column type", $"column '{name}' has type '{SchemaObjectNameHelper.Qualify(declaredType.Name)}' which could not be resolved");
        }

        if (resolvedType is { IsStringFamily: true, Collation: null } && context.DefaultCollation is not null)
        {

            resolvedType = resolvedType with { Collation = context.DefaultCollation };
        }

        if (columnDefinition.ComputedColumnExpression is { } computedExpression)
        {
            computedExpressions[name] = computedExpression;
            computedColumnLines[name] = (columnDefinition.StartLine, columnDefinition.StartColumn);
        }

        return new CatalogColumn(
            name,
            resolvedType,
            isNullable,
            IsIdentity: columnDefinition.IdentityOptions is not null,
            IsComputed: columnDefinition.ComputedColumnExpression is not null,
            IsPersisted: columnDefinition.IsPersisted,
            EncryptionType: ResolveEncryptionType(columnDefinition.Encryption),
            EnclaveSupport: ResolveEnclaveSupport(columnDefinition.Encryption, context.Catalog));
    }

    private static ColumnEncryptionEnclaveSupport ResolveEnclaveSupport(ColumnEncryptionDefinition? encryption, DatabaseCatalog? catalog) =>
        encryption?.Parameters.OfType<ColumnEncryptionKeyNameParameter>().FirstOrDefault() is { Name: { Value: { } keyName } } && catalog is not null
            ? catalog.ResolveColumnEncryptionKeyEnclaveSupport(keyName)
            : ColumnEncryptionEnclaveSupport.Unknown;

    private static ColumnEncryptionType ResolveEncryptionType(ColumnEncryptionDefinition? encryption) =>
        encryption?.Parameters.OfType<ColumnEncryptionTypeParameter>().FirstOrDefault() is { } typeParameter
            ? typeParameter.EncryptionType switch
            {
                Microsoft.SqlServer.TransactSql.ScriptDom.ColumnEncryptionType.Deterministic => ColumnEncryptionType.Deterministic,
                Microsoft.SqlServer.TransactSql.ScriptDom.ColumnEncryptionType.Randomized => ColumnEncryptionType.Randomized,
                _ => ColumnEncryptionType.None,
            }
            : ColumnEncryptionType.None;

    private static List<CatalogColumn> ResolveComputedColumnTypes(
        List<CatalogColumn> columns, Dictionary<string, ScalarExpression> computedExpressions, Dictionary<string, (int Line, int Column)> computedColumnLines, ColumnBuildContext context)
    {
        columns = ComputedColumnTypeResolver.ResolveAll(
            columns, computedExpressions, context.TypeAliases, Collation.IdentifierComparer(context.DefaultCollation));
        columns = [.. columns.Select(c => c.Type is { IsStringFamily: true, Collation: null } && context.DefaultCollation is not null
            ? c with { Type = c.Type with { Collation = context.DefaultCollation } }
            : c)];

        if (context.SourcePath is null)
        {
            return columns;
        }

        foreach (var name in computedExpressions.Keys)
        {
            if (columns.Single(c => c.Name == name).Type is not null)
            {
                continue;
            }

            var (line, column) = computedColumnLines[name];
            context.Ledger?.Record(
                AnalysisPass.Catalog, context.SourcePath, line, column,
                "computed column type", $"column '{name}' is computed but its expression type could not be inferred");
        }

        return columns;
    }

    private static bool BuildColumnConstraints(ColumnDefinition columnDefinition, string columnName, List<CatalogIndex> inlineIndexes, bool defaultNullable)
    {
        var isNullable = columnDefinition.ComputedColumnExpression is not null || defaultNullable;

        foreach (var constraint in columnDefinition.Constraints)
        {
            switch (constraint)
            {
                case NullableConstraintDefinition nullable:
                    isNullable = nullable.Nullable;
                    break;
                case UniqueConstraintDefinition unique:
                    inlineIndexes.Add(new CatalogIndex(
                        unique.ConstraintIdentifier?.Value,
                        unique.IsPrimaryKey ? CatalogIndexKind.PrimaryKey : CatalogIndexKind.UniqueConstraint,
                        IsUnique: true,
                        unique.Columns.Count > 0 ? [.. unique.Columns.Select(ColumnName)] : [columnName],
                        IncludedColumns: []));

                    if (unique.IsPrimaryKey)
                    {
                        isNullable = false;
                    }

                    break;
            }
        }

        return isNullable;
    }

    private static bool IsColumnstoreIndexType(IndexType? indexType) =>
        indexType?.IndexTypeKind is IndexTypeKind.ClusteredColumnStore or IndexTypeKind.NonClusteredColumnStore;

    private static CatalogIndex BuildInlineIndex(IndexDefinition inlineIndex, string columnName) => new(
        inlineIndex.Name?.Value,
        CatalogIndexKind.Index,
        inlineIndex.Unique,
        inlineIndex.Columns.Count > 0 ? [.. inlineIndex.Columns.Select(ColumnName)] : [columnName],
        [.. inlineIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
        IsFiltered: inlineIndex.FilterPredicate is not null,
        IsColumnstore: IsColumnstoreIndexType(inlineIndex.IndexType));

    private static string ColumnName(ColumnWithSortOrder columnWithSortOrder) =>
        columnWithSortOrder.Column.MultiPartIdentifier.Identifiers[^1].Value;
}
