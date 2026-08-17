using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §B "Query anti-patterns
/// still unbuilt" - one scanner, one visitor, the eight <see cref="QueryAntiPatternFindingKind"/>
/// members that survived precision scrutiny. See <see cref="QueryAntiPatternFinding"/> for each
/// kind's own scope/precision story and oracle evidence.
/// </summary>
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
        public List<QueryAntiPatternFinding> Findings { get; } = [];

        private readonly HashSet<string> _tableVariableNames = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            _tableVariableNames.Add(node.Body.VariableName.Value);
            base.ExplicitVisit(node);
        }

        // --- Table variable as a query source (kinds 1 & 2) -----------------------------------

        public override void ExplicitVisit(FromClause node)
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
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(WhileStatement node)
        {
            if (catalog.CompatibilityLevel is not { } knownLevel || knownLevel >= 150)
            {
                InspectStaleTableVariableInLoop(node);
            }

            InspectRbarSingleRowLoopDml(node);

            base.ExplicitVisit(node);
        }

        private void InspectStaleTableVariableInLoop(WhileStatement node)
        {
            var writtenVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var readSites = new List<VariableTableReference>();

            var collector = new LoopWriteAndReadCollector(readSites, writtenVariables, _tableVariableNames);
            node.Statement.Accept(collector);

            foreach (var read in readSites)
            {
                if (writtenVariables.Contains(read.Variable.Name))
                {
                    Findings.Add(new QueryAntiPatternFinding(
                        QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop, sourcePath,
                        read.StartLine, read.StartColumn,
                        $"{read.Variable.Name} read inside a WHILE loop that also writes to it",
                        FindingConfidence.Medium));
                }
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

        /// <summary>Walks a loop body WITHOUT descending into a nested <c>WHILE</c>'s own body - a
        /// nested loop's own reads/writes are a materially different site, inspected separately
        /// when that nested <see cref="WhileStatement"/> is visited on its own.</summary>
        private sealed class LoopWriteAndReadCollector(
            List<VariableTableReference> readSites,
            HashSet<string> writtenVariables,
            HashSet<string> knownTableVariables) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(WhileStatement node)
            {
                // Deliberately does not call base.ExplicitVisit - stops descent into the nested
                // loop's own body.
            }

            public override void ExplicitVisit(FromClause node)
            {
                foreach (var tableReference in node.TableReferences)
                {
                    foreach (var variableRef in CollectVariableTableReferences(tableReference))
                    {
                        if (knownTableVariables.Contains(variableRef.Variable.Name))
                        {
                            readSites.Add(variableRef);
                        }
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

        // --- Row-by-row single-row DML in a WHILE loop (kind 3) --------------------------------

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

        /// <summary>True when <paramref name="where"/> is a single, top-level equality between a
        /// column and one of <paramref name="loopVariables"/> - AND-flattened, never through OR.</summary>
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
                // Stops descent into a nested loop's own body - see the analogous note on
                // LoopWriteAndReadCollector above.
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

        // --- Cursor declared without LOCAL (kind 4) --------------------------------------------

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            InspectCursorGlobalness(node.CursorDefinition, node.Name.Value, node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.CursorDefinition is not null)
            {
                InspectCursorGlobalness(node.CursorDefinition, node.Variable.Name, node.StartLine, node.StartColumn);
            }

            base.ExplicitVisit(node);
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

        // --- COUNT(*) assigned to a variable, then compared only to zero (kind 5) --------------

        public override void ExplicitVisit(StatementList node)
        {
            InspectCountStarExistenceSequence(node.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TSqlBatch node)
        {
            InspectCountStarExistenceSequence(node.Statements);
            _tableVariableNames.Clear();
            base.ExplicitVisit(node);
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
            if (statement is not SelectStatement { QueryExpression: QuerySpecification { SelectElements.Count: 1 } spec } select
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

        // --- Non-aggregate predicate in HAVING that belongs in WHERE (kind 6) ------------------

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectHaving(node);
            InspectDistinctJoinFanout(node);
            base.ExplicitVisit(node);
        }

        private void InspectHaving(QuerySpecification node)
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
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var condition in FlattenAnd(having))
            {
                if (ContainsAggregate(condition))
                {
                    continue;
                }

                var collector = new ColumnReferenceCollector();
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

        // --- DISTINCT masking a join fan-out (kind 8, reuses NonUniqueUpdateSourceScanner's
        // composite-uniqueness catalog check) ---------------------------------------------------

        private void InspectDistinctJoinFanout(QuerySpecification node)
        {
            if (node.UniqueRowFilter != UniqueRowFilter.Distinct || node.FromClause is null)
            {
                return;
            }

            foreach (var join in node.FromClause.TableReferences.SelectMany(FlattenJoinNodes))
            {
                var (joinedAlias, joinedQualifiedName) = ResolveDirectBaseTable(join.SecondTableReference);
                if (joinedAlias is null || joinedQualifiedName is null)
                {
                    continue;
                }

                var joinedTable = catalog.Find(joinedQualifiedName);
                if (joinedTable is null)
                {
                    continue;
                }

                var joinColumns = FlattenAnd(join.SearchCondition)
                    .OfType<BooleanComparisonExpression>()
                    .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                    .SelectMany(c => new[] { c.FirstExpression, c.SecondExpression })
                    .Select(e => ColumnNameIfQualifiedByAlias(e, joinedAlias))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (joinColumns.Count == 0)
                {
                    continue;
                }

                var isProvablyUnique = joinedTable.Indexes.Any(ix =>
                    ix.IsUnique && !ix.IsFiltered && !ix.IsDisabled
                    && ix.KeyColumns.Count > 0
                    && ix.KeyColumns.All(kc => joinColumns.Contains(kc, StringComparer.OrdinalIgnoreCase)));
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

        // --- UNION of provably disjoint branches (kind 7) --------------------------------------

        private readonly HashSet<BinaryQueryExpression> _consumedUnionChainNodes = [];

        public override void ExplicitVisit(BinaryQueryExpression node)
        {
            if (node.BinaryQueryExpressionType == BinaryQueryExpressionType.Union && !node.All
                && !_consumedUnionChainNodes.Contains(node))
            {
                // A chain of more than two UNION branches nests as BinaryQueryExpression inside
                // BinaryQueryExpression - mark every nested union node BEFORE the traversal
                // reaches them via base.ExplicitVisit below, so the same multi-way union is
                // inspected exactly once, at its own outermost node, never once per nesting level.
                MarkNestedUnionChain(node.FirstQueryExpression, _consumedUnionChainNodes);
                MarkNestedUnionChain(node.SecondQueryExpression, _consumedUnionChainNodes);
                InspectUnionDisjointness(node);
            }

            base.ExplicitVisit(node);
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
                // Either a non-literal comparand this pass won't reason about, or two branches
                // share the same literal (not provably disjoint - could be a genuine overlap).
                return;
            }

            Findings.Add(new QueryAntiPatternFinding(
                QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches, sourcePath,
                topUnion.StartLine, topUnion.StartColumn,
                $"UNION of {branches.Count} branches, each filtering {equalities[0].TableQualifiedName}.{equalities[0].ColumnName} to a distinct literal - provably mutually exclusive, UNION ALL is equivalent",
                FindingConfidence.Medium));
        }

        /// <summary>Flattens a chain of plain (non-ALL) UNION nodes into its leaf
        /// <see cref="QuerySpecification"/> branches - null when any branch is itself an EXCEPT/
        /// INTERSECT/UNION ALL, or not a plain query specification at all (a materially different
        /// shape this pass declines rather than guesses at).</summary>
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

            var columnAlias = ColumnNameIfQualifiedByAlias(columnExpr, alias);
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

        // --- Shared join/predicate flattening helpers (matching NonUniqueUpdateSourceScanner) -

        private (string? Alias, string? QualifiedName) ResolveDirectBaseTable(TableReference tableReference)
        {
            if (tableReference is not NamedTableReference named)
            {
                return (null, null);
            }

            var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } ? (alias, qualifiedName) : (null, null);
        }

        private static IEnumerable<QualifiedJoin> FlattenJoinNodes(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    foreach (var t in FlattenJoinNodes(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenJoinNodes(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    yield return join;
                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenJoinNodes(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
            }
        }

        private static IEnumerable<BooleanExpression> FlattenAnd(BooleanExpression? expression)
        {
            switch (expression)
            {
                case null:
                    yield break;

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    foreach (var e in FlattenAnd(and.FirstExpression))
                    {
                        yield return e;
                    }

                    foreach (var e in FlattenAnd(and.SecondExpression))
                    {
                        yield return e;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var e in FlattenAnd(paren.Expression))
                    {
                        yield return e;
                    }

                    break;

                default:
                    yield return expression;
                    break;
            }
        }

        private static string? ColumnNameIfQualifiedByAlias(ScalarExpression expression, string alias)
        {
            if (expression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            var identifiers = columnRef.MultiPartIdentifier.Identifiers;
            return identifiers.Count >= 2 && string.Equals(identifiers[^2].Value, alias, StringComparison.OrdinalIgnoreCase)
                ? identifiers[^1].Value
                : null;
        }

        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> References { get; } = [];

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                References.Add(node);
                base.ExplicitVisit(node);
            }
        }
    }
}
