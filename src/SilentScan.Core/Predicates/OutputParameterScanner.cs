using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

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
