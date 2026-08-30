using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class CatchAllPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CatchAllPredicateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<CatchAllPredicateFinding> Findings { get; } = [];

        private readonly HashSet<string> _formalParameterNames = new(StringComparer.OrdinalIgnoreCase);

        private bool _procedureHasWithRecompile;

        private bool _statementHasOptionRecompile;

        private bool HasActiveRecompileGuard => _procedureHasWithRecompile || _statementHasOptionRecompile;

        private HashSet<string> _previousFormalParameterNames = new(StringComparer.OrdinalIgnoreCase);

        private bool _previousProcedureHasWithRecompile;

        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            _previousFormalParameterNames = new HashSet<string>(_formalParameterNames, StringComparer.OrdinalIgnoreCase);
            _previousProcedureHasWithRecompile = _procedureHasWithRecompile;

            _formalParameterNames.Clear();
            foreach (var parameter in node.Parameters)
            {
                _formalParameterNames.Add(parameter.VariableName.Value);
            }

            _procedureHasWithRecompile = node is ProcedureStatementBody { Options: { } options }
                && options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile);
        }

        protected override void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            _formalParameterNames.Clear();
            foreach (var name in _previousFormalParameterNames)
            {
                _formalParameterNames.Add(name);
            }

            _procedureHasWithRecompile = _previousProcedureHasWithRecompile;
        }

        protected override void OnEnterTriggerBody(TriggerStatementBody node)
        {
            _formalParameterNames.Clear();
            _procedureHasWithRecompile = false;
        }

        protected override void OnSelectStatementScope(SelectStatement node, Action continueDescent)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            continueDescent();
            _statementHasOptionRecompile = previous;
        }

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
            if (!HasActiveRecompileGuard)
            {
                InspectAllPredicateLocations(node, scopeChain, (condition, _) => InspectSearchCondition(condition));
            }

            continueDescent();
        }

        protected override void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            if (!HasActiveRecompileGuard)
            {
                InspectAllPredicateLocations(node, scopeChain, (condition, _) => InspectSearchCondition(condition));
            }

            continueDescent();
            _statementHasOptionRecompile = previous;
        }

        protected override void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            if (!HasActiveRecompileGuard)
            {
                InspectAllPredicateLocations(node, scopeChain, (condition, _) => InspectSearchCondition(condition));
            }

            continueDescent();
            _statementHasOptionRecompile = previous;
        }

        protected override void OnMergeStatementScope(MergeStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            continueDescent();
            _statementHasOptionRecompile = previous;
        }

        private bool BeginStatementOptimizerHints(IList<OptimizerHint> hints)
        {
            var previous = _statementHasOptionRecompile;
            _statementHasOptionRecompile = hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);
            return previous;
        }

        private void InspectSearchCondition(BooleanExpression? searchCondition)
        {
            if (searchCondition is null)
            {
                return;
            }

            var scopeChain = CurrentScopeChain();
            var dead = PredicateSurvivalAnalyzer.FindDeadComparisons(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain));

            foreach (var orClause in FlattenOr(searchCondition))
            {
                InspectOrClause(orClause, scopeChain, dead);
            }
        }

        private static IEnumerable<IReadOnlyList<BooleanExpression>> FlattenOr(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    foreach (var group in FlattenOr(and.FirstExpression))
                    {
                        yield return group;
                    }

                    foreach (var group in FlattenOr(and.SecondExpression))
                    {
                        yield return group;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var group in FlattenOr(paren.Expression))
                    {
                        yield return group;
                    }

                    break;

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }:
                    yield return [.. FlattenOrLeaves(expression)];
                    break;

                default:
                    break;
            }
        }

        private static IEnumerable<BooleanExpression> FlattenOrLeaves(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or_:
                    foreach (var leaf in FlattenOrLeaves(or_.FirstExpression))
                    {
                        yield return leaf;
                    }

                    foreach (var leaf in FlattenOrLeaves(or_.SecondExpression))
                    {
                        yield return leaf;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var leaf in FlattenOrLeaves(paren.Expression))
                    {
                        yield return leaf;
                    }

                    break;

                default:
                    yield return expression;
                    break;
            }
        }

        private void InspectOrClause(
            IReadOnlyList<BooleanExpression> orLeaves,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            IReadOnlySet<TSqlFragment> dead)
        {

            var isNullVariables = orLeaves
                .OfType<BooleanIsNullExpression>()
                .Where(n => !n.IsNot)
                .Select(n => n.Expression as VariableReference)
                .Where(v => v is not null)
                .Select(v => v!.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var equality in orLeaves.OfType<BooleanComparisonExpression>().Where(c => c.ComparisonType == BooleanComparisonType.Equals && !dead.Contains(c)))
            {
                TryMatchCatchAllPair(equality.FirstExpression, equality.SecondExpression, isNullVariables, scopeChain, equality);
                TryMatchCatchAllPair(equality.SecondExpression, equality.FirstExpression, isNullVariables, scopeChain, equality);
            }
        }

        private void TryMatchCatchAllPair(
            ScalarExpression columnSide, ScalarExpression variableSide, HashSet<string> isNullVariables,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            TSqlFragment node)
        {
            if (columnSide is not ColumnReferenceExpression columnRef
                || variableSide is not VariableReference variableRef
                || !isNullVariables.Contains(variableRef.Name)
                || !_formalParameterNames.Contains(variableRef.Name))
            {
                return;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null, catalog);
            if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
            {
                return;
            }

            var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName, catalog.IdentifierComparer) ?? false;

            Findings.Add(new CatchAllPredicateFinding(
                baseColumn.TableQualifiedName, baseColumn.ColumnName, indexed, variableRef.Name,
                sourcePath, node.StartLine, node.StartColumn));
        }
    }
}
