using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Catch-all / kitchen-sink predicates" sibling: "parameter
/// overwritten before use in a predicate" (sniffing-defeat). A standalone scanner, not folded
/// into <see cref="TypedPredicateExtractor"/>'s per-comparison walk: this needs real,
/// path-sensitive reachability state threaded THROUGH the statement list (which formal parameters
/// are reassigned on EVERY path reaching the current point), the same reachability-walk shape
/// <see cref="OutputParameterScanner"/>/<see cref="TransactionHygieneScanner"/> already
/// established - but tracking the DUAL property. Those two track "is there some path where a
/// fact does NOT yet hold" (state only shrinks toward empty, merges via UNION at a branch so a gap
/// on either side keeps the name flagged). This scanner tracks "does a fact hold on EVERY path
/// reaching here" (state only grows, merges via INTERSECT at a branch, so a reassignment on only
/// one side of an IF is NOT carried past the merge point - sound, not merely conservative: a
/// predicate after the merge cannot be guaranteed to see the reassigned value unless BOTH branches
/// produced it).
///
/// Deliberately base-table-only and WHERE-clause-only, like <see cref="CatchAllPredicateScanner"/>:
/// column resolution goes through <see cref="FromScopeResolver"/> with no CTE/view/temp-table
/// scoping (empty resolved-views map, null ledger/CTE map/proc scope), and JOIN ON/HAVING
/// predicates and MERGE's own ON clause are a known, explicitly out-of-v1-scope gap (MERGE's
/// scope resolution differs enough to need its own dedicated work - the identical reasoning
/// <see cref="CatchAllPredicateScanner"/> already documents for excluding it). Only
/// <see cref="BooleanComparisonExpression"/> operators are matched (<c>=</c>/<c>&lt;</c>/
/// <c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c>/<c>&lt;&gt;</c>) - <c>LIKE</c> uses a distinct
/// <see cref="LikePredicate"/> AST shape and is a known v1 scope limit, not silently missed.
///
/// <b>Known v1 scope limits, stated honestly (mirrors <see cref="OutputParameterScanner"/>'s own
/// list, since this is the identical reachability-walk shape):</b>
/// <list type="bullet">
/// <item>A <c>GOTO</c> anywhere in the procedure body declines the WHOLE procedure's analysis - an
/// arbitrary jump target defeats a structural reachability walk without a real labeled-block CFG.</item>
/// <item>A <c>CATCH</c> block is analyzed as entering with whatever reassignment state existed at
/// the START of its own TRY/CATCH construct, never inheriting anything the TRY block itself did -
/// sound, not merely conservative: an error can occur at the TRY block's very first statement.</item>
/// <item>A <c>WHILE</c> loop body's own reassignments never propagate past the loop for guarantee
/// purposes (the loop might run zero times) - the identical "ran zero times, OR-merged with one
/// representative iteration" approximation those two scanners already document, here applied via
/// intersection instead of union.</item>
/// <item>No cross-procedure tracking - an <c>EXEC</c> to another procedure is never followed into
/// that callee's own body, matching every other stream in this codebase's "no proc-call-transitive
/// walk" limit.</item>
/// </list>
/// </summary>
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

        // Only a genuine FORMAL parameter is a sniffable, caller-supplied value whose staleness
        // this stream reports - a DECLARE'd local was never sniffed to begin with (that's
        // LocalVariablePredicateFinding's own, separate concern). Without this gate, reassigning
        // ANY local variable (declared or not) would be tracked identically, misattributing a
        // finding to a variable the optimizer never sniffed in the first place - caught only
        // against the real corpus (a DECLARE'd-and-reassigned local, never a parameter) before
        // this fix.
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
                // A DECLARE'd local (or an unrelated OUT-of-scope name) was reassigned - never a
                // sniffable, caller-supplied value in the first place, so it is never tracked here.
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
                    InspectSearchCondition(spec.WhereClause?.SearchCondition, spec.FromClause, state, spec);
                    break;

                case UpdateStatement { UpdateSpecification: { } upd } update when !HasOptionRecompile(update.OptimizerHints):
                    InspectSearchCondition(upd.WhereClause?.SearchCondition, upd.Target, upd.FromClause, state, statement);
                    break;

                case DeleteStatement { DeleteSpecification: { } del } delete when !HasOptionRecompile(delete.OptimizerHints):
                    InspectSearchCondition(del.WhereClause?.SearchCondition, del.Target, del.FromClause, state, statement);
                    break;

                // MERGE's own ON clause needs its own dedicated scope-resolution work - the same
                // known v1 gap CatchAllPredicateScanner already documents for the identical reason.
                default:
                    break;
            }
        }

        private static bool HasOptionRecompile(IList<OptimizerHint> hints) =>
            hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);

        private void InspectSearchCondition(BooleanExpression? condition, FromClause? fromClause, FlowState state, TSqlFragment anchor)
        {
            if (condition is null || fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
            InspectSearchConditionCore(condition, byAlias, ordered, state);
        }

        private void InspectSearchCondition(BooleanExpression? condition, TableReference target, FromClause? extraFromClause, FlowState state, TSqlFragment anchor)
        {
            if (condition is null)
            {
                return;
            }

            var context = new FromScopeResolver.ResolutionContext(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteRelations: null, ProcScope: null);
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

            // CATCH enters with the state as of the TRY/CATCH construct's own start - the identical
            // reasoning OutputParameterScanner/TransactionHygieneScanner already document.
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
                // Prefer whichever branch's own site is later in source order - either is a sound
                // "this reassignment already happened by here" anchor, both branches guaranteed it.
                sites[name] = b.ReassignmentSites!.TryGetValue(name, out var bSite) ? bSite : a.ReassignmentSites![name];
            }

            return new FlowState(merged, sites, false);
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
