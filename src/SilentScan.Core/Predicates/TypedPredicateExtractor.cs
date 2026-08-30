using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class TypedPredicateExtractor
{

    public static PredicateExtractionResult Extract(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SqlType?>? externalVariables = null,
        DynamicSqlScope? enclosingScope = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
    {
        var resolvedViews = lineage.AllRelations;
        var ledger = new SkipLedger();
        var visitor = new Visitor(parseResult.SourcePath, catalog, resolvedViews, externalVariables, ledger, enclosingScope, callerScopeByCalleeScope);
        visitor.SeedEnclosingScope(parseResult.Fragment);
        parseResult.Fragment.Accept(visitor);
        return new PredicateExtractionResult(visitor.Findings, visitor.ExpressionDerivedFindings, visitor.CollationConflictFindings, visitor.WriteLossFindings, ledger.Entries, visitor.OversizedParameterFindings, visitor.UnderLengthParameterFindings, visitor.AnsiPaddingMismatchFindings, visitor.LocalVariablePredicateFindings, visitor.FilteredIndexParameterMismatchFindings);
    }

#pragma warning disable CS9107
    private sealed class Visitor(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, SqlType?>? externalVariables,
        SkipLedger ledger,
        DynamicSqlScope? enclosingScope = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
        : ScopedSqlVisitorBase(sourcePath, catalog, resolvedViews, ledger, enclosingScope?.ProcScope, callerScopeByCalleeScope)
#pragma warning restore CS9107
    {
        private const string PredicateOperandConstructKind = "predicate operand";

        private const string NonSeekableOperatorConstructKind = "non-seekable operator";

        private const string WriteTargetConstructKind = "write target";

        private const string WriteSourceConstructKind = "write source";

        private const string OperandPositionConstructKind = "comparison inside scalar expression";

        private const string OperandPositionLedgerReason = "not a seek position - nested inside a CASE/IIF/COALESCE/NULLIF branch (or similar operand position) within an enclosing filter clause";

        private const string NoColumnOperandConstructKind = "no column operand";

        private const string UnresolvedColumnComparisonConstructKind = "unresolved column comparison";

        private const string FoldableLiteralComparisonConstructKind = "foldable literal comparison";

        public void SeedEnclosingScope(TSqlFragment rootFragment)
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                PushCteRelations(BuildTriggerPseudoTableRelations(target, rootFragment));
            }
        }

        private enum PredicatePosition
        {
            NotSeekable,

            Seekable,

            SuppressedOperand,
        }

        private PredicatePosition _position;

        private bool _negated;

        private TSqlFragment? _currentPredicateFragment;

        private readonly Dictionary<string, SqlType?> _variables = externalVariables is null
            ? new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SqlType?>(externalVariables, StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _formalParameterNames = externalVariables is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(externalVariables.Keys, StringComparer.OrdinalIgnoreCase);

        public List<TypedPredicateFinding> Findings { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerivedFindings { get; } = [];

        public List<CollationConflictFinding> CollationConflictFindings { get; } = [];

        public List<WriteLossFinding> WriteLossFindings { get; } = [];

        public List<OversizedParameterFinding> OversizedParameterFindings { get; } = [];

        public List<UnderLengthParameterFinding> UnderLengthParameterFindings { get; } = [];

        public List<AnsiPaddingMismatchFinding> AnsiPaddingMismatchFindings { get; } = [];

        public List<LocalVariablePredicateFinding> LocalVariablePredicateFindings { get; } = [];

        public List<FilteredIndexParameterMismatchFinding> FilteredIndexParameterMismatchFindings { get; } = [];

        private Dictionary<(string Table, string Column), List<(string? IndexName, string LiteralText)>>? _literalEqualityFilteredIndexesByColumn;

        private Dictionary<(string Table, string Column), List<(string? IndexName, string LiteralText)>> LiteralEqualityFilteredIndexesByColumn
        {
            get
            {
                if (_literalEqualityFilteredIndexesByColumn is not null)
                {
                    return _literalEqualityFilteredIndexesByColumn;
                }

                var map = new Dictionary<(string, string), List<(string?, string)>>(TableColumnKeyComparer.For(catalog));
                foreach (var table in catalog.Tables)
                {
                    foreach (var index in table.Indexes)
                    {
                        if (!index.IsFiltered || index.FilterDefinition is not { } filterDefinition
                            || IndexDesignScanner.TryExtractSimpleLiteralEqualityFilter(filterDefinition, catalog.CompatibilityLevel) is not { } extracted)
                        {
                            continue;
                        }

                        var key = (table.QualifiedName, extracted.ColumnName);
                        if (!map.TryGetValue(key, out var entries))
                        {
                            entries = [];
                            map[key] = entries;
                        }

                        entries.Add((index.Name, extracted.LiteralText));
                    }
                }

                _literalEqualityFilteredIndexesByColumn = map;
                return map;
            }
        }

        private bool _procedureHasWithRecompile;

        private bool _statementHasOptionRecompile;

        private bool HasActiveRecompileGuard => _procedureHasWithRecompile || _statementHasOptionRecompile;

        private IReadOnlyList<CatalogColumn?>? _pendingInsertTargetColumns;

        private string? _pendingInsertTargetTable;

        public override void ExplicitVisit(SelectStatement node)
        {

            PushCteScope(node.WithCtesAndXmlNamespaces);
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            ScopeStack.Push(FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext()));

            if (_pendingInsertTargetColumns is { } pendingColumns)
            {
                _pendingInsertTargetColumns = null;
                var pendingTable = _pendingInsertTargetTable!;
                _pendingInsertTargetTable = null;
                AnalyzeSelectListWriteLoss(node.SelectElements, pendingColumns, pendingTable);
            }

            var previousPosition = _position;
            _position = PredicatePosition.NotSeekable;

            var previousNegated = _negated;
            _negated = false;

            node.FromClause?.Accept(this);
            foreach (var element in node.SelectElements)
            {
                element.Accept(this);
            }

            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.HavingClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            node.WindowClause?.Accept(this);

            _negated = previousNegated;
            _position = previousPosition;
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _position;
            _position = PredicatePosition.Seekable;
            DeadPredicateStack.Push(ComputeDeadPredicates(node.SearchCondition));
            node.AcceptChildren(this);
            DeadPredicateStack.Pop();
            _position = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _position;
            _position = PredicatePosition.Seekable;
            DeadPredicateStack.Push(ComputeDeadPredicates(node.SearchCondition));
            node.AcceptChildren(this);
            DeadPredicateStack.Pop();
            _position = previous;
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _position;
            _position = PredicatePosition.Seekable;
            DeadPredicateStack.Push(ComputeDeadPredicates(node.SearchCondition));
            node.SearchCondition?.Accept(this);
            DeadPredicateStack.Pop();
            _position = previous;
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            var spec = node.InsertSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);

            node.WithCtesAndXmlNamespaces?.Accept(this);
            AnalyzeInsertWriteLoss(spec);
            spec.Accept(this);

            PopCteScope();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            node.Target?.Accept(this);
            node.TableReference?.Accept(this);
            node.TopRowFilter?.Accept(this);

            var previousPosition = _position;
            _position = PredicatePosition.Seekable;
            node.SearchCondition?.Accept(this);
            _position = previousPosition;

            foreach (var actionClause in node.ActionClauses)
            {
                actionClause.Accept(this);
            }

            node.OutputClause?.Accept(this);
            node.OutputIntoClause?.Accept(this);
        }

        public override void ExplicitVisit(MergeActionClause node)
        {
            var previousPosition = _position;
            _position = PredicatePosition.Seekable;
            node.SearchCondition?.Accept(this);
            _position = previousPosition;

            node.Action?.Accept(this);
        }

        public override void ExplicitVisit(AssignmentSetClause node)
        {
            if (node.Column is { } columnRef)
            {
                var scopeChain = CurrentScopeChain();
                if (ResolveOperand(columnRef, scopeChain) is PredicateOperand.Column target && target.Type is { } targetType)
                {
                    var sourceType = OperandType(ResolveOperand(node.NewValue, scopeChain));
                    EmitWriteLossFinding(target.TableQualifiedName, target.ColumnName, targetType, sourceType, node.NewValue);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.AssignmentKind == AssignmentKind.Equals && node.Expression is { } sourceExpression
                && _variables.TryGetValue(node.Variable.Name, out var targetType) && targetType is { } target)
            {
                var scopeChain = CurrentScopeChain();
                var sourceType = OperandType(ResolveOperand(sourceExpression, scopeChain));
                EmitWriteLossFinding(tableQualifiedName: null, node.Variable.Name, target, sourceType, sourceExpression);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TSqlBatch node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
            if (externalVariables is not null)
            {
                foreach (var (name, type) in externalVariables)
                {
                    _variables[name] = type;
                    _formalParameterNames.Add(name);
                }
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variables[declaration.VariableName.Value] = SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanNotExpression node)
        {
            _negated = !_negated;
            node.AcceptChildren(this);
            _negated = !_negated;
        }

        public override void ExplicitVisit(SearchedCaseExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(SimpleCaseExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(IIfCall node) => EnterOperandPosition(node);

        public override void ExplicitVisit(CoalesceExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(NullIfExpression node) => EnterOperandPosition(node);

        private bool BeginStatementOptimizerHints(IList<OptimizerHint> hints)
        {
            var previous = _statementHasOptionRecompile;
            _statementHasOptionRecompile = hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);
            return previous;
        }

        private void EnterOperandPosition(TSqlFragment node)
        {
            var previousPosition = _position;
            _position = previousPosition == PredicatePosition.Seekable ? PredicatePosition.SuppressedOperand : PredicatePosition.NotSeekable;
            node.AcceptChildren(this);
            _position = previousPosition;
        }

        private bool SkipIfNotSeekable(TSqlFragment node)
        {
            if (_position == PredicatePosition.Seekable)
            {
                return false;
            }

            if (_position == PredicatePosition.SuppressedOperand)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
            }

            return true;
        }

        private void AnalyzeInsertWriteLoss(InsertSpecification spec)
        {
            var table = ResolveWriteTargetTable(spec.Target);
            if (table is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, spec.Target.StartLine, spec.Target.StartColumn,
                    WriteTargetConstructKind, "INSERT target does not resolve to a known table - write-loss analysis skipped");
                return;
            }

            var targetColumns = ResolveInsertTargetColumns(spec, table);

            switch (spec.InsertSource)
            {
                case ValuesInsertSource values:
                    AnalyzeValuesInsertSource(values, targetColumns, table.QualifiedName);
                    return;

                case SelectInsertSource { Select: QuerySpecification querySpec }
                    when querySpec.SelectElements.All(e => e is SelectScalarExpression):
                    _pendingInsertTargetColumns = targetColumns;
                    _pendingInsertTargetTable = table.QualifiedName;
                    return;

                default:
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, spec.StartLine, spec.StartColumn,
                        WriteSourceConstructKind, $"INSERT source of kind '{spec.InsertSource.GetType().Name}' is not analyzed for write-loss - only a plain VALUES list or a single non-UNION SELECT with an explicit scalar select list is");
                    return;
            }
        }

        private CatalogTable? ResolveWriteTargetTable(TableReference target)
        {
            if (target is not NamedTableReference named)
            {
                return null;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            var table = catalog.Find(qualifiedName, CurrentProcScope);

            if (table is null && CurrentProcScope is not null
                && callerScopeByCalleeScope is not null
                && callerScopeByCalleeScope.TryGetValue(CurrentProcScope, out var callerScopes))
            {
                table = FromScopeResolver.TryResolveFromCallerScopes(catalog, qualifiedName, callerScopes);
            }

            return table;
        }

        private List<CatalogColumn?> ResolveInsertTargetColumns(InsertSpecification spec, CatalogTable table)
        {
            if (spec.Columns.Count == 0)
            {
                return [.. table.Columns];
            }

            var resolved = new List<CatalogColumn?>(spec.Columns.Count);
            foreach (var columnRef in spec.Columns)
            {
                var name = columnRef.MultiPartIdentifier.Identifiers[^1].Value;
                var column = table.FindColumn(name, catalog.IdentifierComparer);
                if (column is null)
                {
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                        WriteTargetConstructKind, $"INSERT target column '{name}' does not resolve on table '{table.QualifiedName}' - write-loss analysis skipped for this column");
                }

                resolved.Add(column);
            }

            return resolved;
        }

        private void AnalyzeValuesInsertSource(ValuesInsertSource values, List<CatalogColumn?> targetColumns, string targetTableQualifiedName)
        {
            var scopeChain = CurrentScopeChain();
            foreach (var columnValues in values.RowValues.Select(row => row.ColumnValues))
            {
                var count = Math.Min(columnValues.Count, targetColumns.Count);
                for (var i = 0; i < count; i++)
                {

                    if (targetColumns[i]?.Type is not { } targetType || columnValues[i] is DefaultLiteral)
                    {
                        continue;
                    }

                    var sourceExpression = columnValues[i];
                    var sourceType = OperandType(ResolveOperand(sourceExpression, scopeChain));
                    EmitWriteLossFinding(targetTableQualifiedName, targetColumns[i]!.Name, targetType, sourceType, sourceExpression);
                }
            }
        }

        private void AnalyzeSelectListWriteLoss(IList<SelectElement> selectElements, IReadOnlyList<CatalogColumn?> targetColumns, string targetTableQualifiedName)
        {
            var scopeChain = CurrentScopeChain();
            var count = Math.Min(selectElements.Count, targetColumns.Count);
            for (var i = 0; i < count; i++)
            {

                var sourceExpression = ((SelectScalarExpression)selectElements[i]).Expression;
                if (targetColumns[i]?.Type is not { } targetType)
                {
                    continue;
                }

                var sourceType = OperandType(ResolveOperand(sourceExpression, scopeChain));
                EmitWriteLossFinding(targetTableQualifiedName, targetColumns[i]!.Name, targetType, sourceType, sourceExpression);
            }
        }

        private void EmitWriteLossFinding(string? tableQualifiedName, string columnName, SqlType targetType, SqlType? sourceType, ScalarExpression sourceExpression)
        {
            var kind = Rules.WriteLossClassifier.Classify(targetType, sourceType, sourceExpression, isVariableTarget: tableQualifiedName is null);
            if (kind is null)
            {
                return;
            }

            WriteLossFindings.Add(new WriteLossFinding(
                tableQualifiedName, columnName, kind.Value, targetType, sourceType!,
                sourcePath, sourceExpression.StartLine, sourceExpression.StartColumn));
        }

        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
            RecordParameters(node.Parameters);

            _previousProcedureHasWithRecompile = _procedureHasWithRecompile;
            _procedureHasWithRecompile = node is ProcedureStatementBody { Options: { } options }
                && options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile);
        }

        protected override void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node) =>
            _procedureHasWithRecompile = _previousProcedureHasWithRecompile;

        private bool _previousProcedureHasWithRecompile;

        protected override void OnEnterTriggerBody(TriggerStatementBody node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (IsDeadPredicate(node))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            var operatorText = ToOperatorText(node.ComparisonType);
            if (operatorText is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison operator", $"unrecognized comparison operator '{node.ComparisonType}'");
                return;
            }

            TryAddFinding(node.FirstExpression, node.SecondExpression, _negated ? Negate(operatorText) : operatorText, node);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            if (node.TernaryExpressionType is not (BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween))
            {
                return;
            }

            if (IsDeadPredicate(node))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            var isNotBetween = node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween || _negated;
            if (isNotBetween)
            {
                if (ScopeStack.Count > 0 && _position == PredicatePosition.Seekable)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT BETWEEN is not sargable regardless of type match - not attributed to a type-conversion verdict");
                }
                else if (_position == PredicatePosition.SuppressedOperand)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
                }

                return;
            }

            TryAddFinding(node.FirstExpression, node.SecondExpression, ">=", node);
            TryAddFinding(node.FirstExpression, node.ThirdExpression, "<=", node);
        }

        public override void Visit(LikePredicate node)
        {
            if (IsDeadPredicate(node))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            if (node.NotDefined || _negated)
            {

                if (ScopeStack.Count > 0 && _position == PredicatePosition.Seekable)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT LIKE is not sargable regardless of type match - not attributed to a type-conversion verdict");
                }
                else if (_position == PredicatePosition.SuppressedOperand)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
                }

                return;
            }

            TryAddFinding(node.FirstExpression, node.SecondExpression, "LIKE", node);
        }

        public override void Visit(InPredicate node)
        {
            if (IsDeadPredicate(node))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            if (ScopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (SkipIfNotSeekable(node))
            {
                return;
            }

            if (node.NotDefined || _negated)
            {

                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT IN is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            var scopeChain = CurrentScopeChain();
            _currentPredicateFragment = node;
            if (ResolveOperand(node.Expression, scopeChain) is not PredicateOperand.Column column)
            {

                return;
            }

            var otherType = node.Subquery is not null
                ? ResolveInSubqueryType(node.Subquery)
                : CombineListElementTypes(node.Values, scopeChain);

            if (otherType is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "IN predicate", "list contains a non-literal/unresolvable element, or the subquery's output column type could not be resolved");
                return;
            }

            var verdict = VerdictClassifier.Classify(column.Type, otherType, operatorText: "IN");
            Findings.Add(new TypedPredicateFinding(verdict, column, new PredicateOperand.Value(otherType), "IN", sourcePath, node.StartLine, node.StartColumn));
        }

        public override void Visit(SubqueryComparisonPredicate node)
        {
            if (IsDeadPredicate(node))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            if (ScopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (SkipIfNotSeekable(node))
            {
                return;
            }

            var isAnyEquals = node.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.Any && node.ComparisonType == BooleanComparisonType.Equals;
            var isAllNotEquals = node.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.All
                && node.ComparisonType is BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation;

            if (_negated || (!isAnyEquals && !isAllNotEquals))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "subquery comparison predicate", $"'{node.ComparisonType} {node.SubqueryComparisonPredicateType}' is not modeled - only '= ANY/SOME' and '<> ALL' are (the IN/NOT IN equivalents)");
                return;
            }

            if (isAllNotEquals)
            {

                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "<> ALL is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            var scopeChain = CurrentScopeChain();
            _currentPredicateFragment = node;
            if (ResolveOperand(node.Expression, scopeChain) is not PredicateOperand.Column column)
            {
                return;
            }

            var otherType = ResolveInSubqueryType(node.Subquery);
            if (otherType is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "subquery comparison predicate", "the subquery's output column type could not be resolved");
                return;
            }

            var verdict = VerdictClassifier.Classify(column.Type, otherType, operatorText: "IN");
            Findings.Add(new TypedPredicateFinding(verdict, column, new PredicateOperand.Value(otherType), "IN", sourcePath, node.StartLine, node.StartColumn));
        }

        public override void Visit(BooleanIsNullExpression node)
        {
            _ = node;
        }

        private static string? ToOperatorText(BooleanComparisonType comparisonType) => comparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.NotGreaterThan => "!>",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.NotLessThan => "!<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets => "<>",
            BooleanComparisonType.NotEqualToExclamation => "<>",
            _ => null,
        };

        private static string Negate(string operatorText) => operatorText switch
        {
            "=" => "<>",
            "<>" => "=",
            ">" => "<=",
            "<" => ">=",
            ">=" => "<",
            "<=" => ">",
            "!<" => "<",
            "!>" => ">",
            _ => operatorText,
        };

        private void RecordParameters(IList<ProcedureParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                _variables[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
                _formalParameterNames.Add(parameter.VariableName.Value);
            }
        }

        private void TryAddFinding(ScalarExpression first, ScalarExpression second, string operatorText, TSqlFragment node)
        {
            if (ScopeStack.Count == 0)
            {

                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (SkipIfNotSeekable(node))
            {
                return;
            }

            if (operatorText == "<>")
            {

                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "<> is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            var scopeChain = CurrentScopeChain();
            _currentPredicateFragment = node;
            var left = ResolveOperand(first, scopeChain);
            var right = ResolveOperand(second, scopeChain);

            if (left is PredicateOperand.Column leftColumn && right is PredicateOperand.Column rightColumn)
            {

                if (TryRecordCollationConflict(leftColumn, rightColumn, operatorText, node))
                {
                    return;
                }

                AddFinding(leftColumn, rightColumn, operatorText, node);
                AddFinding(rightColumn, leftColumn, operatorText, node);
                return;
            }

            PredicateOperand.Column? column;
            PredicateOperand? other;
            if (left is PredicateOperand.Column singleLeftColumn)
            {
                (column, other) = (singleLeftColumn, right);
            }
            else if (right is PredicateOperand.Column singleRightColumn)
            {
                (column, other) = (singleRightColumn, left);
            }
            else
            {
                (column, other) = (null, null);
            }

            if (column is null || other is null)
            {
                RecordNoColumnOperand(first, second, node);
                return;
            }

            AddFinding(column, other, operatorText, node);
        }

        private void RecordNoColumnOperand(ScalarExpression first, ScalarExpression second, TSqlFragment node)
        {
            if (first is ColumnReferenceExpression || second is ColumnReferenceExpression)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, UnresolvedColumnComparisonConstructKind,
                    "at least one side of this comparison is a bare column reference that failed to resolve to a real column (most commonly an unresolved FROM-scope alias) - not the benign no-column-operand shape");
                return;
            }

            if (node is BooleanComparisonExpression comparison
                && LiteralComparisonFolder.TryFoldComparison(first, second, comparison.ComparisonType) is { } truth)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, FoldableLiteralComparisonConstructKind,
                    $"both sides are literals (optionally with one level of arithmetic) provably {(truth ? "always true" : "always false")} - not an arbitrary unresolvable comparison");
                return;
            }

            ledger.Record(
                AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NoColumnOperandConstructKind,
                "neither side of this comparison resolved to a real column - most commonly both sides are expressions (e.g. a column wrapped in COALESCE/CASE/NULLIF/IIF compared to a literal)");
        }

        private void AddFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            var otherIsLiteral = other is PredicateOperand.Value { IsLiteral: true };
            var otherType = other is PredicateOperand.Value value ? value.Type : ((PredicateOperand.Column)other).Type;
            var (verdict, unknownReason) = VerdictClassifier.ClassifyWithReason(column.Type, otherType, otherIsLiteral, operatorText);

            Findings.Add(new TypedPredicateFinding(
                verdict, column, other, operatorText, sourcePath, node.StartLine, node.StartColumn,
                UnknownReason: unknownReason,
                PredicateFragmentText: _currentPredicateFragment is { } fragment ? Common.FragmentTextRenderer.Render(fragment) : null,
                Fingerprint: TypedPredicateFindingIdentity.ComputeFingerprint(column, other, operatorText)));

            TryAddOversizedParameterFinding(column, other, otherIsLiteral, node);
            TryAddUnderLengthParameterFinding(column, other, otherIsLiteral, operatorText, node);
            TryAddAnsiPaddingMismatchFinding(column, other, operatorText, node);
            TryAddLocalVariablePredicateFinding(column, other, operatorText, node);
            TryAddFilteredIndexParameterMismatchFinding(column, other, operatorText, node);
        }

        private void TryAddLocalVariablePredicateFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (HasActiveRecompileGuard || other is not PredicateOperand.Value { VariableName: { } variableName, IsFormalParameter: false })
            {
                return;
            }

            LocalVariablePredicateFindings.Add(new LocalVariablePredicateFinding(
                column.TableQualifiedName, column.ColumnName, column.Indexed, column.Depth,
                variableName, operatorText, sourcePath, node.StartLine, node.StartColumn));
        }

        private void TryAddFilteredIndexParameterMismatchFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (other is not PredicateOperand.Value { VariableName: { } variableName, IsFormalParameter: var isFormalParameter }
                || !LiteralEqualityFilteredIndexesByColumn.TryGetValue((column.TableQualifiedName, column.ColumnName), out var candidates))
            {
                return;
            }

            foreach (var (indexName, literalText) in candidates)
            {
                FilteredIndexParameterMismatchFindings.Add(new FilteredIndexParameterMismatchFinding(
                    column.TableQualifiedName, column.ColumnName, indexName, literalText,
                    variableName, isFormalParameter, operatorText, sourcePath, node.StartLine, node.StartColumn));
            }
        }

        private void TryAddOversizedParameterFinding(PredicateOperand.Column column, PredicateOperand other, bool otherIsLiteral, TSqlFragment node)
        {
            if (otherIsLiteral || other is not PredicateOperand.Value { Type: { } otherType }
                || Rules.ParameterLengthClassifier.ClassifyOversized(column.Type, otherType) is not { } result)
            {
                return;
            }

            OversizedParameterFindings.Add(new OversizedParameterFinding(
                column.TableQualifiedName, column.ColumnName, result.ColumnLength, result.OtherLength, sourcePath, node.StartLine, node.StartColumn));
        }

        private void TryAddUnderLengthParameterFinding(
            PredicateOperand.Column column, PredicateOperand other, bool otherIsLiteral, string operatorText, TSqlFragment node)
        {
            if (otherIsLiteral || other is not PredicateOperand.Value { Type: { } otherType }
                || Rules.ParameterLengthClassifier.ClassifyUnderLength(column.Type, otherType) is not { } result)
            {
                return;
            }

            var changesRangeOrPatternShape = Rules.ParameterLengthClassifier.ChangesRangeOrPatternShape(operatorText);

            UnderLengthParameterFindings.Add(new UnderLengthParameterFinding(
                column.TableQualifiedName, column.ColumnName, result.ColumnLength, result.OtherLength, result.IsImplicitDefault,
                operatorText, changesRangeOrPatternShape, sourcePath, node.StartLine, node.StartColumn));
        }

        private void TryAddAnsiPaddingMismatchFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (operatorText != "LIKE"
                || column.Type is not { Category: SqlTypeCategory.VarChar or SqlTypeCategory.VarBinary }
                || other is not PredicateOperand.Value { IsLiteral: true, LiteralText: { } literalText }
                || !LiteralEndsWithSignificantWhitespace(literalText))
            {
                return;
            }

            var catalogColumn = catalog.Find(column.TableQualifiedName, CurrentProcScope)?.FindColumn(column.ColumnName, catalog.IdentifierComparer);
            if (catalogColumn is not { IsAnsiPadded: false })
            {
                return;
            }

            AnsiPaddingMismatchFindings.Add(new AnsiPaddingMismatchFinding(
                column.TableQualifiedName, column.ColumnName, literalText, sourcePath, node.StartLine, node.StartColumn));
        }

        private static bool LiteralEndsWithSignificantWhitespace(string literalText)
        {
            var firstQuote = literalText.IndexOf('\'');
            var lastQuote = literalText.LastIndexOf('\'');
            if (firstQuote < 0 || lastQuote <= firstQuote)
            {
                return false;
            }

            var content = literalText[(firstQuote + 1)..lastQuote];
            return content.Length > 0 && char.IsWhiteSpace(content[^1]);
        }

        private bool TryRecordCollationConflict(PredicateOperand.Column first, PredicateOperand.Column second, string operatorText, TSqlFragment node)
        {
            if (!Rules.VerdictClassifier.HasGenuineCollationMismatch(first.Type, second.Type)
                || first.Type?.Collation is not { } firstCollation || second.Type?.Collation is not { } secondCollation)
            {
                return false;
            }

            CollationConflictFindings.Add(new CollationConflictFinding(
                first.TableQualifiedName, first.ColumnName, firstCollation.Name,
                second.TableQualifiedName, second.ColumnName, secondCollation.Name,
                operatorText, sourcePath, node.StartLine, node.StartColumn));
            return true;
        }

        private PredicateOperand ResolveOperand(
            ScalarExpression expression, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            switch (expression)
            {
                case ColumnReferenceExpression columnRef:
                    return ResolveColumnOperand(columnRef, scopeChain);

                case VariableReference variableRef:
                    return new PredicateOperand.Value(
                        _variables.GetValueOrDefault(variableRef.Name), VariableName: variableRef.Name,
                        IsFormalParameter: _formalParameterNames.Contains(variableRef.Name));

                case Literal literal:
                    return new PredicateOperand.Value(TypeInference.LiteralTypeResolver.Resolve(literal), IsLiteral: true, Rules.LiteralTextRenderer.Render(literal));

                case GlobalVariableExpression globalVariable:
                    return ResolveGlobalVariableOperand(globalVariable);

                case FunctionCall functionCall:
                    return ResolveFunctionCallOperand(functionCall, scopeChain);

                case CastCall castCall:
                    return ResolveCastOrConvertOperand(castCall.DataType, castCall.Parameter, scopeChain, castCall);

                case ConvertCall convertCall:
                    return ResolveCastOrConvertOperand(convertCall.DataType, convertCall.Parameter, scopeChain, convertCall);

                case ScalarSubquery scalarSubquery:
                    return new PredicateOperand.Value(ResolveInSubqueryType(scalarSubquery));

                case ParenthesisExpression or UnaryExpression or BinaryExpression
                    or CoalesceExpression or NullIfExpression or IIfCall
                    or SearchedCaseExpression or SimpleCaseExpression:
                    return new PredicateOperand.Value(
                        ExpressionTypeInferencer.Resolve(expression, e => OperandType(ResolveOperand(e, scopeChain)), catalog.TypeAliases));

                default:

                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, expression.StartLine, expression.StartColumn,
                        PredicateOperandConstructKind, $"operand of kind '{expression.GetType().Name}' has no type resolution - resolved Unknown");
                    return new PredicateOperand.Value(Type: null);
            }
        }

        private static SqlType? OperandType(PredicateOperand operand) => operand switch
        {
            PredicateOperand.Value v => v.Type,
            PredicateOperand.Column c => c.Type,
            _ => null,
        };

        private PredicateOperand.Value ResolveGlobalVariableOperand(GlobalVariableExpression globalVariable)
        {
            var type = TypeInference.BuiltinFunctionTypeResolver.ResolveGlobalVariable(globalVariable.Name);
            if (type is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, globalVariable.StartLine, globalVariable.StartColumn,
                    PredicateOperandConstructKind, $"global variable '{globalVariable.Name}' has no type resolution - resolved Unknown");
            }

            return new PredicateOperand.Value(type);
        }

        private PredicateOperand.Value ResolveFunctionCallOperand(
            FunctionCall functionCall, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var name = functionCall.FunctionName.Value;

            if (TypeInference.BuiltinFunctionTypeResolver.TryGetArgumentTypeIndex(name) is { } argumentIndex && functionCall.Parameters.Count > argumentIndex)
            {
                var argumentType = OperandType(ResolveOperand(functionCall.Parameters[argumentIndex], scopeChain));
                argumentType = TypeInference.BuiltinFunctionTypeResolver.AdjustArgumentTypeFunctionResult(name, argumentType);
                return new PredicateOperand.Value(argumentType);
            }

            var fixedType = TypeInference.BuiltinFunctionTypeResolver.ResolveFixedReturnType(name);
            if (fixedType is not null)
            {
                return new PredicateOperand.Value(fixedType);
            }

            var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(functionCall);
            if (catalog.TryGetScalarFunctionReturnType(qualifiedName, out var udfType))
            {
                if (udfType is null)
                {
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                        PredicateOperandConstructKind, $"function '{qualifiedName}' RETURNS type could not be resolved - resolved Unknown");
                }

                return new PredicateOperand.Value(udfType);
            }

            ledger.Record(
                AnalysisPass.Predicates, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                PredicateOperandConstructKind, $"function '{name}' has no return-type resolution - resolved Unknown");

            return new PredicateOperand.Value(Type: null);
        }

        private PredicateOperand.Value ResolveCastOrConvertOperand(
            DataTypeReference dataType, ScalarExpression parameter,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            TSqlFragment node)
        {

            var type = Parsing.SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases, unsizedStringOrBinaryDefaultLength: 30);
            if (type is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                    PredicateOperandConstructKind, "CAST/CONVERT target type could not be resolved - resolved Unknown");
                return new PredicateOperand.Value(Type: null);
            }

            if (type.IsStringFamily)
            {
                var innerType = OperandType(ResolveOperand(parameter, scopeChain));

                if (innerType is { IsStringFamily: true, Collation: { } innerCollation })
                {
                    type = type with { Collation = innerCollation };
                }
            }

            return new PredicateOperand.Value(type);
        }

        private SqlType? CombineListElementTypes(
            IList<ScalarExpression> values, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            SqlType? best = null;
            foreach (var value in values)
            {
                var type = OperandType(ResolveOperand(value, scopeChain));

                if (type is null)
                {
                    return null;
                }

                if (best is null || type.Category > best.Category)
                {
                    best = type;
                }
            }

            return best;
        }

        private SqlType? ResolveInSubqueryType(ScalarSubquery subquery)
        {
            var innerCtes = CurrentCteRelations();
            var columns = QueryExpressionResolver.Resolve(subquery.QueryExpression, catalog, resolvedViews, sourcePath, ledger, innerCtes, CurrentProcScope);
            if (columns.Count != 1)
            {

                return null;
            }

            return ColumnProvenanceAnalysis.TryGetScalarType(columns[0].Provenance);
        }

        private PredicateOperand ResolveColumnOperand(
            ColumnReferenceExpression columnRef, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger, catalog);
            var columnName = columnRef.MultiPartIdentifier.Identifiers[^1].Value;

            if (provenance is ColumnProvenance.BaseColumn baseColumn)
            {

                var tableEntry = catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope);
                var matchedIndex = tableEntry?.FindIndexedColumn(baseColumn.ColumnName, catalog.IdentifierComparer);

                bool? indexed = tableEntry is null ? null : matchedIndex is not null;
                var immediateRelation = ScalarExpressionResolver.TryResolveImmediateRelation(columnRef, scopeChain, catalog);
                return new PredicateOperand.Column(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, indexed, baseColumn.Depth, baseColumn,
                    immediateRelation?.RelationQualifiedName, immediateRelation?.ExposedColumnName, matchedIndex?.Name);
            }

            if (provenance is ColumnProvenance.Declared declared)
            {

                return new PredicateOperand.Column(declared.TableQualifiedName ?? "?", columnName, declared.Type, Indexed: false, declared.Depth, declared);
            }

            if (ColumnProvenanceAnalysis.IsExpressionDerived(provenance))
            {
                RecordExpressionDerivedFinding(columnName, columnRef, provenance, scopeChain);
            }
            else if (provenance is ColumnProvenance.Union union)
            {

                var agreedType = ColumnProvenanceAnalysis.TryGetScalarType(union);
                if (agreedType is not null)
                {
                    return new PredicateOperand.Column("?", columnName, agreedType, Indexed: false, Depth: 0, union);
                }

                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                    PredicateOperandConstructKind, $"column '{columnName}' resolves through a UNION view whose branches disagree on type - not eligible for a single verdict, never guessed");
            }

            return new PredicateOperand.Value(Type: null);
        }

        private void RecordExpressionDerivedFinding(
            string columnName, ColumnReferenceExpression columnRef, ColumnProvenance provenance,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var underlyingBaseColumns = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(provenance)
                .Select(bc => new UnderlyingBaseColumn(bc.TableQualifiedName, bc.ColumnName, catalog.Find(bc.TableQualifiedName, CurrentProcScope)?.IsIndexedColumn(bc.ColumnName, catalog.IdentifierComparer) ?? false))
                .ToList();

            if (underlyingBaseColumns.Count == 0)
            {

                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                    "expression-derived predicate", $"'{columnName}' is expression-derived but no underlying base column could be traced (e.g. ROW_NUMBER(), a derived-table alias over another expression, an XML shred) - nothing actionable to report");
                return;
            }

            var transformationChain = ColumnProvenanceAnalysis.DescribeTransformationChain(provenance);

            var immediateRelation = ScalarExpressionResolver.TryResolveImmediateRelation(columnRef, scopeChain, catalog);
            var identifiers = columnRef.MultiPartIdentifier.Identifiers;
            var alias = identifiers.Count >= 2 ? identifiers[^2].Value : null;

            ExpressionDerivedFindings.Add(new ExpressionDerivedFinding(
                columnName, sourcePath, columnRef.StartLine, columnRef.StartColumn, transformationChain, underlyingBaseColumns,
                PredicateFragmentText: _currentPredicateFragment is { } fragment ? Common.FragmentTextRenderer.Render(fragment) : null,
                ImmediateRelationQualifiedName: immediateRelation?.RelationQualifiedName,
                ImmediateRelationAlias: immediateRelation is not null ? alias : null));
        }
    }
}
