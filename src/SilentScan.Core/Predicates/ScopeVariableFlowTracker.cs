using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public static class ScopeVariableFlowTracker
{
    private readonly record struct WriteState(bool Written, bool LiteralKnown, ScalarExpression? Literal, bool Approximate)
    {
        public static WriteState Unwritten { get; } = new(false, false, null, false);
    }

    private enum BranchCombine
    {
        RequireAll,
        RequireAny,
    }

    public readonly record struct AssignmentFlow(bool Assigned, bool Approximate);

    public static AssignmentFlow WasAssignedBeforeCall(IList<TSqlStatement>? scopeStatements, string variableName, TSqlFragment callSite)
    {
        if (scopeStatements is null)
        {
            return new AssignmentFlow(Assigned: false, Approximate: false);
        }

        var everDeclared = DeclaresVariable(scopeStatements, variableName);
        var policy = new Policy(variableName, BranchCombine.RequireAny);
        var state = ResolveUpTo(scopeStatements, variableName, callSite, WriteState.Unwritten, policy);
        return new AssignmentFlow(state.Written || !everDeclared, state.Approximate);
    }

    public static ProcCallLiteralArgument? ResolvePropagatedLiteral(
        string? callerVariableName, IList<TSqlStatement>? currentScopeStatements, string sourcePath, TSqlFragment callSite)
    {
        if (callerVariableName is null || currentScopeStatements is null)
        {
            return null;
        }

        var policy = new Policy(callerVariableName, BranchCombine.RequireAll);
        var state = ResolveUpTo(currentScopeStatements, callerVariableName, callSite, WriteState.Unwritten, policy);
        return state is { Written: true, LiteralKnown: true, Literal: { } literal }
            ? TryGetDirectLiteralArgument(literal, sourcePath)
            : null;
    }

    public static ProcCallLiteralArgument? TryGetDirectLiteralArgument(ScalarExpression parameterValue, string sourcePath) => parameterValue switch
    {
        StringLiteral stringLiteral => ToLiteralArgument(stringLiteral, sourcePath),
        IntegerLiteral integerLiteral => ToIntegerLiteralArgument(integerLiteral, sourcePath),
        _ => null,
    };

    private static bool DeclaresVariable(IList<TSqlStatement> statements, string variableName)
    {
        foreach (var statement in statements)
        {
            if (statement is DeclareVariableStatement declare
                && declare.Declarations.Any(e => NameEquals(e.VariableName.Value, variableName)))
            {
                return true;
            }

            if (NestedLists(statement).Any(nested => DeclaresVariable(nested, variableName)))
            {
                return true;
            }
        }

        return false;
    }

    private static WriteState ResolveUpTo(IList<TSqlStatement> statements, string variableName, TSqlFragment target, WriteState state, Policy policy)
    {
        foreach (var statement in statements)
        {
            if (ReferenceEquals(statement, target) || Contains(statement, target) && statement is not (BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement))
            {
                return state;
            }

            if (Contains(statement, target))
            {
                return ResolveIntoContainer(statement, variableName, target, state, policy);
            }

            if (TryHandleFlowTerminator(statement, state, out var terminatedState))
            {
                return terminatedState;
            }

            state = ProcedureBodyFlowWalker.Walk([statement], state, policy);
        }

        return state;
    }

    private static bool TryHandleFlowTerminator(TSqlStatement statement, WriteState state, out WriteState result)
    {
        switch (statement)
        {
            case GoToStatement:
                result = state with { Approximate = true };
                return true;

            case ReturnStatement or ThrowStatement:
                result = state;
                return true;

            default:
                result = state;
                return false;
        }
    }

    private static WriteState ResolveIntoContainer(TSqlStatement statement, string variableName, TSqlFragment target, WriteState state, Policy policy) => statement switch
    {
        BeginEndBlockStatement block => ResolveUpTo(block.StatementList.Statements, variableName, target, state, policy),
        IfStatement ifStatement when Contains(ifStatement.ThenStatement, target) =>
            ResolveUpTo(ToStatementList(ifStatement.ThenStatement), variableName, target, state, policy),
        IfStatement { ElseStatement: { } elseStatement } when Contains(elseStatement, target) =>
            ResolveUpTo(ToStatementList(elseStatement), variableName, target, state, policy),
        WhileStatement whileStatement => ResolveUpTo(ToStatementList(whileStatement.Statement), variableName, target, state, policy),
        TryCatchStatement tryCatch when Contains(tryCatch.TryStatements, target) =>
            ResolveUpTo(tryCatch.TryStatements.Statements, variableName, target, state, policy),
        TryCatchStatement tryCatch => ResolveUpTo(tryCatch.CatchStatements.Statements, variableName, target, state, policy),
        _ => state,
    };

    private const int LoopFixpointIterationCap = 8;

    private sealed class Policy(string variableName, BranchCombine combine) : IStatementFlowPolicy<WriteState>
    {
        public bool IsDeclined(WriteState state) => false;

        public bool IsDone(WriteState state) => false;

        public WriteState PerStatement(TSqlStatement statement, WriteState state)
        {
            var directWrite = VariableWriteSites.InStatement(statement).FirstOrDefault(w => NameEquals(w.Name, variableName));
            if (directWrite.Name is null)
            {
                return state;
            }

            var literal = VariableWriteSites.DirectLiteralAssignment(statement, variableName);
            return literal is not null ? new WriteState(true, true, literal, Approximate: false) : new WriteState(true, false, null, Approximate: false);
        }

        public WriteState OnReturn(WriteState state, TSqlStatement statement) => state;

        public WriteState OnThrow(WriteState state) => state;

        public WriteState OnGoTo(WriteState state) => state with { Approximate = true };

        public WriteState CloneForBranch(WriteState state) => state;

        public WriteState Merge(WriteState a, WriteState b) => combine == BranchCombine.RequireAll ? Intersect(a, b) : Union(a, b);

        public int WhileFixpointCap => LoopFixpointIterationCap;

        public bool StatesEqual(WriteState a, WriteState b) => a == b;

        public WriteState MarkApproximateOnCapExceeded(WriteState state) => state with { Approximate = true };

        private static WriteState Intersect(WriteState a, WriteState b)
        {
            var approximate = a.Approximate || b.Approximate;
            var written = a.Written && b.Written;
            if (!written)
            {
                return WriteState.Unwritten with { Approximate = approximate };
            }

            return a is { LiteralKnown: true } && b is { LiteralKnown: true } && LiteralTextEquals(a.Literal, b.Literal)
                ? new WriteState(true, true, a.Literal, approximate)
                : new WriteState(true, false, null, approximate);
        }

        private static WriteState Union(WriteState a, WriteState b)
        {
            var approximate = a.Approximate || b.Approximate;
            if (!a.Written && !b.Written)
            {
                return WriteState.Unwritten with { Approximate = approximate };
            }

            return a is { Written: true, LiteralKnown: true } && b is { Written: true, LiteralKnown: true } && LiteralTextEquals(a.Literal, b.Literal)
                ? new WriteState(true, true, a.Literal, approximate)
                : new WriteState(true, false, null, approximate);
        }

        private static bool LiteralTextEquals(ScalarExpression? a, ScalarExpression? b) => (a, b) switch
        {
            (StringLiteral left, StringLiteral right) => string.Equals(left.Value, right.Value, StringComparison.Ordinal),
            (IntegerLiteral left, IntegerLiteral right) => string.Equals(left.Value, right.Value, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static IEnumerable<IList<TSqlStatement>> NestedLists(TSqlStatement statement)
    {
        switch (statement)
        {
            case BeginEndBlockStatement block:
                yield return block.StatementList.Statements;
                break;

            case IfStatement ifStatement:
                yield return ToStatementList(ifStatement.ThenStatement);
                if (ifStatement.ElseStatement is not null)
                {
                    yield return ToStatementList(ifStatement.ElseStatement);
                }

                break;

            case WhileStatement whileStatement:
                yield return ToStatementList(whileStatement.Statement);
                break;

            case TryCatchStatement tryCatch:
                yield return tryCatch.TryStatements.Statements;
                yield return tryCatch.CatchStatements.Statements;
                break;
        }
    }

    private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
        statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(TSqlFragment container, TSqlFragment target)
    {
        if (ReferenceEquals(container, target))
        {
            return true;
        }

        var containerEnd = EndPosition(container);
        var targetStart = (target.StartLine, target.StartColumn);
        return (container.StartLine, container.StartColumn).CompareTo(targetStart) <= 0
            && targetStart.CompareTo(containerEnd) <= 0;
    }

    private static (int Line, int Column) EndPosition(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < 0 || fragment.LastTokenIndex >= fragment.ScriptTokenStream.Count)
        {
            return (fragment.StartLine, fragment.StartColumn);
        }

        var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
        return (lastToken.Line, lastToken.Column);
    }

    private static ProcCallLiteralArgument ToLiteralArgument(StringLiteral stringLiteral, string sourcePath)
    {
        var prefixLength = stringLiteral.IsNational ? 2 : 1;
        return new ProcCallLiteralArgument(stringLiteral.Value, sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn, prefixLength);
    }

    private static ProcCallLiteralArgument ToIntegerLiteralArgument(IntegerLiteral integerLiteral, string sourcePath) =>
        new(integerLiteral.Value, sourcePath, integerLiteral.StartLine, integerLiteral.StartColumn, PrefixLength: 0);
}
