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
        var state = ResolveUpTo(scopeStatements, variableName, callSite, WriteState.Unwritten, BranchCombine.RequireAny);
        return new AssignmentFlow(state.Written || !everDeclared, state.Approximate);
    }

    public static ProcCallLiteralArgument? ResolvePropagatedLiteral(
        string? callerVariableName, IList<TSqlStatement>? currentScopeStatements, string sourcePath, TSqlFragment callSite)
    {
        if (callerVariableName is null || currentScopeStatements is null)
        {
            return null;
        }

        var state = ResolveUpTo(currentScopeStatements, callerVariableName, callSite, WriteState.Unwritten, BranchCombine.RequireAll);
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

    private static WriteState ResolveUpTo(IList<TSqlStatement> statements, string variableName, TSqlFragment target, WriteState state, BranchCombine combine)
    {
        foreach (var statement in statements)
        {
            if (ReferenceEquals(statement, target) || Contains(statement, target) && statement is not (BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement))
            {
                return state;
            }

            if (Contains(statement, target))
            {
                return ResolveIntoContainer(statement, variableName, target, state, combine);
            }

            if (TryHandleFlowTerminator(statement, state, out var terminatedState))
            {
                return terminatedState;
            }

            state = Advance(statement, variableName, state, combine);
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

    private static WriteState ResolveIntoContainer(TSqlStatement statement, string variableName, TSqlFragment target, WriteState state, BranchCombine combine) => statement switch
    {
        BeginEndBlockStatement block => ResolveUpTo(block.StatementList.Statements, variableName, target, state, combine),
        IfStatement ifStatement when Contains(ifStatement.ThenStatement, target) =>
            ResolveUpTo(ToStatementList(ifStatement.ThenStatement), variableName, target, state, combine),
        IfStatement { ElseStatement: { } elseStatement } when Contains(elseStatement, target) =>
            ResolveUpTo(ToStatementList(elseStatement), variableName, target, state, combine),
        WhileStatement whileStatement => ResolveUpTo(ToStatementList(whileStatement.Statement), variableName, target, state, combine),
        TryCatchStatement tryCatch when Contains(tryCatch.TryStatements, target) =>
            ResolveUpTo(tryCatch.TryStatements.Statements, variableName, target, state, combine),
        TryCatchStatement tryCatch => ResolveUpTo(tryCatch.CatchStatements.Statements, variableName, target, state, combine),
        _ => state,
    };

    private static WriteState Advance(TSqlStatement statement, string variableName, WriteState state, BranchCombine combine)
    {
        var directWrite = VariableWriteSites.InStatement(statement).FirstOrDefault(w => NameEquals(w.Name, variableName));
        if (directWrite.Name is not null)
        {
            var literal = VariableWriteSites.DirectLiteralAssignment(statement, variableName);
            return literal is not null ? new WriteState(true, true, literal, Approximate: false) : new WriteState(true, false, null, Approximate: false);
        }

        return statement switch
        {
            BeginEndBlockStatement block => AnalyzeList(block.StatementList.Statements, variableName, state, combine),
            IfStatement ifStatement when TouchesVariable(AllBranches(ifStatement), variableName) => AnalyzeIf(ifStatement, variableName, state, combine),
            WhileStatement whileStatement when TouchesVariable(ToStatementList(whileStatement.Statement), variableName) =>
                AnalyzeWhileToFixpoint(ToStatementList(whileStatement.Statement), variableName, state, combine),
            TryCatchStatement tryCatch when TouchesVariable(tryCatch.TryStatements.Statements, variableName)
                || TouchesVariable(tryCatch.CatchStatements.Statements, variableName) =>
                Combine(
                    AnalyzeList(tryCatch.TryStatements.Statements, variableName, state, combine),
                    AnalyzeList(tryCatch.CatchStatements.Statements, variableName, state, combine),
                    combine),
            GoToStatement => state with { Approximate = true },
            ReturnStatement or ThrowStatement => state,
            _ => state,
        };
    }

    private const int LoopFixpointIterationCap = 8;

    private static WriteState AnalyzeWhileToFixpoint(IList<TSqlStatement> body, string variableName, WriteState entryState, BranchCombine combine)
    {
        var current = entryState;
        for (var iteration = 0; iteration < LoopFixpointIterationCap; iteration++)
        {
            var next = Combine(entryState, AnalyzeList(body, variableName, current, combine), combine);
            if (next == current)
            {
                return next;
            }

            current = next;
        }

        return current with { Approximate = true };
    }

    private static WriteState AnalyzeIf(IfStatement ifStatement, string variableName, WriteState entryState, BranchCombine combine)
    {
        var thenState = AnalyzeList(ToStatementList(ifStatement.ThenStatement), variableName, entryState, combine);
        var elseState = ifStatement.ElseStatement is not null
            ? AnalyzeList(ToStatementList(ifStatement.ElseStatement), variableName, entryState, combine)
            : entryState;
        return Combine(thenState, elseState, combine);
    }

    private static WriteState AnalyzeList(IList<TSqlStatement> statements, string variableName, WriteState state, BranchCombine combine)
    {
        foreach (var statement in statements)
        {
            if (TryHandleFlowTerminator(statement, state, out var terminatedState))
            {
                return terminatedState;
            }

            state = Advance(statement, variableName, state, combine);
        }

        return state;
    }

    private static WriteState Combine(WriteState a, WriteState b, BranchCombine combine) =>
        combine == BranchCombine.RequireAll ? Intersect(a, b) : Union(a, b);

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

    private static bool TouchesVariable(IEnumerable<TSqlStatement> statements, string variableName)
    {
        foreach (var statement in statements)
        {
            if (VariableWriteSites.InStatement(statement).Any(w => NameEquals(w.Name, variableName)))
            {
                return true;
            }

            if (NestedLists(statement).Any(nested => TouchesVariable(nested, variableName)))
            {
                return true;
            }
        }

        return false;
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

    private static IEnumerable<TSqlStatement> AllBranches(IfStatement ifStatement)
    {
        yield return ifStatement.ThenStatement;
        if (ifStatement.ElseStatement is not null)
        {
            yield return ifStatement.ElseStatement;
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
