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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<ParameterReassignmentPredicateFinding> Findings { get; } = [];

        private bool _procedureHasWithRecompile;

        private HashSet<string> _formalParameterNames = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile));

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile));

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile));

        public override void ExplicitVisit(CreateFunctionStatement node) => AnalyzeProcedure(node.Parameters, node.StatementList, hasWithRecompile: false);

        public override void ExplicitVisit(AlterFunctionStatement node) => AnalyzeProcedure(node.Parameters, node.StatementList, hasWithRecompile: false);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => AnalyzeProcedure(node.Parameters, node.StatementList, hasWithRecompile: false);

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
            AnalyzeSequential(statements, entryState);
        }

        private FlowState AnalyzeSequential(IList<TSqlStatement> statements, FlowState state)
        {
            foreach (var statement in statements)
            {
                if (state.Declined)
                {
                    return state;
                }

                if (!_procedureHasWithRecompile)
                {
                    InspectStatementForFindings(statement, state);
                }

                switch (statement)
                {
                    case SetVariableStatement set:
                        Reassign(state, set.Variable.Name, set);
                        break;

                    case SelectStatement { QueryExpression: QuerySpecification spec }:
                        foreach (var element in spec.SelectElements.OfType<SelectSetVariable>())
                        {
                            Reassign(state, element.Variable.Name, statement);
                        }

                        break;

                    case ReturnStatement or ThrowStatement:
                        return state with { Reassigned = [] };

                    case GoToStatement:
                        return FlowState.Declined_();

                    case BeginEndBlockStatement block:
                        state = AnalyzeSequential(block.StatementList.Statements, state);
                        break;

                    case IfStatement ifStatement:
                        state = AnalyzeIf(ifStatement, state);
                        break;

                    case WhileStatement whileStatement:
                        state = AnalyzeWhile(whileStatement, state);
                        break;

                    case TryCatchStatement tryCatch:
                        state = AnalyzeTryCatch(tryCatch, state);
                        break;

                    default:
                        break;
                }
            }

            return state;
        }

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
                    InspectSearchCondition(spec.WhereClause?.SearchCondition, spec.FromClause, select.WithCtesAndXmlNamespaces, state);
                    break;

                case UpdateStatement { UpdateSpecification: { } upd } update when !HasOptionRecompile(update.OptimizerHints):
                    InspectSearchCondition(upd.WhereClause?.SearchCondition, upd.Target, upd.FromClause, update.WithCtesAndXmlNamespaces, state);
                    break;

                case DeleteStatement { DeleteSpecification: { } del } delete when !HasOptionRecompile(delete.OptimizerHints):
                    InspectSearchCondition(del.WhereClause?.SearchCondition, del.Target, del.FromClause, delete.WithCtesAndXmlNamespaces, state);
                    break;

                default:
                    break;
            }
        }

        private static bool HasOptionRecompile(IList<OptimizerHint> hints) =>
            hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);

        private void InspectSearchCondition(BooleanExpression? condition, FromClause? fromClause, WithCtesAndXmlNamespaces? withClause, FlowState state)
        {
            if (condition is null || fromClause is null)
            {
                return;
            }

            var cteRelations = CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations, procScope: null);
            InspectSearchConditionCore(condition, byAlias, ordered, state);
        }

        private void InspectSearchCondition(BooleanExpression? condition, TableReference target, FromClause? extraFromClause, WithCtesAndXmlNamespaces? withClause, FlowState state)
        {
            if (condition is null)
            {
                return;
            }

            var cteRelations = CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var context = new FromScopeResolver.ResolutionContext(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(target, extraFromClause, context);
            InspectSearchConditionCore(condition, byAlias, ordered, state);
        }

        private void InspectSearchConditionCore(
            BooleanExpression condition,
            IReadOnlyDictionary<string, ScopeEntry> byAlias,
            IReadOnlyList<ScopeEntry> ordered,
            FlowState state)
        {
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
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
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            FlowState state)
        {
            if (columnSide is not ColumnReferenceExpression columnRef
                || variableSide is not VariableReference variableRef
                || !state.Reassigned!.Contains(variableRef.Name))
            {
                return;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
            if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
            {
                return;
            }

            var operatorText = ToOperatorText(comparison.ComparisonType);
            if (operatorText is null)
            {
                return;
            }

            var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName) ?? false;
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

        private FlowState AnalyzeIf(IfStatement ifStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var thenResult = AnalyzeSequential(ToStatementList(ifStatement.ThenStatement), CloneState(enteringState));
            var elseResult = ifStatement.ElseStatement is not null
                ? AnalyzeSequential(ToStatementList(ifStatement.ElseStatement), CloneState(enteringState))
                : enteringState;

            return IntersectBranches(thenResult, elseResult);
        }

        private FlowState AnalyzeWhile(WhileStatement whileStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var bodyResult = AnalyzeSequential(ToStatementList(whileStatement.Statement), CloneState(enteringState));
            return IntersectBranches(enteringState, bodyResult);
        }

        private FlowState AnalyzeTryCatch(TryCatchStatement tryCatch, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var tryResult = AnalyzeSequential(tryCatch.TryStatements.Statements, CloneState(enteringState));

            var catchResult = AnalyzeSequential(tryCatch.CatchStatements.Statements, CloneState(enteringState));

            return IntersectBranches(tryResult, catchResult);
        }

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

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
