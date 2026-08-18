using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Output parameter not populated on
/// every code path". A real, sound (never-guess) reachability walk over a procedure's own
/// control-flow AST - IF/ELSE, TRY/CATCH, WHILE, BEGIN/END, RETURN - tracking, per path, the SET
/// of the procedure's own OUTPUT parameters not yet PROVEN assigned. Directly reuses the
/// reachability-walk shape <see cref="TransactionHygieneScanner"/> already established for "does
/// every path resolve a state", adapted from a single tracked site to a set of tracked names.
///
/// Only <c>CREATE/ALTER/CREATE OR ALTER PROCEDURE</c> bodies are visited: a scalar or table-valued
/// function cannot declare an <c>OUTPUT</c> parameter at all (a hard T-SQL compile error), and a
/// trigger takes no parameters - neither can ever have a finding.
///
/// An assignment is recognized in exactly three forms, matching real T-SQL idiom:
/// <list type="bullet">
/// <item><c>SET @p = ...</c> (or any compound-assignment form, e.g. <c>+=</c>) - a
/// <see cref="SetVariableStatement"/> targeting the parameter's own name.</item>
/// <item><c>SELECT @p = ...</c> - a <see cref="SelectSetVariable"/> element in a top-level
/// <see cref="QuerySpecification"/>'s own select list (not inside a nested subquery - only the
/// outermost query of a standalone SELECT statement is inspected).</item>
/// <item><c>EXEC OtherProc @p = @p OUTPUT</c> - passing this procedure's own OUTPUT parameter
/// onward as the OUTPUT argument to a nested call. Treated as an assignment even though this pass
/// cannot verify the callee actually assigns it on every path of ITS OWN body - the alternative
/// (declining to recognize this idiom at all) would false-positive on a real, common, and
/// deliberate "delegate the whole output" pattern; a genuinely broken callee is the callee's own
/// procedure's finding, not a reason to double-flag the caller.</item>
/// </list>
///
/// <b>Known v1 scope limits, stated honestly (never guessed past):</b>
/// <list type="bullet">
/// <item>A <c>GOTO</c> anywhere in the procedure body declines the WHOLE procedure's analysis -
/// identical reasoning to <see cref="TransactionHygieneScanner"/>'s own documented choice: an
/// arbitrary jump target defeats a structural reachability walk without a real labeled-block CFG.</item>
/// <item>A <c>CATCH</c> block is analyzed as entering with whatever assignment state existed at
/// the START of its own <c>TRY</c>/<c>CATCH</c> construct - sound, not merely conservative, for
/// the identical reason <see cref="TransactionHygieneScanner"/>'s own doc comment states: an error
/// inside <c>TRY</c> can occur at literally the first statement.</item>
/// <item>A <c>WHILE</c> loop body is analyzed as running exactly one representative iteration,
/// OR-merged with the "ran zero times" possibility - the same approximation
/// <see cref="TransactionHygieneScanner"/> already documents for its own reason.</item>
/// <item>No cross-procedure tracking beyond the direct "passed onward as OUTPUT" recognition
/// above - an <c>EXEC</c> to another procedure that does NOT pass this parameter onward is never
/// followed into that callee's own body, matching every other stream in this codebase with the
/// same "no proc-call-transitive walk" limit.</item>
/// <item>An OUTPUT parameter reachable only through a query result (e.g. an INSERT...EXEC target
/// column, or a value read back from a temp table populated by a nested call) is never recognized
/// as an assignment source - only the three direct forms above are.</item>
/// </list>
///
/// <b>THROW is deliberately NOT treated as a finding site</b> - unlike a <c>RETURN</c> or the
/// natural end of the body, a <c>THROW</c> raises a real, loud engine error the instant it
/// executes; the caller does not silently receive a stale output value without any signal at
/// all, which is the specific "silent" defect this stream (and this codebase's whole scope rule)
/// targets - flagging it here would double-count a defect the engine itself already surfaces,
/// the same reasoning <see cref="Rules.WriteLossClassifier"/>'s own scope statement gives for
/// excluding cases T-SQL already hard-errors on. A <c>THROW</c> IS treated as terminal for the
/// walk (no statement after it executes on that path), but contributes no finding of its own.
/// <c>RAISERROR</c> is NOT treated as terminal at all - by default it does not stop batch
/// execution the way <c>THROW</c> does, so statements after it are genuinely still reachable.
/// </summary>
public static class OutputParameterScanner
{
    public static IReadOnlyList<OutputParameterFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ProcedureLine)
                .ThenBy(f => f.ParameterName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private readonly record struct FlowState(HashSet<string>? Unassigned, bool Declined)
    {
        public static FlowState Declined_() => new(null, true);
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        private int _procedureLine;
        private int _procedureColumn;

        public List<OutputParameterFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        private void AnalyzeProcedure(
            IList<ProcedureParameter> parameters, StatementList? statementList, SchemaObjectName procedureName)
        {
            var outputNames = parameters
                .Where(p => p.Modifier == ParameterModifier.Output)
                .Select(p => p.VariableName.Value)
                .ToList();

            if (outputNames.Count == 0 || statementList is null)
            {
                // No OUTPUT parameters to track, or an EXTERNAL NAME (CLR) body with nothing to walk.
                return;
            }

            var statements = statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;

            _procedureLine = procedureName.BaseIdentifier.StartLine;
            _procedureColumn = procedureName.BaseIdentifier.StartColumn;

            var entryState = new FlowState(new HashSet<string>(outputNames, StringComparer.OrdinalIgnoreCase), false);
            var finalState = AnalyzeSequential(statements, entryState);

            if (finalState is { Declined: false, Unassigned.Count: > 0 })
            {
                var exitAnchor = statements.Count > 0 ? statements[^1] : (TSqlFragment)procedureName;
                foreach (var name in finalState.Unassigned.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    Findings.Add(new OutputParameterFinding(
                        OutputParameterFindingKind.UnassignedOnSomePath,
                        sourcePath,
                        name,
                        procedureName.BaseIdentifier.StartLine,
                        procedureName.BaseIdentifier.StartColumn,
                        exitAnchor.StartLine,
                        exitAnchor.StartColumn));
                }
            }
        }

        private FlowState AnalyzeSequential(IList<TSqlStatement> statements, FlowState state)
        {
            foreach (var statement in statements)
            {
                // Nothing left to prove, or already declined - short-circuit the rest of this
                // path (matches TransactionHygieneScanner's identical short-circuit shape).
                if (state.Declined)
                {
                    return state;
                }

                if (state.Unassigned!.Count == 0)
                {
                    continue;
                }

                var (nextState, terminal) = AnalyzeStatement(statement, state);
                state = nextState;
                if (terminal)
                {
                    // Terminal: nothing after this executes on this path.
                    return state;
                }
            }

            return state;
        }

        private (FlowState State, bool Terminal) AnalyzeStatement(TSqlStatement statement, FlowState state)
        {
            switch (statement)
            {
                case SetVariableStatement set:
                    state.Unassigned!.Remove(set.Variable.Name);
                    return (state, false);

                case SelectStatement { QueryExpression: QuerySpecification spec }:
                    RemoveSelectAssignedVariables(spec, state.Unassigned!);
                    return (state, false);

                case ExecuteStatement exec:
                    RemoveForwardedOutputArguments(exec, state.Unassigned!);
                    return (state, false);

                case ReturnStatement:
                    EmitUnassignedFindings(state.Unassigned!, statement);
                    return (state with { Unassigned = [] }, true);

                case ThrowStatement:
                    // Terminal, but never a finding site - see class doc comment: a THROW is a
                    // real, loud engine error, not a silent defect.
                    return (state with { Unassigned = [] }, true);

                case GoToStatement:
                    return (FlowState.Declined_(), true);

                case BeginEndBlockStatement block:
                    return (AnalyzeSequential(block.StatementList.Statements, state), false);

                case IfStatement ifStatement:
                    return (AnalyzeIf(ifStatement, state), false);

                case WhileStatement whileStatement:
                    return (AnalyzeWhile(whileStatement, state), false);

                case TryCatchStatement tryCatch:
                    return (AnalyzeTryCatch(tryCatch, state), false);

                default:
                    return (state, false);
            }
        }

        private static void RemoveSelectAssignedVariables(QuerySpecification spec, HashSet<string> unassigned)
        {
            foreach (var element in spec.SelectElements.OfType<SelectSetVariable>())
            {
                unassigned.Remove(element.Variable.Name);
            }
        }

        private void EmitUnassignedFindings(HashSet<string> unassigned, TSqlStatement statement)
        {
            foreach (var name in unassigned)
            {
                Findings.Add(new OutputParameterFinding(
                    OutputParameterFindingKind.UnassignedOnSomePath,
                    sourcePath,
                    name,
                    _procedureLine,
                    _procedureColumn,
                    statement.StartLine,
                    statement.StartColumn));
            }
        }

        private static void RemoveForwardedOutputArguments(ExecuteStatement exec, HashSet<string> unassigned)
        {
            if (exec.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference procRef)
            {
                return;
            }

            foreach (var parameter in procRef.Parameters)
            {
                if (parameter is { IsOutput: true, ParameterValue: VariableReference variable })
                {
                    unassigned.Remove(variable.Name);
                }
            }
        }

        private FlowState AnalyzeIf(IfStatement ifStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var thenResult = AnalyzeSequential(
                ToStatementList(ifStatement.ThenStatement), CloneState(enteringState));
            var elseResult = ifStatement.ElseStatement is not null
                ? AnalyzeSequential(ToStatementList(ifStatement.ElseStatement), CloneState(enteringState))
                : enteringState;

            return MergeBranches(thenResult, elseResult);
        }

        private FlowState AnalyzeWhile(WhileStatement whileStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var bodyResult = AnalyzeSequential(
                ToStatementList(whileStatement.Statement), CloneState(enteringState));
            return MergeBranches(enteringState, bodyResult);
        }

        private FlowState AnalyzeTryCatch(TryCatchStatement tryCatch, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var tryResult = AnalyzeSequential(tryCatch.TryStatements.Statements, CloneState(enteringState));

            // CATCH enters with the state as of the TRY/CATCH construct's own start - sound, not
            // merely conservative, for the same reason TransactionHygieneScanner's own doc
            // comment gives.
            var catchResult = AnalyzeSequential(tryCatch.CatchStatements.Statements, CloneState(enteringState));

            return MergeBranches(tryResult, catchResult);
        }

        private static FlowState CloneState(FlowState state) =>
            state.Declined ? state : new FlowState([.. state.Unassigned!], false);

        private static FlowState MergeBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.Declined_();
            }

            var merged = new HashSet<string>(a.Unassigned!, StringComparer.OrdinalIgnoreCase);
            merged.UnionWith(b.Unassigned!);
            return new FlowState(merged, false);
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
