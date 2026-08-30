using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class ParameterReassignmentPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ParameterReassignmentPredicateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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

    private readonly record struct FlowState(HashSet<string>? Reassigned, Dictionary<string, TSqlFragment>? ReassignmentSites, bool Declined)
    {
        public static FlowState Declined_() => new(null, null, true);
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null), IStatementFlowPolicy<FlowState>
#pragma warning restore CS9107
    {
        public List<ParameterReassignmentPredicateFinding> Findings { get; } = [];

        private bool _procedureHasWithRecompile;

        private HashSet<string> _formalParameterNames = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            var hasWithRecompile = node switch
            {
                CreateProcedureStatement p => p.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile),
                AlterProcedureStatement p => p.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile),
                CreateOrAlterProcedureStatement p => p.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile),
                _ => false,
            };

            AnalyzeProcedure(node.Parameters, node.StatementList, hasWithRecompile);
        }

        private void AnalyzeProcedure(IList<ProcedureParameter> parameters, StatementList? statementList, bool hasWithRecompile)
        {
            _formalParameterNames = new HashSet<string>(parameters.Select(p => p.VariableName.Value), StringComparer.OrdinalIgnoreCase);
            if (_formalParameterNames.Count == 0 || statementList is null)
            {
                return;
            }

            _procedureHasWithRecompile = hasWithRecompile;

            var statements = statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;

            var entryState = new FlowState([], new Dictionary<string, TSqlFragment>(StringComparer.OrdinalIgnoreCase), false);
            ProcedureBodyFlowWalker.Walk(statements, entryState, this);
        }

        public bool IsDeclined(FlowState state) => state.Declined;

        public bool IsDone(FlowState state) => false;

        public FlowState PerStatement(TSqlStatement statement, FlowState state)
        {
            if (!_procedureHasWithRecompile)
            {
                InspectStatementForFindings(statement, state);
            }

            foreach (var (name, site) in VariableWriteSites.InStatement(statement))
            {
                Reassign(state, name, site);
            }

            return state;
        }

        public FlowState OnReturn(FlowState state, TSqlStatement statement) => state with { Reassigned = [] };

        public FlowState OnThrow(FlowState state) => state with { Reassigned = [] };

        public FlowState OnGoTo(FlowState state) => FlowState.Declined_();

        public FlowState CloneForBranch(FlowState state) => CloneState(state);

        public FlowState Merge(FlowState a, FlowState b) => IntersectBranches(a, b);

        private void Reassign(FlowState state, string variableName, TSqlFragment site)
        {
            if (!_formalParameterNames.Contains(variableName))
            {

                return;
            }

            state.Reassigned!.Add(variableName);
            state.ReassignmentSites![variableName] = site;
        }

        private void InspectStatementForFindings(TSqlStatement statement, FlowState state)
        {
            if (state.Reassigned!.Count == 0)
            {
                return;
            }

            switch (statement)
            {
                case SelectStatement { QueryExpression: QuerySpecification spec } select when !HasOptionRecompile(select.OptimizerHints):
                    InspectAllPredicateLocations(
                        spec, BuildScopeChain(spec.FromClause, select.WithCtesAndXmlNamespaces), (condition, chain) => InspectSearchConditionCore(condition, chain, state));
                    break;

                case UpdateStatement { UpdateSpecification: { } upd } update when !HasOptionRecompile(update.OptimizerHints):
                    InspectAllPredicateLocations(
                        update, BuildDataModificationScopeChain(upd.Target, upd.FromClause, update.WithCtesAndXmlNamespaces), (condition, chain) => InspectSearchConditionCore(condition, chain, state));
                    break;

                case DeleteStatement { DeleteSpecification: { } del } delete when !HasOptionRecompile(delete.OptimizerHints):
                    InspectAllPredicateLocations(
                        delete, BuildDataModificationScopeChain(del.Target, del.FromClause, delete.WithCtesAndXmlNamespaces), (condition, chain) => InspectSearchConditionCore(condition, chain, state));
                    break;

                default:
                    break;
            }
        }

        private static bool HasOptionRecompile(IList<OptimizerHint> hints) =>
            hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);

        private ScopeChain BuildScopeChain(FromClause? fromClause, WithCtesAndXmlNamespaces? withClause)
        {
            var cteRelations = CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, CurrentProcScope);
            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations, CurrentProcScope);
            return [((IReadOnlyDictionary<string, ScopeEntry>)byAlias, (IReadOnlyList<ScopeEntry>)ordered)];
        }

        private ScopeChain BuildDataModificationScopeChain(TableReference target, FromClause? extraFromClause, WithCtesAndXmlNamespaces? withClause)
        {
            var cteRelations = CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, CurrentProcScope);
            var context = new FromScopeResolver.ResolutionContext(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, CurrentProcScope);
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(target, extraFromClause, context);
            return [((IReadOnlyDictionary<string, ScopeEntry>)byAlias, (IReadOnlyList<ScopeEntry>)ordered)];
        }

        private void InspectSearchConditionCore(BooleanExpression condition, ScopeChain scopeChain, FlowState state)
        {
            foreach (var comparison in FindComparisons(condition))
            {
                TryMatch(comparison.FirstExpression, comparison.SecondExpression, comparison, scopeChain, state);
                TryMatch(comparison.SecondExpression, comparison.FirstExpression, comparison, scopeChain, state);
            }
        }

        private static IEnumerable<BooleanComparisonExpression> FindComparisons(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanComparisonExpression comparison:
                    yield return comparison;
                    break;

                case BooleanBinaryExpression binary:
                    foreach (var c in FindComparisons(binary.FirstExpression))
                    {
                        yield return c;
                    }

                    foreach (var c in FindComparisons(binary.SecondExpression))
                    {
                        yield return c;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var c in FindComparisons(paren.Expression))
                    {
                        yield return c;
                    }

                    break;

                default:
                    break;
            }
        }

        private void TryMatch(
            ScalarExpression columnSide, ScalarExpression variableSide, BooleanComparisonExpression comparison,
            ScopeChain scopeChain,
            FlowState state)
        {
            if (columnSide is not ColumnReferenceExpression columnRef
                || variableSide is not VariableReference variableRef
                || !state.Reassigned!.Contains(variableRef.Name))
            {
                return;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null, catalog);
            if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
            {
                return;
            }

            var operatorText = ToOperatorText(comparison.ComparisonType);
            if (operatorText is null)
            {
                return;
            }

            var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName, catalog.IdentifierComparer) ?? false;
            var site = state.ReassignmentSites![variableRef.Name];

            Findings.Add(new ParameterReassignmentPredicateFinding(
                baseColumn.TableQualifiedName, baseColumn.ColumnName, indexed, variableRef.Name, operatorText,
                site.StartLine, site.StartColumn,
                sourcePath, comparison.StartLine, comparison.StartColumn));
        }

        private static string? ToOperatorText(BooleanComparisonType comparisonType) => comparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets => "<>",
            BooleanComparisonType.NotEqualToExclamation => "<>",
            _ => null,
        };

        private static FlowState CloneState(FlowState state) => state.Declined
            ? state
            : new FlowState([.. state.Reassigned!], new Dictionary<string, TSqlFragment>(state.ReassignmentSites!, StringComparer.OrdinalIgnoreCase), false);

        private static FlowState IntersectBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.Declined_();
            }

            var merged = new HashSet<string>(a.Reassigned!, StringComparer.OrdinalIgnoreCase);
            merged.IntersectWith(b.Reassigned!);

            var sites = new Dictionary<string, TSqlFragment>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in merged)
            {

                sites[name] = b.ReassignmentSites!.TryGetValue(name, out var bSite) ? bSite : a.ReassignmentSites![name];
            }

            return new FlowState(merged, sites, false);
        }
    }
}
