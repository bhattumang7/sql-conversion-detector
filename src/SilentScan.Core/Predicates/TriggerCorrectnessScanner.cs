using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §C "Trigger correctness".
/// One scanner, one visitor scoped per <c>CREATE/ALTER/CREATE OR ALTER TRIGGER</c> body - see
/// <see cref="TriggerCorrectnessFinding"/> for each kind's own precision story and oracle
/// evidence.
/// </summary>
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
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<TriggerCorrectnessFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTrigger(node, node.Name, node.TriggerObject, node.StatementList);

        private void VisitTrigger(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, StatementList? statementList)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(name);

            // The overwhelmingly common `AS BEGIN ... END` shape wraps the whole body in one
            // BeginEndBlockStatement - unwrap it exactly like ProcCallGraphBuilder's own
            // VisitScopedBody, so "the trigger's own top-level statements" is the real statement
            // list, not a one-element wrapper.
            var topLevelStatements = statementList?.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList?.Statements;

            if (topLevelStatements is not null)
            {
                InspectMultiRowUnsafeAssignments(qualifiedName, topLevelStatements);
                InspectMissingEarlyOut(qualifiedName, node, topLevelStatements);
            }

            if (triggerObject.Name is { } targetTableName)
            {
                InspectDirectRecursion(qualifiedName, node, targetTableName, statementList);
            }

            // Predicate/lineage-level pieces of this codebase already resolve inserted/deleted
            // scope for typed predicate work; this scanner stays a pure AST/catalog pass and does
            // not need that machinery for any of its own four kinds.
        }

        // --- Multi-row-unsafe single-row assignment (kinds 1 & 2) -------------------------------

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

        /// <summary>
        /// Recognizes <c>SELECT @v = col FROM inserted/deleted</c> (a top-level
        /// <see cref="SelectSetVariable"/> element) and the structurally identical scalar-subquery
        /// form assigned via <c>SET</c> or a <c>DECLARE</c> initializer - returns the single
        /// assigned variable's name, or null when the statement isn't this shape at all, assigns
        /// more than one variable, or the assigned expression is an aggregate (a real, well-defined
        /// single value regardless of row count).
        /// </summary>
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

        /// <summary>
        /// True when <paramref name="expression"/> is a direct aggregate function call
        /// (<c>COUNT(*)</c>, <c>MAX(col)</c>, ...) - a real, well-defined single value regardless
        /// of how many rows the source has, so assigning it from inserted/deleted with no WHERE/TOP
        /// is not the unsafe shape this scanner targets.
        /// </summary>
        private static bool IsAggregateExpression(ScalarExpression expression) =>
            expression is FunctionCall call && AggregateFunctionNames.Contains(call.FunctionName.Value);

        /// <summary>
        /// True when <paramref name="spec"/>'s own FROM clause is exactly the bare inserted/deleted
        /// pseudo-table (no join, no derived table, no alias-qualified schema), with no WHERE and
        /// no TOP - the shape that reads an unspecified single row when more than one is present.
        /// </summary>
        private static bool IsUnsafeSourceQuery(QuerySpecification spec) =>
            spec.WhereClause is null
            && spec.TopRowFilter is null
            && spec.FromClause is { TableReferences: [NamedTableReference { SchemaObject.SchemaIdentifier: null } named] }
            && PseudoTableNames.Contains(named.SchemaObject.BaseIdentifier.Value);

        /// <summary>
        /// The line of the FIRST subsequent top-level statement (never descending into an
        /// IF/WHILE/TRY branch - this scan has no fold-state to trace a value across a branch it
        /// can't cheaply prove) that is an UPDATE/DELETE against a real target whose WHERE clause
        /// is EXACTLY one top-level equality between a column and <paramref name="variableName"/> -
        /// or null when no such statement is found before the trigger body ends.
        /// </summary>
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

        // --- No early-out for the zero-row case (kind 3) ----------------------------------------

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

        /// <summary>
        /// Looks for <c>IF NOT EXISTS (SELECT ... FROM inserted/deleted ...)</c> or
        /// <c>IF @@ROWCOUNT = 0</c> anywhere in the trigger body, at any nesting depth - a
        /// structural presence check, not a position check (CLAUDE.md precision-over-recall: this
        /// kind is explicitly advisory, so a guard found anywhere in the body is accepted rather
        /// than second-guessing whether it sits early enough to matter).
        /// </summary>
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

        // --- Direct self-recursion (kind 4) -----------------------------------------------------

        private void InspectDirectRecursion(string triggerQualifiedName, TriggerStatementBody node, SchemaObjectName targetTableName, StatementList? statementList)
        {
            if (catalog.IsRecursiveTriggersEnabled != true || statementList is null)
            {
                // Live-mode-only, gated strictly on a live-confirmed TRUE - file-mode (null) and a
                // live-confirmed FALSE both mean this specific recursion path is a structural
                // no-op, so never overclaim a risk that is not actually live (see
                // DatabaseCatalog.IsRecursiveTriggersEnabled's own doc comment).
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
                    && string.Equals(catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject)), targetQualifiedName, StringComparison.OrdinalIgnoreCase))
                {
                    FoundAt = line;
                }
            }
        }
    }
}
