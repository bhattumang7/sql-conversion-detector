using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class TriggerCorrectnessScanner
{
    private static readonly HashSet<string> PseudoTableNames = new(StringComparer.OrdinalIgnoreCase) { "inserted", "deleted" };

    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "APPROX_COUNT_DISTINCT",
        "CHECKSUM_AGG", "GROUPING", "GROUPING_ID", "STDEV", "STDEVP", "STRING_AGG", "VAR", "VARP",
    };

    public static IReadOnlyList<TriggerCorrectnessFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<TriggerCorrectnessFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<TriggerCorrectnessFinding> Findings { get; } = [];

        public void OnEnterTriggerStatementScope(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, ModuleWalker walker) =>
            VisitTrigger(node, name, triggerObject, node.StatementList);

        private void VisitTrigger(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, StatementList? statementList)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(name);

            var topLevelStatements = statementList?.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList?.Statements;

            if (topLevelStatements is not null)
            {
                InspectMultiRowUnsafeAssignments(qualifiedName, topLevelStatements);
                InspectMissingEarlyOut(qualifiedName, node, topLevelStatements);
                InspectInsteadOfInsertFilteredReinsert(qualifiedName, node, triggerObject, topLevelStatements);
            }

            if (triggerObject.Name is { } targetTableName)
            {
                InspectDirectRecursion(qualifiedName, node, targetTableName, statementList);
            }
            else if (statementList is not null && IsLogonTrigger(node))
            {
                InspectLogonTriggerHostNameGate(qualifiedName, statementList);
            }

            if (statementList is not null)
            {
                InspectUpdateFunctionWithoutValueComparison(qualifiedName, statementList);
            }

        }

        private static bool IsLogonTrigger(TriggerStatementBody node) =>
            node.TriggerActions is { } actions && actions.Any(a => a.TriggerActionType == TriggerActionType.LogOn);

        private void InspectMultiRowUnsafeAssignments(string triggerQualifiedName, IList<TSqlStatement> statements)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                var variableName = TryGetUnsafeAssignedVariable(statements[i]);
                if (variableName is null)
                {
                    continue;
                }

                var keyedDmlLine = FindStraightLineKeyedDmlUse(statements, i + 1, variableName);
                if (keyedDmlLine is { } line)
                {
                    Findings.Add(new TriggerCorrectnessFinding(
                        TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml, triggerQualifiedName, sourcePath,
                        statements[i].StartLine, statements[i].StartColumn,
                        $"'{variableName}' is assigned from a single, unspecified row of inserted/deleted with no WHERE/TOP/aggregate, then used as the sole key of a write at line {line} in the same trigger body - a multi-row INSERT/UPDATE/MERGE silently drives that write off one arbitrary row's value.",
                        FindingConfidence.High));
                    continue;
                }

                Findings.Add(new TriggerCorrectnessFinding(
                    TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment, triggerQualifiedName, sourcePath,
                    statements[i].StartLine, statements[i].StartColumn,
                    $"'{variableName}' is assigned from a single, unspecified row of inserted/deleted (no WHERE/TOP/aggregate) - silently wrong for every multi-row INSERT/UPDATE/MERGE that fires this trigger.",
                    FindingConfidence.High));
            }
        }

        private static string? TryGetUnsafeAssignedVariable(TSqlStatement statement) => statement switch
        {
            SelectStatement { QueryExpression: QuerySpecification { SelectElements: [SelectSetVariable { AssignmentKind: AssignmentKind.Equals } setVar] } spec }
                when IsUnsafeSourceQuery(spec) && !IsAggregateExpression(setVar.Expression)
                => setVar.Variable.Name,

            SetVariableStatement { AssignmentKind: AssignmentKind.Equals, Expression: ScalarSubquery { QueryExpression: QuerySpecification spec } } set
                when IsUnsafeSourceQuery(spec) && !SelectListIsAggregateOnly(spec)
                => set.Variable.Name,

            DeclareVariableStatement declare => TryGetUnsafeDeclareInitializer(declare),

            _ => null,
        };

        private static string? TryGetUnsafeDeclareInitializer(DeclareVariableStatement declare)
        {
            foreach (var element in declare.Declarations)
            {
                if (element.Value is ScalarSubquery { QueryExpression: QuerySpecification spec }
                    && IsUnsafeSourceQuery(spec) && !SelectListIsAggregateOnly(spec))
                {
                    return element.VariableName.Value;
                }
            }

            return null;
        }

        private static bool SelectListIsAggregateOnly(QuerySpecification spec) =>
            spec.SelectElements is [SelectScalarExpression { Expression: { } expr }] && IsAggregateExpression(expr);

        private static bool IsAggregateExpression(ScalarExpression expression) =>
            expression is FunctionCall call && AggregateFunctionNames.Contains(call.FunctionName.Value);

        private static bool IsUnsafeSourceQuery(QuerySpecification spec) =>
            spec.WhereClause is null
            && spec.TopRowFilter is null
            && spec.FromClause is { TableReferences: [NamedTableReference { SchemaObject.SchemaIdentifier: null } named] }
            && PseudoTableNames.Contains(named.SchemaObject.BaseIdentifier.Value);

        private static int? FindStraightLineKeyedDmlUse(IList<TSqlStatement> statements, int startIndex, string variableName)
        {
            for (var i = startIndex; i < statements.Count; i++)
            {
                var whereClause = statements[i] switch
                {
                    UpdateStatement { UpdateSpecification.Target: NamedTableReference } upd => upd.UpdateSpecification.WhereClause,
                    DeleteStatement { DeleteSpecification.Target: NamedTableReference } del => del.DeleteSpecification.WhereClause,
                    _ => null,
                };

                if (whereClause is not null && IsSoleKeyEquality(whereClause.SearchCondition, variableName))
                {
                    return statements[i].StartLine;
                }
            }

            return null;
        }

        private static bool IsSoleKeyEquality(BooleanExpression condition, string variableName) =>
            condition is BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp
            && ((cmp.FirstExpression is ColumnReferenceExpression && cmp.SecondExpression is VariableReference v1
                    && string.Equals(v1.Name, variableName, StringComparison.OrdinalIgnoreCase))
                || (cmp.SecondExpression is ColumnReferenceExpression && cmp.FirstExpression is VariableReference v2
                    && string.Equals(v2.Name, variableName, StringComparison.OrdinalIgnoreCase)));

        private void InspectMissingEarlyOut(string triggerQualifiedName, TriggerStatementBody node, IList<TSqlStatement> statements)
        {
            var guardCollector = new EarlyOutGuardCollector();
            foreach (var statement in statements)
            {
                statement.Accept(guardCollector);
            }

            if (guardCollector.Found)
            {
                return;
            }

            Findings.Add(new TriggerCorrectnessFinding(
                TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation, triggerQualifiedName, sourcePath,
                node.StartLine, node.StartColumn,
                $"'{triggerQualifiedName}' has no IF NOT EXISTS(SELECT * FROM inserted/deleted)/IF @@ROWCOUNT = 0 RETURN-style guard - a well-documented convention to skip unnecessary work on an empty invocation, not a proven defect for this specific body.",
                FindingConfidence.Low));
        }

        private sealed class EarlyOutGuardCollector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(IfStatement node)
            {
                if (IsRowCountZeroCheck(node.Predicate) || IsNotExistsOverPseudoTable(node.Predicate))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }

            private static bool IsRowCountZeroCheck(BooleanExpression predicate) =>
                predicate is BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp
                && ((IsRowCountFunction(cmp.FirstExpression) && cmp.SecondExpression is IntegerLiteral { Value: "0" })
                    || (IsRowCountFunction(cmp.SecondExpression) && cmp.FirstExpression is IntegerLiteral { Value: "0" }));

            private static bool IsRowCountFunction(ScalarExpression expression) =>
                expression is GlobalVariableExpression global && string.Equals(global.Name, "@@ROWCOUNT", StringComparison.OrdinalIgnoreCase);

            private static bool IsNotExistsOverPseudoTable(BooleanExpression predicate) =>
                predicate is BooleanNotExpression not && ExistsOverPseudoTable(not.Expression);

            private static bool ExistsOverPseudoTable(BooleanExpression expression) =>
                expression is ExistsPredicate { Subquery.QueryExpression: QuerySpecification { FromClause.TableReferences: [NamedTableReference named, ..] } }
                && PseudoTableNames.Contains(named.SchemaObject.BaseIdentifier.Value);
        }

        private void InspectInsteadOfInsertFilteredReinsert(string triggerQualifiedName, TriggerStatementBody node, TriggerObject triggerObject, IList<TSqlStatement> topLevelStatements)
        {
            if (node.TriggerType != TriggerType.InsteadOf
                || node.TriggerActions is not { } actions
                || !actions.Any(a => a.TriggerActionType == TriggerActionType.Insert))
            {
                return;
            }

            var filteredInserts = topLevelStatements
                .OfType<InsertStatement>()
                .Where(ins => ins.InsertSpecification.Target is NamedTableReference
                    && ins.InsertSpecification.InsertSource is SelectInsertSource { Select: QuerySpecification spec }
                    && IsFilteredReinsertFromInserted(spec))
                .ToList();

            if (filteredInserts.Count == 0)
            {
                return;
            }

            var hasCompanionInsert = topLevelStatements.OfType<InsertStatement>().Count() > filteredInserts.Count;
            var hasRaiseErrorOrThrow = ContainsRaiseErrorOrThrow(topLevelStatements);
            var hasProvenExhaustiveCoverage = filteredInserts.Count > 1 && HasProvenExhaustiveCoverage(filteredInserts, triggerObject);

            if (hasCompanionInsert || hasRaiseErrorOrThrow || hasProvenExhaustiveCoverage)
            {
                return;
            }

            foreach (var insert in filteredInserts)
            {
                Findings.Add(new TriggerCorrectnessFinding(
                    TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath, triggerQualifiedName, sourcePath,
                    insert.StartLine, insert.StartColumn,
                    "INSTEAD OF INSERT trigger re-inserts a WHERE/join-filtered subset of inserted with no companion INSERT/RAISERROR/THROW anywhere in the body - the caller's own INSERT reports success while rows matching the negated filter are silently dropped, no error, no trace.",
                    FindingConfidence.High));
            }
        }

        private static bool IsFilteredReinsertFromInserted(QuerySpecification spec)
        {
            if (spec.FromClause is not { TableReferences: { Count: > 0 } tableReferences })
            {
                return false;
            }

            var referencesInserted = tableReferences.Any(ReferencesInsertedTable);
            var isFiltered = spec.WhereClause is not null || tableReferences.Any(tr => tr is QualifiedJoin);
            return referencesInserted && isFiltered;
        }

        private static bool ReferencesInsertedTable(TableReference reference) => reference switch
        {
            NamedTableReference { SchemaObject.SchemaIdentifier: null } named
                => string.Equals(named.SchemaObject.BaseIdentifier.Value, "inserted", StringComparison.OrdinalIgnoreCase),
            QualifiedJoin join => ReferencesInsertedTable(join.FirstTableReference) || ReferencesInsertedTable(join.SecondTableReference),
            _ => false,
        };

        private bool HasProvenExhaustiveCoverage(List<InsertStatement> filteredInserts, TriggerObject triggerObject)
        {
            if (triggerObject.Name is not { } targetTableName)
            {
                return false;
            }

            var searchConditions = new List<BooleanExpression>();
            foreach (var insert in filteredInserts)
            {
                if (insert.InsertSpecification.InsertSource is not SelectInsertSource
                    {
                        Select: QuerySpecification
                        {
                            FromClause.TableReferences: [NamedTableReference { SchemaObject.SchemaIdentifier: null } named],
                            WhereClause: { SearchCondition: { } condition },
                        },
                    }
                    || !string.Equals(named.SchemaObject.BaseIdentifier.Value, "inserted", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                searchConditions.Add(condition);
            }

            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetTableName));

            PredicateSurvivalAnalyzer.ColumnFacts ResolveInsertedColumnFacts(ColumnReferenceExpression columnRef)
            {
                var identifiers = columnRef.MultiPartIdentifier.Identifiers;
                if (identifiers is not [.., { } last])
                {
                    return default;
                }

                if (identifiers.Count > 1 && !PseudoTableNames.Contains(identifiers[^2].Value))
                {
                    return default;
                }

                var catalogColumn = catalog.Find(targetQualifiedName)?.FindColumn(last.Value, catalog.IdentifierComparer);
                return new PredicateSurvivalAnalyzer.ColumnFacts(
                    catalogColumn is null ? null : !catalogColumn.IsNullable,
                    null,
                    catalogColumn?.Type?.Category is SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image);
            }

            return PredicateSurvivalAnalyzer.IsTautology(searchConditions, ResolveInsertedColumnFacts);
        }

        private static bool ContainsRaiseErrorOrThrow(IList<TSqlStatement> statements)
        {
            var collector = new RaiseErrorOrThrowCollector();
            foreach (var statement in statements)
            {
                statement.Accept(collector);
            }

            return collector.Found;
        }

        private sealed class RaiseErrorOrThrowCollector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(RaiseErrorStatement node) => Found = true;

            public override void ExplicitVisit(ThrowStatement node) => Found = true;
        }

        private void InspectUpdateFunctionWithoutValueComparison(string triggerQualifiedName, StatementList statementList)
        {
            var collector = new IfPredicateCollector();
            statementList.Accept(collector);

            foreach (var predicate in collector.Predicates)
            {
                var updateCalls = new UpdateCallCollector();
                predicate.Accept(updateCalls);

                foreach (var updateCall in updateCalls.Calls)
                {
                    var columnName = updateCall.Identifier.Value;
                    if (HasSameColumnValueComparison(predicate, columnName, catalog.IdentifierComparer))
                    {
                        continue;
                    }

                    Findings.Add(new TriggerCorrectnessFinding(
                        TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison, triggerQualifiedName, sourcePath,
                        updateCall.StartLine, updateCall.StartColumn,
                        $"IF UPDATE({columnName}) gates this branch with no comparison between inserted.{columnName}/deleted.{columnName} in the same predicate - UPDATE() reports whether the column was NAMED in the SET list, not whether its value changed, so a full-column UPDATE (an ORM's generated statement, e.g.) fires this branch on a genuine no-op save.",
                        FindingConfidence.High));
                }
            }
        }

        private static bool HasSameColumnValueComparison(BooleanExpression predicate, string columnName, StringComparer identifierComparer)
        {
            var collector = new SameColumnComparisonCollector(columnName, identifierComparer);
            predicate.Accept(collector);
            return collector.Found;
        }

        private sealed class IfPredicateCollector : TSqlFragmentVisitor
        {
            public List<BooleanExpression> Predicates { get; } = [];

            public override void ExplicitVisit(IfStatement node)
            {
                Predicates.Add(node.Predicate);
                base.ExplicitVisit(node);
            }
        }

        private sealed class UpdateCallCollector : TSqlFragmentVisitor
        {
            public List<UpdateCall> Calls { get; } = [];

            public override void ExplicitVisit(UpdateCall node) => Calls.Add(node);
        }

        private sealed class SameColumnComparisonCollector(string columnName, StringComparer identifierComparer) : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (IsColumnNamed(node.FirstExpression, columnName) && IsColumnNamed(node.SecondExpression, columnName))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }

            private bool IsColumnNamed(ScalarExpression expression, string columnName) =>
                expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] }
                && identifierComparer.Equals(last.Value, columnName);
        }

        private void InspectLogonTriggerHostNameGate(string triggerQualifiedName, StatementList statementList)
        {
            var collector = new HostNameGateCollector();
            statementList.Accept(collector);

            foreach (var site in collector.Sites)
            {
                Findings.Add(new TriggerCorrectnessFinding(
                    TriggerCorrectnessFindingKind.LogonTriggerHostNameGate, triggerQualifiedName, sourcePath,
                    site.Line, site.Column,
                    "Logon trigger denies/allows a connection based on HOST_NAME(), which is client-supplied via the connection string's own Workstation ID and unauthenticated - oracle-confirmed trivially spoofable, so this check does not actually control access.",
                    FindingConfidence.High));
            }
        }

        private sealed class HostNameGateCollector : TSqlFragmentVisitor
        {
            public List<(int Line, int Column)> Sites { get; } = [];

            public override void ExplicitVisit(IfStatement node)
            {
                if (ContainsHostNameCall(node.Predicate) && (ContainsRollback(node.ThenStatement) || (node.ElseStatement is not null && ContainsRollback(node.ElseStatement))))
                {
                    Sites.Add((node.StartLine, node.StartColumn));
                }

                base.ExplicitVisit(node);
            }

            private static bool ContainsHostNameCall(TSqlFragment fragment)
            {
                var collector = new FunctionCallCollector("HOST_NAME");
                fragment.Accept(collector);
                return collector.Found;
            }

            private static bool ContainsRollback(TSqlFragment fragment)
            {
                var collector = new RollbackCollector();
                fragment.Accept(collector);
                return collector.Found;
            }
        }

        private sealed class FunctionCallCollector(string functionName) : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(FunctionCall node)
            {
                if (string.Equals(node.FunctionName.Value, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }
        }

        private sealed class RollbackCollector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(RollbackTransactionStatement node) => Found = true;
        }

        private void InspectDirectRecursion(string triggerQualifiedName, TriggerStatementBody node, SchemaObjectName targetTableName, StatementList? statementList)
        {
            if (catalog.IsRecursiveTriggersEnabled != true || statementList is null)
            {

                return;
            }

            var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetTableName));
            var recursionCollector = new SelfRecursionCollector(catalog, targetQualifiedName);
            statementList.Accept(recursionCollector);

            if (recursionCollector.FoundAt is { } site)
            {
                Findings.Add(new TriggerCorrectnessFinding(
                    TriggerCorrectnessFindingKind.DirectRecursiveTrigger, triggerQualifiedName, sourcePath,
                    node.StartLine, node.StartColumn,
                    $"'{triggerQualifiedName}' writes directly to its own target table '{targetQualifiedName}' at line {site} - RECURSIVE_TRIGGERS is ON for the connected database, so this write re-fires this same trigger instead of silently no-oping.",
                    FindingConfidence.Medium));
            }
        }

        private sealed class SelfRecursionCollector(DatabaseCatalog catalog, string targetQualifiedName) : TSqlFragmentVisitor
        {
            public int? FoundAt { get; private set; }

            public override void ExplicitVisit(InsertStatement node) => Check(node.InsertSpecification.Target, node.StartLine);

            public override void ExplicitVisit(UpdateStatement node) => Check(node.UpdateSpecification.Target, node.StartLine);

            public override void ExplicitVisit(DeleteStatement node) => Check(node.DeleteSpecification.Target, node.StartLine);

            public override void ExplicitVisit(MergeStatement node) => Check(node.MergeSpecification.Target, node.StartLine);

            private void Check(TableReference? target, int line)
            {
                if (FoundAt is null
                    && target is NamedTableReference named
                    && catalog.IdentifierComparer.Equals(catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject)), targetQualifiedName))
                {
                    FoundAt = line;
                }
            }
        }
    }
}
