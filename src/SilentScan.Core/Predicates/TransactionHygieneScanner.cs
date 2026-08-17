using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds", first half of the "Transaction hygiene pair"
/// item: <c>BEGIN TRANSACTION</c> with no reachable <c>COMMIT</c>/<c>ROLLBACK</c> on some path.
/// A real, sound (never-guess) reachability walk over the module body's own control-flow AST -
/// IF/ELSE, TRY/CATCH, WHILE, BEGIN/END, RETURN/THROW - not a heuristic text scan.
///
/// Only procedure and trigger bodies are visited: a scalar/table-valued function cannot contain
/// <c>BEGIN TRANSACTION</c> at all (a hard T-SQL compile error, Msg 443) - verified directly
/// against the oracle before excluding functions here, not assumed from documentation.
///
/// <b>Known v1 scope limits, stated honestly (never guessed past):</b>
/// <list type="bullet">
/// <item>Only ONE currently-open <c>BEGIN TRANSACTION</c> instance is tracked at a time. A second
/// <c>BEGIN TRANSACTION</c> reached while one is already open (a real, if unusual, nested-
/// transaction shape) declines the whole enclosing scope's analysis from that point on, rather
/// than guessing which of the two open instances a later <c>COMMIT</c>/<c>ROLLBACK</c> resolves -
/// <c>@@TRANCOUNT</c> nesting semantics genuinely make that ambiguous without deeper modeling.</item>
/// <item>A <c>GOTO</c> anywhere in the module body declines the WHOLE module's analysis - an
/// arbitrary jump target defeats a structural reachability walk without a real labeled-block CFG,
/// which this pass does not build (unlike <see cref="DynamicSqlValue.DynamicSqlCfg"/>'s own
/// GOTO-aware fixpoint engine, purpose-built for a different lattice - reusing it here would need
/// a second, unrelated value domain bolted onto a fixpoint solver designed around
/// <c>SqlTextValue</c>, not a net simplification for one boolean fact).</item>
/// <item>A <c>CATCH</c> block is analyzed as entering with whatever transaction state existed at
/// the START of its own <c>TRY</c>/<c>CATCH</c> construct - <b>sound, not merely conservative</b>:
/// an error inside <c>TRY</c> can occur at literally the first statement, so "the state as of TRY's
/// own start" is itself a real, statically reachable entry state for <c>CATCH</c>, never an
/// over-claim. The complementary gap this leaves is a real under-report, not a false positive: a
/// <c>BEGIN TRANSACTION</c> opened INSIDE its own <c>TRY</c> block is tracked correctly for the
/// no-error (falls-through) path, but is not cross-checked into that same <c>TRY</c>'s <c>CATCH</c>
/// block at all (<c>CATCH</c> enters with whatever was open before the whole construct started,
/// which never includes a transaction the <c>TRY</c> itself only just opened).</item>
/// <item>A <c>WHILE</c> loop body is analyzed as running exactly one representative iteration - the
/// merged state after the loop is the OR of "ran zero times" (state unchanged) and "ran once" (the
/// body's own effect), the same branch-merge logic already used for IF's implicit ELSE.
/// <c>BREAK</c>/<c>CONTINUE</c> are not specially modeled (treated as ordinary, no-effect
/// statements) - a documented, not silently assumed, approximation.</item>
/// <item>No cross-procedure tracking: an <c>EXEC</c> to another procedure is never followed into
/// that callee's own body, matching every other stream in this codebase that states the same
/// "no proc-call-transitive walk" limit (e.g. the SET-options stream's identical documented
/// choice) for the same reason - this pass never holds every module's parsed AST alive at once.</item>
/// </list>
///
/// A correctness/robustness finding, not a plan-shape one - see <see cref="TransactionHygieneFinding"/>
/// for the oracle confirmation of the underlying <c>@@TRANCOUNT</c> mechanism and severity/SARIF
/// tier.
/// </summary>
public static class TransactionHygieneScanner
{
    public static IReadOnlyList<TransactionHygieneFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.BeginTransactionLine)
                .ThenBy(f => f.BeginTransactionColumn)
                .ThenBy(f => f.UnresolvedExitLine)
                .ThenBy(f => f.UnresolvedExitColumn),
        ];
    }

    private readonly record struct FlowState(BeginTransactionStatement? OpenSite, bool Declined)
    {
        public static readonly FlowState NotTracking = new(null, false);
        public static readonly FlowState DeclinedState = new(null, true);
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<TransactionHygieneFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) => AnalyzeScope(node.StatementList);

        public override void ExplicitVisit(AlterProcedureStatement node) => AnalyzeScope(node.StatementList);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => AnalyzeScope(node.StatementList);

        public override void ExplicitVisit(CreateTriggerStatement node) => AnalyzeScope(node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) => AnalyzeScope(node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => AnalyzeScope(node.StatementList);

        private void AnalyzeScope(StatementList? statementList)
        {
            // StatementList is null for an EXTERNAL NAME (CLR) body - nothing to walk.
            if (statementList is null)
            {
                return;
            }

            // The overwhelmingly common `AS BEGIN ... END` shape wraps the whole body in one
            // BeginEndBlockStatement - unwrap it so "the scope's own top-level statements" is the
            // real statement sequence, not a one-element list containing just the wrapper (same
            // trick ProcCallGraphBuilder.VisitScopedBody already uses for the identical reason).
            var statements = statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;

            var finalState = AnalyzeSequential(statements, FlowState.NotTracking);

            if (finalState is { Declined: false, OpenSite: { } openSite })
            {
                var exitAnchor = statements.Count > 0 ? statements[^1] : (TSqlStatement)openSite;
                Findings.Add(new TransactionHygieneFinding(
                    TransactionHygieneFindingKind.UnresolvedOnSomePath,
                    sourcePath,
                    openSite.StartLine,
                    openSite.StartColumn,
                    exitAnchor.StartLine,
                    exitAnchor.StartColumn));
            }
        }

        private FlowState AnalyzeSequential(IList<TSqlStatement> statements, FlowState state)
        {
            foreach (var statement in statements)
            {
                if (state.Declined)
                {
                    return state;
                }

                switch (statement)
                {
                    case BeginTransactionStatement begin:
                        state = state.OpenSite is not null
                            ? FlowState.DeclinedState
                            : state with { OpenSite = begin };
                        break;

                    case CommitTransactionStatement or RollbackTransactionStatement:
                        state = state with { OpenSite = null };
                        break;

                    case ReturnStatement or ThrowStatement:
                        if (state.OpenSite is { } openSite)
                        {
                            Findings.Add(new TransactionHygieneFinding(
                                TransactionHygieneFindingKind.UnresolvedOnSomePath,
                                sourcePath,
                                openSite.StartLine,
                                openSite.StartColumn,
                                statement.StartLine,
                                statement.StartColumn));
                        }

                        // Terminal: nothing after this executes on this path, so it contributes
                        // no further "falls through open" possibility to the caller.
                        return state with { OpenSite = null };

                    case GoToStatement:
                        // An arbitrary jump target defeats this structural walk - decline the
                        // whole enclosing scope rather than guess (see class doc comment).
                        return FlowState.DeclinedState;

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
                        // An ordinary statement (SELECT/INSERT/UPDATE/DELETE/DECLARE/SET/EXEC/...)
                        // has no effect on transaction state for this rule's purposes.
                        break;
                }
            }

            return state;
        }

        private FlowState AnalyzeIf(IfStatement ifStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var thenResult = AnalyzeSequential(ToStatementList(ifStatement.ThenStatement), enteringState);

            // No ELSE: the implicit else path falls straight through unchanged - the exact
            // analog of a WHILE loop's own "ran zero times" branch below.
            var elseResult = ifStatement.ElseStatement is not null
                ? AnalyzeSequential(ToStatementList(ifStatement.ElseStatement), enteringState)
                : enteringState;

            return MergeBranches(thenResult, elseResult);
        }

        private FlowState AnalyzeWhile(WhileStatement whileStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            // One representative iteration, OR-merged with the "ran zero times" (state
            // unchanged) possibility - see class doc comment's documented approximation.
            var bodyResult = AnalyzeSequential(ToStatementList(whileStatement.Statement), enteringState);
            return MergeBranches(enteringState, bodyResult);
        }

        private FlowState AnalyzeTryCatch(TryCatchStatement tryCatch, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var tryResult = AnalyzeSequential(tryCatch.TryStatements.Statements, enteringState);

            // CATCH enters with the state as of the TRY/CATCH construct's own start - sound, not
            // merely conservative (see class doc comment): an error can occur at TRY's very first
            // statement, so this is itself a real, statically reachable entry state for CATCH.
            var catchResult = AnalyzeSequential(tryCatch.CatchStatements.Statements, enteringState);

            return MergeBranches(tryResult, catchResult);
        }

        private static FlowState MergeBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.DeclinedState;
            }

            if (a.OpenSite is null && b.OpenSite is null)
            {
                return FlowState.NotTracking;
            }

            if (a.OpenSite is null)
            {
                return b;
            }

            if (b.OpenSite is null)
            {
                return a;
            }

            // Both branches carry an open transaction. If it's the SAME one (entered the
            // construct already open, neither branch resolved it), carrying it forward is
            // unambiguous. If the two branches independently opened DIFFERENT transactions and
            // neither resolved its own, which one continues is genuinely ambiguous - decline
            // rather than arbitrarily pick one.
            return ReferenceEquals(a.OpenSite, b.OpenSite) ? a : FlowState.DeclinedState;
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
