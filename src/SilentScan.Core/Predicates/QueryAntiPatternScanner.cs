using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.Common;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class QueryAntiPatternScanner
{
    private static readonly HashSet<string> CountStarFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "COUNT_BIG",
    };

    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "APPROX_COUNT_DISTINCT",
        "CHECKSUM_AGG", "GROUPING", "GROUPING_ID", "STDEV", "STDEVP", "STRING_AGG", "VAR", "VARP",
    };

    public static IReadOnlyList<QueryAntiPatternFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var cteNameCollector = new Rule.CteNameCollector(catalog.IdentifierComparer);
        parseResult.Fragment.Accept(cteNameCollector);
        return new Rule(parseResult.SourcePath, catalog, cteNameCollector.Names);
    }

    internal static IReadOnlyList<QueryAntiPatternFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly HashSet<string> SystemDatabaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "tempdb", "msdb", "model",
    };

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog, HashSet<string> cteNames) : IModuleRule
    {
        public List<QueryAntiPatternFinding> Findings { get; } = [];

        private readonly HashSet<string> _tableVariableNames = new(StringComparer.OrdinalIgnoreCase);

        private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);

        private static string? AliasOf(TableReference reference) =>
            reference is NamedTableReference named ? named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value : null;

        private readonly HashSet<BinaryQueryExpression> _consumedUnionChainNodes = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            InspectTableValuedParameters(node.Parameters);

        public void OnEnterDeclareTableVariableStatement(DeclareTableVariableStatement node, ModuleWalker walker) =>
            _tableVariableNames.Add(node.Body.VariableName.Value);

        public void OnEnterFromClause(FromClause node, ModuleWalker walker)
        {
            foreach (var tableReference in node.TableReferences)
            {
                foreach (var variableRef in CollectVariableTableReferences(tableReference))
                {
                    if (!_tableVariableNames.Contains(variableRef.Variable.Name))
                    {
                        continue;
                    }

                    if (catalog.CompatibilityLevel is { } level && level < 150)
                    {
                        Findings.Add(new QueryAntiPatternFinding(
                            QueryAntiPatternFindingKind.TableVariableLowCompatEstimate, sourcePath,
                            variableRef.StartLine, variableRef.StartColumn,
                            $"{variableRef.Variable.Name} (connected compatibility level {level}, below 150)",
                            FindingConfidence.High));
                    }
                }

                foreach (var named in CollectNamedTableReferences(tableReference))
                {
                    InspectUnqualifiedReference(named);
                    InspectLinkedServerOrCrossDatabase(named);
                }
            }
        }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            InspectSiteIfNamedTable(node.InsertSpecification.Target);
            InspectMultiRowInsertIgnoreDupKey(node);
        }

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            InspectSiteIfNamedTable(node.UpdateSpecification.Target);
            InspectUnboundedWrite(node.UpdateSpecification.WhereClause, node.UpdateSpecification.TopRowFilter, node);
        }

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            InspectSiteIfNamedTable(node.DeleteSpecification.Target);
            InspectUnboundedWrite(node.DeleteSpecification.WhereClause, node.DeleteSpecification.TopRowFilter, node);
        }

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            InspectSiteIfNamedTable(node.MergeSpecification.Target);
            InspectSiteIfNamedTable(node.MergeSpecification.TableReference);
            InspectMergeHazards(node.MergeSpecification, walker);
        }

        public void OnEnterAlterTableSwitchStatement(AlterTableSwitchStatement node, ModuleWalker walker)
        {
            InspectAlterTableSwitchColumnMismatch(node);
            InspectAlterTableSwitchIndexMismatch(node);
            InspectAlterTableSwitchConstraintMismatch(node);
            InspectAlterTableSwitchTargetOnlyIndexRestriction(node);
            InspectAlterTableSwitchFilegroupMismatch(node);
            InspectAlterTableSwitchTemporalMismatch(node);
            InspectAlterTableSwitchRuleConstraint(node);
            InspectAlterTableSwitchCdcPartitionSwitch(node);
            InspectAlterTableSwitchPartitionFilegroupMismatch(node);
            InspectAlterTableSwitchFullTextIndexRestriction(node);
        }

        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker) =>
            InspectRecursiveCteMaxRecursion(node);

        public void OnEnterWhileStatement(WhileStatement node, ModuleWalker walker)
        {
            if (catalog.CompatibilityLevel is not { } knownLevel || knownLevel >= 150)
            {
                InspectStaleTableVariableInLoop(node);
            }

            InspectRbarSingleRowLoopDml(node);
        }

        public void OnEnterDeclareCursorStatement(DeclareCursorStatement node, ModuleWalker walker) =>
            InspectCursorGlobalness(node.CursorDefinition, node.Name.Value, node.StartLine, node.StartColumn);

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.CursorDefinition is not null)
            {
                InspectCursorGlobalness(node.CursorDefinition, node.Variable.Name, node.StartLine, node.StartColumn);
            }
        }

        public void OnEnterStatementList(StatementList node, ModuleWalker walker) =>
            InspectCountStarExistenceSequence(node.Statements);

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker)
        {
            InspectCountStarExistenceSequence(node.Statements);
            _tableVariableNames.Clear();
        }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var byAlias = scopeChain[0].ByAlias;

            InspectHaving(node, scopeChain, walker);
            InspectDistinctJoinFanout(node, byAlias, scopeChain, walker);
            InspectGroupBySetsCardinality(node.GroupByClause);
        }

        public void OnEnterBinaryQueryExpression(BinaryQueryExpression node, ModuleWalker walker)
        {
            if (node.BinaryQueryExpressionType == BinaryQueryExpressionType.Union && !node.All
                && !_consumedUnionChainNodes.Contains(node))
            {

                MarkNestedUnionChain(node.FirstQueryExpression, _consumedUnionChainNodes);
                MarkNestedUnionChain(node.SecondQueryExpression, _consumedUnionChainNodes);
                InspectUnionDisjointness(node);
            }
        }

        private void InspectTableValuedParameters(IList<ProcedureParameter> parameters)
        {
            if (catalog.CompatibilityLevel is not >= 170)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                if (parameter.DataType is not UserDataTypeReference userType
                    || catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is not { Kind: CatalogTableKind.TableType })
                {
                    continue;
                }

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.TableVariablePspSkip, sourcePath,
                    parameter.StartLine, parameter.StartColumn, parameter.VariableName.Value,
                    FindingConfidence.High));
            }
        }

        internal sealed class CteNameCollector(StringComparer identifierComparer) : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(identifierComparer);

            public override void ExplicitVisit(CommonTableExpression node)
            {
                Names.Add(node.ExpressionName.Value);
                base.ExplicitVisit(node);
            }
        }

        private void InspectSiteIfNamedTable(TableReference? tableReference)
        {
            if (tableReference is NamedTableReference named)
            {
                InspectUnqualifiedReference(named);
                InspectLinkedServerOrCrossDatabase(named);
            }
        }

        private void InspectUnqualifiedReference(NamedTableReference named)
        {
            if (named.SchemaObject.SchemaIdentifier is not null
                || named.SchemaObject.BaseIdentifier.Value.StartsWith('#')
                || cteNames.Contains(named.SchemaObject.BaseIdentifier.Value))
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            var resolved = catalog.Find(qualifiedName);
            if (resolved is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.UnqualifiedTableReference, sourcePath,
                named.StartLine, named.StartColumn,
                $"'{named.SchemaObject.BaseIdentifier.Value}' resolves to '{qualifiedName}' with no explicit schema qualifier at this reference.",
                FindingConfidence.Medium));
        }

        private void InspectLinkedServerOrCrossDatabase(NamedTableReference named)
        {
            var schemaObject = named.SchemaObject;
            if (schemaObject.ServerIdentifier is { Value.Length: > 0 } server)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference, sourcePath,
                    named.StartLine, named.StartColumn,
                    $"'{server.Value}.{schemaObject.DatabaseIdentifier?.Value}.{schemaObject.SchemaIdentifier?.Value}.{schemaObject.BaseIdentifier.Value}' names a remote linked server - remote statistics are usually unavailable to the optimizer.",
                    FindingConfidence.High));
                return;
            }

            if (schemaObject.DatabaseIdentifier is not { Value.Length: > 0 } database)
            {
                return;
            }

            if (SystemDatabaseNames.Contains(database.Value))
            {

                return;
            }

            if (catalog.CurrentDatabaseName is not { Length: > 0 } currentDatabase
                || string.Equals(database.Value, currentDatabase, StringComparison.OrdinalIgnoreCase))
            {

                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference, sourcePath,
                named.StartLine, named.StartColumn,
                $"'{database.Value}.{schemaObject.SchemaIdentifier?.Value}.{schemaObject.BaseIdentifier.Value}' references a different database than the one this scan connected to ('{currentDatabase}').",
                FindingConfidence.Medium));
        }

        private void InspectMultiRowInsertIgnoreDupKey(InsertStatement node)
        {
            if (node.InsertSpecification.InsertSource is not ValuesInsertSource { RowValues.Count: > 1 }
                || node.InsertSpecification.Target is not NamedTableReference named)
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            var resolved = catalog.Find(qualifiedName);
            if (resolved is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var hazardIndex = resolved.Indexes.FirstOrDefault(ix => ix.IsUnique && ix.IgnoreDupKey);
            if (hazardIndex is null)
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop, sourcePath,
                node.StartLine, node.StartColumn,
                $"Multi-row INSERT into '{qualifiedName}' - unique index '{hazardIndex.Name}' has IGNORE_DUP_KEY=ON, so a row whose key duplicates an existing (or an earlier row in this same batch's) value is silently skipped instead of raising an error.",
                FindingConfidence.High));
        }

        private void InspectAlterTableSwitchColumnMismatch(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            if (source.Columns.Count != target.Columns.Count)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' ({source.Columns.Count} columns) to '{targetQualifiedName}' ({target.Columns.Count} columns) - SQL Server requires both tables to have the same number of columns (error 4943); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }

            for (var i = 0; i < source.Columns.Count; i++)
            {
                var sourceColumn = source.Columns[i];
                var targetColumn = target.Columns[i];

                if (!catalog.IdentifierComparer.Equals(sourceColumn.Name, targetColumn.Name))
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - column '{sourceColumn.Name}' at ordinal {i + 1} in the source table has a different name than column '{targetColumn.Name}' at the same ordinal in the target table (error 4942); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (sourceColumn.IsComputed != targetColumn.IsComputed)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - column '{sourceColumn.Name}' is computed in one table but not the other (error 4965); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (sourceColumn.Type is not null && targetColumn.Type is not null && !HasSameShape(sourceColumn.Type, targetColumn.Type))
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - column '{sourceColumn.Name}' has type {sourceColumn.Type} in the source table which is different from its type {targetColumn.Type} in the target table (error 4944); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (sourceColumn.IsNullable != targetColumn.IsNullable)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - column '{sourceColumn.Name}' does not have the same nullability in both tables (error 4985); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (HasCollationMismatch(sourceColumn.Type, targetColumn.Type))
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - column '{sourceColumn.Name}' has collation {sourceColumn.Type!.Collation!.Name} in the source table which is different from its collation {targetColumn.Type!.Collation!.Name} in the target table (error 4945); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }
            }
        }

        private static bool HasSameShape(SqlType a, SqlType b) =>
            a.Category == b.Category && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale && a.IsMax == b.IsMax;

        private static bool HasCollationMismatch(SqlType? a, SqlType? b) =>
            a is { IsStringFamily: true, Collation: { } collationA } && b is { IsStringFamily: true, Collation: { } collationB }
            && !string.Equals(collationA.Name, collationB.Name, StringComparison.OrdinalIgnoreCase);

        private void InspectAlterTableSwitchIndexMismatch(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var sourceHasClustered = source.Indexes.Any(ix => ix.IsClustered && !ix.IsColumnstore);
            var targetHasClustered = target.Indexes.Any(ix => ix.IsClustered && !ix.IsColumnstore);
            if (sourceHasClustered != targetHasClustered)
            {
                var (withClusteredName, withoutClusteredName) = sourceHasClustered
                    ? (sourceQualifiedName, targetQualifiedName)
                    : (targetQualifiedName, sourceQualifiedName);

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - table '{withClusteredName}' has a clustered index while table '{withoutClusteredName}' does not (error 4913); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }

            foreach (var targetIndex in target.Indexes.Where(IsComparableSwitchIndex))
            {
                if (source.Indexes.Where(IsComparableSwitchIndex).Any(sourceIndex => HasSameIndexShape(sourceIndex, targetIndex)))
                {
                    continue;
                }

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - there is no identical index in the source table for index '{targetIndex.Name}' in the target table (error 4947); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }
        }

        private static bool IsComparableSwitchIndex(CatalogIndex index) =>
            !index.IsFiltered && !index.IsColumnstore && !index.IsDisabled && !index.IsHypothetical;

        private bool HasSameIndexShape(CatalogIndex sourceIndex, CatalogIndex targetIndex)
        {
            if (sourceIndex.IsUnique != targetIndex.IsUnique
                || !sourceIndex.KeyColumns.SequenceEqual(targetIndex.KeyColumns, catalog.IdentifierComparer))
            {
                return false;
            }

            if (sourceIndex.KeyColumnIsDescending.Count > 0 && targetIndex.KeyColumnIsDescending.Count > 0
                && !sourceIndex.KeyColumnIsDescending.SequenceEqual(targetIndex.KeyColumnIsDescending))
            {
                return false;
            }

            var sourceIncluded = new HashSet<string>(sourceIndex.IncludedColumns, catalog.IdentifierComparer);
            var targetIncluded = new HashSet<string>(targetIndex.IncludedColumns, catalog.IdentifierComparer);
            return sourceIncluded.SetEquals(targetIncluded);
        }

        private void InspectAlterTableSwitchConstraintMismatch(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var sourceChecks = catalog.CheckConstraints.Where(c => catalog.IdentifierComparer.Equals(c.TableQualifiedName, sourceQualifiedName)).ToList();
            var targetChecks = catalog.CheckConstraints.Where(c => catalog.IdentifierComparer.Equals(c.TableQualifiedName, targetQualifiedName)).ToList();

            foreach (var targetCheck in targetChecks)
            {
                var matchingSource = sourceChecks.FirstOrDefault(c => string.Equals(c.DefinitionText, targetCheck.DefinitionText, StringComparison.Ordinal));
                if (matchingSource is null)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - target table has check constraint '{targetCheck.ConstraintName}' with no corresponding constraint in the source table (error 4970/4971); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (matchingSource.IsDisabled != targetCheck.IsDisabled)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - check constraint '{matchingSource.ConstraintName}' in the source table and matching constraint '{targetCheck.ConstraintName}' in the target table disagree on NOCHECK/CHECK state (error 4960); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }
            }

            var sourceForeignKeys = GroupForeignKeys(catalog.ForeignKeys, sourceQualifiedName, catalog.IdentifierComparer);
            var targetForeignKeys = GroupForeignKeys(catalog.ForeignKeys, targetQualifiedName, catalog.IdentifierComparer);

            foreach (var targetFk in targetForeignKeys)
            {
                var matchingSource = sourceForeignKeys.FirstOrDefault(fk => HasSameForeignKeyShape(fk, targetFk));
                if (matchingSource is null)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - target table has foreign key constraint '{targetFk.ConstraintName}' with no corresponding key in the source table (error 4968); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (matchingSource.IsDisabled != targetFk.IsDisabled)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - foreign key constraint '{matchingSource.ConstraintName}' in the source table and matching constraint '{targetFk.ConstraintName}' in the target table disagree on enabled/disabled state (error 4969); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }

                if (matchingSource.IsNotTrusted != targetFk.IsNotTrusted)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch, sourcePath,
                        node.StartLine, node.StartColumn,
                        $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - foreign key constraint '{matchingSource.ConstraintName}' in the source table and matching constraint '{targetFk.ConstraintName}' in the target table disagree on NOCHECK/CHECK state (error 4974); this statement will fail at execution.",
                        FindingConfidence.High));
                    return;
                }
            }
        }

        private sealed record GroupedForeignKey(
            string ConstraintName, string ReferencedTableQualifiedName,
            IReadOnlySet<(string Parent, string Referenced)> ColumnPairs, bool IsDisabled, bool IsNotTrusted);

        private static List<GroupedForeignKey> GroupForeignKeys(
            IReadOnlyList<ForeignKeyRelationship> foreignKeys, string tableQualifiedName, StringComparer identifierComparer) =>
            foreignKeys
                .Where(fk => identifierComparer.Equals(fk.ParentTableQualifiedName, tableQualifiedName))
                .GroupBy(fk => fk.ConstraintName, identifierComparer)
                .Select(g => new GroupedForeignKey(
                    g.Key,
                    g.First().ReferencedTableQualifiedName,
                    g.Select(fk => (fk.ParentColumnName, fk.ReferencedColumnName)).ToHashSet(new ColumnPairComparer(identifierComparer)),
                    g.First().IsDisabled,
                    g.First().IsNotTrusted))
                .ToList();

        private sealed class ColumnPairComparer(StringComparer identifierComparer) : IEqualityComparer<(string Parent, string Referenced)>
        {
            public bool Equals((string Parent, string Referenced) x, (string Parent, string Referenced) y) =>
                identifierComparer.Equals(x.Parent, y.Parent) && identifierComparer.Equals(x.Referenced, y.Referenced);

            public int GetHashCode((string Parent, string Referenced) obj) =>
                HashCode.Combine(identifierComparer.GetHashCode(obj.Parent), identifierComparer.GetHashCode(obj.Referenced));
        }

        private bool HasSameForeignKeyShape(GroupedForeignKey a, GroupedForeignKey b) =>
            catalog.IdentifierComparer.Equals(a.ReferencedTableQualifiedName, b.ReferencedTableQualifiedName)
            && a.ColumnPairs.SetEquals(b.ColumnPairs);

        private void InspectAlterTableSwitchTargetOnlyIndexRestriction(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var target = catalog.Find(targetQualifiedName);
            if (catalog.Find(sourceQualifiedName) is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var offendingIndex = target.Indexes.FirstOrDefault(ix => ix.IsXmlIndex || ix.IsSpatialIndex);
            if (offendingIndex is null)
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction, sourcePath,
                node.StartLine, node.StartColumn,
                $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - target table has an XML or spatial index '{offendingIndex.Name}' on it; only the source table is allowed to carry one (error 4983); this statement will fail at execution.",
                FindingConfidence.High));
        }

        private void InspectAlterTableSwitchFilegroupMismatch(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            if (source.FilegroupName is null || target.FilegroupName is null)
            {

                return;
            }

            if (source.FilegroupIsReadOnly)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - source table resides in read-only filegroup '{source.FilegroupName}' (error 4979); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }

            if (target.FilegroupIsReadOnly)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - target table resides in read-only filegroup '{target.FilegroupName}' (error 4979); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }

            if (!catalog.IdentifierComparer.Equals(source.FilegroupName, target.FilegroupName))
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - source table is in filegroup '{source.FilegroupName}' and target table is in filegroup '{target.FilegroupName}' (error 4940); this statement will fail at execution.",
                    FindingConfidence.High));
            }
        }

        private void InspectAlterTableSwitchRuleConstraint(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            if (!source.HasRuleConstraint && !target.HasRuleConstraint)
            {
                return;
            }

            var offendingName = source.HasRuleConstraint ? sourceQualifiedName : targetQualifiedName;
            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint, sourcePath,
                node.StartLine, node.StartColumn,
                $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - table '{offendingName}' has a legacy RULE constraint bound to one of its columns; SWITCH is not allowed on tables with RULE constraints (error 4964); this statement will fail at execution.",
                FindingConfidence.High));
        }

        private (string SourceQualifiedName, string TargetQualifiedName, CatalogTable Source, CatalogTable Target)? ResolveSwitchTables(
            AlterTableSwitchStatement node)
        {
            if (node.SourcePartitionNumber is null && node.TargetPartitionNumber is null)
            {
                return null;
            }

            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return null;
            }

            return (sourceQualifiedName, targetQualifiedName, source, target);
        }

        private void InspectAlterTableSwitchCdcPartitionSwitch(AlterTableSwitchStatement node)
        {
            if (ResolveSwitchTables(node) is not { } tables)
            {
                return;
            }

            var (sourceQualifiedName, targetQualifiedName, source, target) = tables;

            if (target.CdcPartitionSwitchDisallowed)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - target table is enabled for Change Data Capture with @allow_partition_switch explicitly set to 0 (error 22842); this statement will fail at execution.",
                    FindingConfidence.High));
                return;
            }

            if (source.CdcPartitionSwitchDisallowed)
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - source table is enabled for Change Data Capture with @allow_partition_switch explicitly set to 0 (error 22843); this statement will fail at execution.",
                    FindingConfidence.High));
            }
        }

        private void InspectAlterTableSwitchPartitionFilegroupMismatch(AlterTableSwitchStatement node)
        {
            if (ResolveSwitchTables(node) is not { } tables)
            {
                return;
            }

            var (sourceQualifiedName, targetQualifiedName, source, target) = tables;

            var sourceFilegroup = ResolveSwitchSideFilegroup(source, node.SourcePartitionNumber);
            var targetFilegroup = ResolveSwitchSideFilegroup(target, node.TargetPartitionNumber);
            if (sourceFilegroup is null || targetFilegroup is null
                || catalog.IdentifierComparer.Equals(sourceFilegroup, targetFilegroup))
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch, sourcePath,
                node.StartLine, node.StartColumn,
                $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - the source side resolves to filegroup '{sourceFilegroup}' and the target side resolves to filegroup '{targetFilegroup}' (error 4938/4939); this statement will fail at execution.",
                FindingConfidence.High));
        }

        private string? ResolveSwitchSideFilegroup(CatalogTable table, ScalarExpression? partitionNumberExpression)
        {
            if (partitionNumberExpression is null)
            {

                return table.FilegroupName;
            }

            if (ResolveIntegerLiteral(partitionNumberExpression) is not { } partitionNumber || table.PartitionSchemeName is null)
            {
                return null;
            }

            return catalog.FindPartitionFilegroup(table.PartitionSchemeName, partitionNumber);
        }

        private static int? ResolveIntegerLiteral(ScalarExpression expression) =>
            expression is IntegerLiteral { Value: { } text } && int.TryParse(text, out var value) ? value : null;

        private void InspectAlterTableSwitchTemporalMismatch(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            if (catalog.Find(sourceQualifiedName) is not { Kind: CatalogTableKind.Table } || catalog.Find(targetQualifiedName) is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var sourceIsTemporal = catalog.TemporalTablePairs.Any(p => catalog.IdentifierComparer.Equals(p.CurrentTableQualifiedName, sourceQualifiedName));
            var targetIsTemporal = catalog.TemporalTablePairs.Any(p => catalog.IdentifierComparer.Equals(p.CurrentTableQualifiedName, targetQualifiedName));
            if (sourceIsTemporal == targetIsTemporal)
            {
                return;
            }

            var (withPeriodName, withoutPeriodName) = targetIsTemporal
                ? (targetQualifiedName, sourceQualifiedName)
                : (sourceQualifiedName, targetQualifiedName);

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch, sourcePath,
                node.StartLine, node.StartColumn,
                $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - table '{withPeriodName}' has a SYSTEM_TIME PERIOD (system-versioned) while table '{withoutPeriodName}' does not (error 13577); this statement will fail at execution.",
                FindingConfidence.High));
        }

        private void InspectAlterTableSwitchFullTextIndexRestriction(AlterTableSwitchStatement node)
        {
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));
            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(node.TargetTable));
            var source = catalog.Find(sourceQualifiedName);
            var target = catalog.Find(targetQualifiedName);
            if (source is not { Kind: CatalogTableKind.Table } || target is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            string? offendingName;
            if (source.HasFullTextIndex)
            {
                offendingName = sourceQualifiedName;
            }
            else if (target.HasFullTextIndex)
            {
                offendingName = targetQualifiedName;
            }
            else
            {
                offendingName = null;
            }

            if (offendingName is null)
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction, sourcePath,
                node.StartLine, node.StartColumn,
                $"ALTER TABLE SWITCH from '{sourceQualifiedName}' to '{targetQualifiedName}' - table '{offendingName}' has a full-text index on it (error 4918); this statement will fail at execution.",
                FindingConfidence.High));
        }

        private static IEnumerable<NamedTableReference> CollectNamedTableReferences(TableReference tableReference)
        {
            switch (tableReference)
            {
                case NamedTableReference named:
                    yield return named;
                    break;

                case QualifiedJoin join:
                    foreach (var t in CollectNamedTableReferences(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in CollectNamedTableReferences(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in CollectNamedTableReferences(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
            }
        }

        private void InspectUnboundedWrite(WhereClause? where, TopRowFilter? top, TSqlStatement node)
        {
            if (where is not null || top is not null)
            {
                return;
            }

            var verb = node is UpdateStatement ? "UPDATE" : "DELETE";
            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.UnboundedTableWrite, sourcePath,
                node.StartLine, node.StartColumn,
                $"{verb} with no WHERE clause and no TOP - a whole-table write with no row-limiting mechanism at all. A deliberate full-table maintenance statement is a legitimate reason this fires; verify intent before treating this as a bug.",
                FindingConfidence.Medium));
        }

        private void InspectMergeHazards(MergeSpecification spec, ModuleWalker walker)
        {
            InspectMergeMissingHoldlock(spec);
            InspectMergeNonUniqueUsingSource(spec, walker);
            InspectMergeUnconditionalDelete(spec);
        }

        private void InspectMergeMissingHoldlock(MergeSpecification spec)
        {
            if (spec.Target is not NamedTableReference targetRef)
            {
                return;
            }

            var hintKinds = targetRef.TableHints.Select(h => h.HintKind).ToHashSet();
            if (hintKinds.Contains(TableHintKind.HoldLock) || hintKinds.Contains(TableHintKind.Serializable))
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.MergeMissingHoldlock, sourcePath,
                spec.StartLine, spec.StartColumn,
                "MERGE target carries no WITH (HOLDLOCK)/SERIALIZABLE hint - two concurrent sessions can both take the WHEN NOT MATCHED branch under READ COMMITTED and race a primary-key violation.",
                FindingConfidence.Medium));
        }

        private void InspectMergeNonUniqueUsingSource(MergeSpecification spec, ModuleWalker walker)
        {
            if (spec.TableReference is not NamedTableReference sourceRef)
            {
                return;
            }

            var sourceAlias = sourceRef.Alias?.Value ?? sourceRef.SchemaObject.BaseIdentifier.Value;
            var sourceQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(sourceRef.SchemaObject));
            var sourceTable = catalog.Find(sourceQualifiedName);
            if (sourceTable is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            var joinColumns = JoinKeyUniqueness.EqualityColumnsQualifiedBy(spec.SearchCondition, sourceAlias, catalog.IdentifierComparer);
            if (joinColumns.Count == 0)
            {
                return;
            }

            if (JoinKeyUniqueness.IsProvenUniqueOver(sourceTable, joinColumns, catalog.IdentifierComparer))
            {
                return;
            }

            var (mergeByAlias, mergeOrdered) = FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, ResolutionContext(walker.CurrentCteRelations()));
            var mergeScopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
            {
                (mergeByAlias, mergeOrdered),
            };

            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(spec.SearchCondition, columnRef => walker.ResolveColumnFacts(columnRef, mergeScopeChain)))
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.MergeNonUniqueUsingSource, sourcePath,
                spec.StartLine, spec.StartColumn,
                $"MERGE USING source '{sourceQualifiedName}' is not backed by a unique index covering its own ON-clause join columns ({string.Join(", ", joinColumns)}) - a future duplicate join-key value hard-errors the statement.",
                FindingConfidence.High));
        }

        private void InspectMergeUnconditionalDelete(MergeSpecification spec)
        {
            foreach (var clause in spec.ActionClauses)
            {
                if (clause.Action is not DeleteMergeAction || clause.SearchCondition is not null)
                {
                    continue;
                }

                var matchKindText = clause.Condition switch
                {
                    MergeCondition.NotMatchedBySource => "WHEN NOT MATCHED BY SOURCE THEN DELETE",
                    MergeCondition.Matched => "WHEN MATCHED THEN DELETE",
                    _ => "THEN DELETE",
                };

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.MergeUnconditionalDelete, sourcePath,
                    clause.StartLine, clause.StartColumn,
                    $"{matchKindText} with no additional AND condition of its own - {(clause.Condition == MergeCondition.NotMatchedBySource ? "deletes every target row absent from the USING source's result set" : "deletes every row the join matched")}.",
                    FindingConfidence.Medium));
            }
        }

        private void InspectRecursiveCteMaxRecursion(SelectStatement node)
        {
            if (node.WithCtesAndXmlNamespaces is not { CommonTableExpressions: { Count: > 0 } ctes })
            {
                return;
            }

            var hasMaxRecursion = node.OptimizerHints.Any(h => h.HintKind == OptimizerHintKind.MaxRecursion);
            if (hasMaxRecursion)
            {
                return;
            }

            foreach (var cte in ctes)
            {
                if (!CteResolver.ReferencesSelf(cte.QueryExpression, cte.ExpressionName.Value))
                {
                    continue;
                }

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion, sourcePath,
                    cte.StartLine, cte.StartColumn,
                    $"Recursive CTE '{cte.ExpressionName.Value}' has no OPTION (MAXRECURSION n) on its containing statement - the engine's own default limit of 100 levels fails the statement outright (Msg 530) once exceeded.",
                    FindingConfidence.High));
            }
        }

        private void InspectGroupBySetsCardinality(GroupByClause? groupBy)
        {
            if (groupBy is null)
            {
                return;
            }

            if (groupBy.GroupByOption == GroupByOption.Cube)
            {
                ReportCubeCeilingIfExceeded(groupBy, groupBy.GroupingSpecifications.Count);
            }
            else if (groupBy.GroupByOption == GroupByOption.Rollup)
            {
                ReportRollupCeilingIfExceeded(groupBy, groupBy.GroupingSpecifications.Count);
            }

            foreach (var spec in groupBy.GroupingSpecifications)
            {
                InspectGroupingSpecification(spec);
            }
        }

        private void InspectGroupingSpecification(GroupingSpecification spec)
        {
            switch (spec)
            {
                case CubeGroupingSpecification cube:
                    ReportCubeCeilingIfExceeded(cube, cube.Arguments.Count);
                    foreach (var argument in cube.Arguments)
                    {
                        InspectGroupingSpecification(argument);
                    }

                    break;

                case RollupGroupingSpecification rollup:
                    ReportRollupCeilingIfExceeded(rollup, rollup.Arguments.Count);
                    foreach (var argument in rollup.Arguments)
                    {
                        InspectGroupingSpecification(argument);
                    }

                    break;

                case GroupingSetsGroupingSpecification groupingSets:
                    var combinationCount = GroupingSetsCombinationCount(groupingSets);
                    if (combinationCount > 4096)
                    {
                        Findings.Add(new QueryAntiPatternFinding(
                            QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded, sourcePath,
                            groupingSets.StartLine, groupingSets.StartColumn,
                            $"GROUPING SETS (...) expands to {combinationCount} grouping sets, exceeding the engine's fixed compile-time limit of 4096 - the statement does not compile (Msg 10703, \"Too many grouping sets. The maximum number is 4096.\").",
                            FindingConfidence.High));
                    }

                    foreach (var set in groupingSets.Sets)
                    {
                        InspectGroupingSpecification(set);
                    }

                    break;

                case CompositeGroupingSpecification composite:
                    foreach (var item in composite.Items)
                    {
                        InspectGroupingSpecification(item);
                    }

                    break;
            }
        }

        private void ReportCubeCeilingIfExceeded(TSqlFragment anchor, int columnCount)
        {
            if (columnCount <= 12)
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded, sourcePath,
                anchor.StartLine, anchor.StartColumn,
                $"CUBE over {columnCount} columns expands to 2^{columnCount} grouping sets, exceeding the engine's fixed compile-time limit of 4096 for CUBE (12 columns) - the statement does not compile (Msg 10703, \"Too many grouping sets. The maximum number is 4096.\").",
                FindingConfidence.High));
        }

        private void ReportRollupCeilingIfExceeded(TSqlFragment anchor, int columnCount)
        {
            if (columnCount <= 32)
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded, sourcePath,
                anchor.StartLine, anchor.StartColumn,
                $"ROLLUP over {columnCount} columns exceeds the engine's fixed compile-time limit of 32 grouping expressions for an extended GROUP BY clause - the statement does not compile (Msg 10706, \"Too many expressions are specified in the GROUP BY clause. The maximum number is 32 when grouping sets are supplied.\").",
                FindingConfidence.High));
        }

        private static long GroupingSetsCombinationCount(GroupingSpecification spec) => spec switch
        {
            CubeGroupingSpecification cube => CubePower(cube.Arguments.Count),
            RollupGroupingSpecification rollup => rollup.Arguments.Count + 1L,
            GroupingSetsGroupingSpecification nested => SumClamped(nested.Sets),
            _ => 1L,
        };

        private static long CubePower(int columnCount) =>
            columnCount >= 62 ? long.MaxValue / 2 : 1L << columnCount;

        private static long SumClamped(IList<GroupingSpecification> specs)
        {
            var total = 0L;
            foreach (var spec in specs)
            {
                total += GroupingSetsCombinationCount(spec);
                if (total > 1_000_000)
                {
                    return total;
                }
            }

            return total;
        }

        private void InspectStaleTableVariableInLoop(WhileStatement node)
        {
            var writtenVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var readSites = new List<VariableTableReference>();

            var collector = new LoopWriteAndReadCollector(readSites, writtenVariables, _tableVariableNames);
            node.Statement.Accept(collector);

            foreach (var read in readSites.Where(read => writtenVariables.Contains(read.Variable.Name)))
            {
                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop, sourcePath,
                    read.StartLine, read.StartColumn,
                    $"{read.Variable.Name} read inside a WHILE loop that also writes to it",
                    FindingConfidence.Medium));
            }
        }

        private static IEnumerable<VariableTableReference> CollectVariableTableReferences(TableReference tableReference)
        {
            switch (tableReference)
            {
                case VariableTableReference variableRef:
                    yield return variableRef;
                    break;

                case QualifiedJoin join:
                    foreach (var v in CollectVariableTableReferences(join.FirstTableReference))
                    {
                        yield return v;
                    }

                    foreach (var v in CollectVariableTableReferences(join.SecondTableReference))
                    {
                        yield return v;
                    }

                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var v in CollectVariableTableReferences(parenthesis.Join))
                    {
                        yield return v;
                    }

                    break;
            }
        }

        private sealed class LoopWriteAndReadCollector(
            List<VariableTableReference> readSites,
            HashSet<string> writtenVariables,
            HashSet<string> knownTableVariables) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(WhileStatement node)
            {
                _ = node;
            }

            public override void ExplicitVisit(FromClause node)
            {
                foreach (var tableReference in node.TableReferences)
                {
                    foreach (var variableRef in CollectVariableTableReferences(tableReference)
                        .Where(v => knownTableVariables.Contains(v.Variable.Name)))
                    {
                        readSites.Add(variableRef);
                    }
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InsertStatement node)
            {
                RecordWrite(node.InsertSpecification.Target, null);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(UpdateStatement node)
            {
                RecordWrite(node.UpdateSpecification.Target, node.UpdateSpecification.OutputIntoClause);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteStatement node)
            {
                RecordWrite(node.DeleteSpecification.Target, node.DeleteSpecification.OutputIntoClause);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(MergeStatement node)
            {
                RecordWrite(node.MergeSpecification.Target, node.MergeSpecification.OutputIntoClause);
                base.ExplicitVisit(node);
            }

            private void RecordWrite(TableReference target, OutputIntoClause? outputInto)
            {
                if (target is VariableTableReference targetVariable)
                {
                    writtenVariables.Add(targetVariable.Variable.Name);
                }

                if (outputInto?.IntoTable is VariableTableReference outputVariable)
                {
                    writtenVariables.Add(outputVariable.Variable.Name);
                }
            }
        }

        private void InspectRbarSingleRowLoopDml(WhileStatement node)
        {
            var assignedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dmlSites = new List<(WhereClause? Where, int Line, int Column)>();
            var collector = new LoopDmlAndAssignmentCollector(dmlSites, assignedVariables);
            node.Statement.Accept(collector);

            foreach (var (where, line, column) in dmlSites)
            {
                if (SingleVariableEqualityColumn(where, assignedVariables) is { } detail)
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.RbarSingleRowLoopDml, sourcePath, line, column,
                        detail, FindingConfidence.Medium));
                }
            }
        }

        private static string? SingleVariableEqualityColumn(WhereClause? where, HashSet<string> loopVariables)
        {
            if (where?.SearchCondition is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp)
            {
                return null;
            }

            var (columnExpr, variableExpr) = cmp.FirstExpression switch
            {
                ColumnReferenceExpression col when cmp.SecondExpression is VariableReference v => (col, v),
                VariableReference v when cmp.SecondExpression is ColumnReferenceExpression col => (col, v),
                _ => (null, null),
            };

            if (columnExpr is null || variableExpr is null || !loopVariables.Contains(variableExpr.Name))
            {
                return null;
            }

            var columnName = columnExpr.MultiPartIdentifier.Identifiers is { Count: > 0 } identifiers
                ? identifiers[^1].Value
                : "?";
            return $"{columnName} = {variableExpr.Name}";
        }

        private sealed class LoopDmlAndAssignmentCollector(
            List<(WhereClause? Where, int Line, int Column)> dmlSites,
            HashSet<string> assignedVariables) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(WhileStatement node)
            {
                _ = node;
            }

            public override void ExplicitVisit(UpdateStatement node)
            {
                dmlSites.Add((node.UpdateSpecification.WhereClause, node.StartLine, node.StartColumn));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteStatement node)
            {
                dmlSites.Add((node.DeleteSpecification.WhereClause, node.StartLine, node.StartColumn));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SetVariableStatement node)
            {
                if (node.CursorDefinition is null)
                {
                    assignedVariables.Add(node.Variable.Name);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SelectSetVariable node)
            {
                assignedVariables.Add(node.Variable.Name);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(FetchCursorStatement node)
            {
                foreach (var v in node.IntoVariables)
                {
                    assignedVariables.Add(v.Name);
                }

                base.ExplicitVisit(node);
            }
        }

        private void InspectCursorGlobalness(CursorDefinition definition, string cursorName, int line, int column)
        {
            var kinds = definition.Options.Select(o => o.OptionKind).ToHashSet();
            if (kinds.Contains(CursorOptionKind.Local))
            {
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.GlobalCursorDeclaration, sourcePath, line, column,
                $"{cursorName} ({(kinds.Contains(CursorOptionKind.Global) ? "explicit GLOBAL" : "no LOCAL/GLOBAL keyword, defaults to GLOBAL")})",
                FindingConfidence.Low));
        }

        private void InspectCountStarExistenceSequence(IList<TSqlStatement> statements)
        {
            for (var i = 0; i + 1 < statements.Count; i++)
            {
                if (CountStarAssignedVariable(statements[i]) is not { } variableName)
                {
                    continue;
                }

                if (IsZeroExistenceComparison(NextStatementPredicate(statements[i + 1]), variableName))
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.CountStarVariableExistenceCheck, sourcePath,
                        statements[i].StartLine, statements[i].StartColumn,
                        $"{variableName} = COUNT(*) then compared only to zero in the very next statement",
                        FindingConfidence.High));
                }
            }
        }

        private static string? CountStarAssignedVariable(TSqlStatement statement)
        {
            if (statement is not SelectStatement { QueryExpression: QuerySpecification { SelectElements.Count: 1 } spec }
                || spec.SelectElements[0] is not SelectSetVariable { AssignmentKind: AssignmentKind.Equals } setVar
                || setVar.Expression is not FunctionCall call
                || !CountStarFunctionNames.Contains(call.FunctionName.Value)
                || call.Parameters.Count != 1)
            {
                return null;
            }

            var isStar = call.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard };
            var isOne = call.Parameters[0] is IntegerLiteral { Value: "1" };
            return isStar || isOne ? setVar.Variable.Name : null;
        }

        private static BooleanExpression? NextStatementPredicate(TSqlStatement statement) => statement switch
        {
            IfStatement ifStatement => ifStatement.Predicate,
            WhileStatement whileStatement => whileStatement.Predicate,
            _ => null,
        };

        private static bool IsZeroExistenceComparison(BooleanExpression? expression, string variableName)
        {
            if (expression is not BooleanComparisonExpression cmp)
            {
                return false;
            }

            var (literal, comparisonType) = cmp.FirstExpression switch
            {
                VariableReference v when string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase)
                    && cmp.SecondExpression is IntegerLiteral secondLiteral
                    => (secondLiteral, cmp.ComparisonType),
                IntegerLiteral firstLiteral when cmp.SecondExpression is VariableReference v
                    && string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase)
                    => (firstLiteral, Flip(cmp.ComparisonType)),
                _ => (null, cmp.ComparisonType),
            };

            if (literal is null)
            {
                return false;
            }

            return (comparisonType, literal.Value) switch
            {
                (BooleanComparisonType.GreaterThan, "0") => true,
                (BooleanComparisonType.GreaterThanOrEqualTo, "1") => true,
                (BooleanComparisonType.Equals, "0") => true,
                (BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation, "0") => true,
                _ => false,
            };
        }

        private static BooleanComparisonType Flip(BooleanComparisonType type) => type switch
        {
            BooleanComparisonType.GreaterThan => BooleanComparisonType.LessThan,
            BooleanComparisonType.LessThan => BooleanComparisonType.GreaterThan,
            BooleanComparisonType.GreaterThanOrEqualTo => BooleanComparisonType.LessThanOrEqualTo,
            BooleanComparisonType.LessThanOrEqualTo => BooleanComparisonType.GreaterThanOrEqualTo,
            _ => type,
        };

        private void InspectHaving(
            QuerySpecification node, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain, ModuleWalker walker)
        {
            if (node.HavingClause?.SearchCondition is not { } having || node.GroupByClause is not { } groupBy)
            {
                return;
            }

            var groupByColumns = groupBy.GroupingSpecifications
                .OfType<ExpressionGroupingSpecification>()
                .Select(g => g.Expression)
                .OfType<ColumnReferenceExpression>()
                .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
                .ToHashSet(catalog.IdentifierComparer);

            var dead = PredicateSurvivalAnalyzer.FindDeadComparisons(having, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain));

            foreach (var condition in PredicateTreeWalker.FlattenAnd(having))
            {
                if (ContainsAggregate(condition) || dead.Contains(condition))
                {
                    continue;
                }

                var collector = new ColumnAliasHelpers.RawColumnReferenceCollector();
                condition.Accept(collector);
                if (collector.References.Count == 0)
                {
                    continue;
                }

                var allGroupByOrLiteral = collector.References.All(c =>
                    groupByColumns.Contains(c.MultiPartIdentifier.Identifiers[^1].Value));
                if (!allGroupByOrLiteral)
                {
                    continue;
                }

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.NonAggregateHavingPredicate, sourcePath,
                    condition.StartLine, condition.StartColumn,
                    "HAVING condition references only GROUP BY key columns/literals - equivalent WHERE condition would filter before aggregation",
                    FindingConfidence.High));
            }
        }

        private static bool ContainsAggregate(TSqlFragment fragment)
        {
            var collector = new AggregateCallCollector();
            fragment.Accept(collector);
            return collector.Found;
        }

        private sealed class AggregateCallCollector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(FunctionCall node)
            {
                if (AggregateFunctionNames.Contains(node.FunctionName.Value))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }
        }

        private void InspectDistinctJoinFanout(QuerySpecification node, IReadOnlyDictionary<string, ScopeEntry> byAlias, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.UniqueRowFilter != UniqueRowFilter.Distinct || node.FromClause is null)
            {
                return;
            }

            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(node.WhereClause?.SearchCondition, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain)))
            {
                return;
            }

            foreach (var join in node.FromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                var joinedAlias = AliasOf(join.SecondTableReference);
                if (joinedAlias is null || !byAlias.TryGetValue(joinedAlias, out var joinedEntry)
                    || joinedEntry.IsViewLayer || joinedEntry.Relation.QualifiedName is not { } joinedQualifiedName)
                {
                    continue;
                }

                var joinedTable = catalog.Find(joinedQualifiedName);
                if (joinedTable is null)
                {
                    continue;
                }

                var joinColumns = PredicateTreeWalker.FlattenAnd(join.SearchCondition)
                    .OfType<BooleanComparisonExpression>()
                    .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                    .SelectMany(c => new[] { c.FirstExpression, c.SecondExpression })
                    .Select(e => ColumnAliasHelpers.ColumnNameIfQualifiedByAlias(e, joinedAlias, catalog.IdentifierComparer))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .Distinct(catalog.IdentifierComparer)
                    .ToList();

                if (joinColumns.Count == 0)
                {
                    continue;
                }

                var isProvablyUnique = joinedTable.Indexes.Any(ix =>
                    ix.IsUnique && !ix.IsFiltered && !ix.IsDisabled
                    && ix.KeyColumns.Count > 0
                    && ix.KeyColumns.All(kc => joinColumns.Contains(kc, catalog.IdentifierComparer)));
                if (isProvablyUnique)
                {
                    continue;
                }

                Findings.Add(new QueryAntiPatternFinding(
                    QueryAntiPatternFindingKind.DistinctMaskingJoinFanout, sourcePath,
                    join.StartLine, join.StartColumn,
                    $"SELECT DISTINCT joins {joinedQualifiedName} on columns not backed by a unique index ({string.Join(", ", joinColumns)})",
                    FindingConfidence.Medium));
            }
        }

        private static void MarkNestedUnionChain(QueryExpression expression, HashSet<BinaryQueryExpression> sink)
        {
            if (expression is BinaryQueryExpression { BinaryQueryExpressionType: BinaryQueryExpressionType.Union, All: false } inner)
            {
                sink.Add(inner);
                MarkNestedUnionChain(inner.FirstQueryExpression, sink);
                MarkNestedUnionChain(inner.SecondQueryExpression, sink);
            }
        }

        private void InspectUnionDisjointness(BinaryQueryExpression topUnion)
        {
            var branches = FlattenUnionBranches(topUnion);
            if (branches is null || branches.Count < 2)
            {
                return;
            }

            var equalities = new List<(string TableQualifiedName, string ColumnName, ScalarExpression Literal)>();
            foreach (var branch in branches)
            {
                if (SingleTableSingleEqualityLiteral(branch) is not { } equality)
                {
                    return;
                }

                equalities.Add(equality);
            }

            var sameTableAndColumn = equalities
                .Select(e => (e.TableQualifiedName, e.ColumnName))
                .Distinct()
                .Count() == 1;
            if (!sameTableAndColumn)
            {
                return;
            }

            var literalTexts = equalities.Select(e => LiteralText(e.Literal)).ToList();
            if (literalTexts.Any(t => t is null) || literalTexts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != literalTexts.Count)
            {

                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches, sourcePath,
                topUnion.StartLine, topUnion.StartColumn,
                $"UNION of {branches.Count} branches, each filtering {equalities[0].TableQualifiedName}.{equalities[0].ColumnName} to a distinct literal - provably mutually exclusive, UNION ALL is equivalent",
                FindingConfidence.Medium));
        }

        private static List<QuerySpecification>? FlattenUnionBranches(QueryExpression expression)
        {
            switch (expression)
            {
                case QuerySpecification spec:
                    return [spec];

                case BinaryQueryExpression { BinaryQueryExpressionType: BinaryQueryExpressionType.Union, All: false } union:
                    var first = FlattenUnionBranches(union.FirstQueryExpression);
                    var second = FlattenUnionBranches(union.SecondQueryExpression);
                    if (first is null || second is null)
                    {
                        return null;
                    }

                    first.AddRange(second);
                    return first;

                default:
                    return null;
            }
        }

        private (string TableQualifiedName, string ColumnName, ScalarExpression Literal)? SingleTableSingleEqualityLiteral(QuerySpecification spec)
        {
            if (spec.FromClause is not { TableReferences.Count: 1 } from
                || from.TableReferences[0] is not NamedTableReference named
                || spec.WhereClause?.SearchCondition is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp)
            {
                return null;
            }

            var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            if (catalog.Find(qualifiedName) is not { Kind: CatalogTableKind.Table })
            {
                return null;
            }

            var (columnExpr, literalExpr) = cmp.FirstExpression switch
            {
                ColumnReferenceExpression col when IsLiteral(cmp.SecondExpression) => (col, cmp.SecondExpression),
                _ when IsLiteral(cmp.FirstExpression) && cmp.SecondExpression is ColumnReferenceExpression col => (col, cmp.FirstExpression),
                _ => (null, null),
            };

            if (columnExpr is null || literalExpr is null)
            {
                return null;
            }

            var columnAlias = ColumnAliasHelpers.ColumnNameIfQualifiedByAlias(columnExpr, alias, catalog.IdentifierComparer);
            var columnName = columnAlias ?? (columnExpr.MultiPartIdentifier.Identifiers.Count == 1 ? columnExpr.MultiPartIdentifier.Identifiers[0].Value : null);
            return columnName is null ? null : (qualifiedName, columnName, literalExpr);
        }

        private static bool IsLiteral(ScalarExpression expression) => expression is Literal;

        private static string? LiteralText(ScalarExpression expression) => expression switch
        {
            StringLiteral s => s.Value,
            IntegerLiteral i => i.Value,
            NumericLiteral n => n.Value,
            _ => null,
        };

    }

}
