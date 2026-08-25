using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public static class ScopeVariableFlowTracker
{
    private readonly record struct WriteState(bool Written, bool LiteralKnown, ScalarExpression? Literal)
    {
        public static WriteState Unwritten { get; } = new(false, false, null);
    }

    public static bool WasAssignedBeforeCall(IList<TSqlStatement>? scopeStatements, string variableName, TSqlFragment callSite)
    {
        if (scopeStatements is null)
        {
            return true;
        }

        var everDeclared = DeclaresVariable(scopeStatements, variableName);
        return WasWrittenBeforeTarget(scopeStatements, variableName, callSite) || !everDeclared;
    }

    private static bool WasWrittenBeforeTarget(IList<TSqlStatement> statements, string variableName, TSqlFragment target)
    {
        foreach (var statement in statements)
        {
            if (ReferenceEquals(statement, target))
            {
                return false;
            }

            if (Contains(statement, target))
            {
                return WasWrittenBeforeInContainer(statement, variableName, target);
            }

            if (TouchesVariable([statement], variableName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WasWrittenBeforeInContainer(TSqlStatement statement, string variableName, TSqlFragment target) => statement switch
    {
        BeginEndBlockStatement block => WasWrittenBeforeTarget(block.StatementList.Statements, variableName, target),
        IfStatement ifStatement when Contains(ifStatement.ThenStatement, target) =>
            WasWrittenBeforeTarget(ToStatementList(ifStatement.ThenStatement), variableName, target),
        IfStatement { ElseStatement: { } elseStatement } when Contains(elseStatement, target) =>
            WasWrittenBeforeTarget(ToStatementList(elseStatement), variableName, target),
        WhileStatement whileStatement => WasWrittenBeforeTarget(ToStatementList(whileStatement.Statement), variableName, target),
        TryCatchStatement tryCatch when Contains(tryCatch.TryStatements, target) =>
            WasWrittenBeforeTarget(tryCatch.TryStatements.Statements, variableName, target),
        TryCatchStatement tryCatch => WasWrittenBeforeTarget(tryCatch.CatchStatements.Statements, variableName, target),
        _ => false,
    };

    public static ProcCallLiteralArgument? ResolvePropagatedLiteral(
        string? callerVariableName, IList<TSqlStatement>? currentScopeStatements, string sourcePath, TSqlFragment callSite)
    {
        if (callerVariableName is null || currentScopeStatements is null)
        {
            return null;
        }

        var state = ResolveUpTo(currentScopeStatements, callerVariableName, callSite, WriteState.Unwritten);
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

    private static WriteState ResolveUpTo(IList<TSqlStatement> statements, string variableName, TSqlFragment target, WriteState state)
    {
        foreach (var statement in statements)
        {
            if (ReferenceEquals(statement, target) || Contains(statement, target) && statement is not (BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement))
            {
                return state;
            }

            if (Contains(statement, target))
            {
                return ResolveIntoContainer(statement, variableName, target, state);
            }

            state = Advance(statement, variableName, state);
        }

        return state;
    }

    private static WriteState ResolveIntoContainer(TSqlStatement statement, string variableName, TSqlFragment target, WriteState state) => statement switch
    {
        BeginEndBlockStatement block => ResolveUpTo(block.StatementList.Statements, variableName, target, state),
        IfStatement ifStatement when Contains(ifStatement.ThenStatement, target) =>
            ResolveUpTo(ToStatementList(ifStatement.ThenStatement), variableName, target, state),
        IfStatement { ElseStatement: { } elseStatement } when Contains(elseStatement, target) =>
            ResolveUpTo(ToStatementList(elseStatement), variableName, target, state),
        WhileStatement whileStatement => ResolveUpTo(ToStatementList(whileStatement.Statement), variableName, target, state),
        TryCatchStatement tryCatch when Contains(tryCatch.TryStatements, target) =>
            ResolveUpTo(tryCatch.TryStatements.Statements, variableName, target, state),
        TryCatchStatement tryCatch => ResolveUpTo(tryCatch.CatchStatements.Statements, variableName, target, state),
        _ => state,
    };

    private static WriteState Advance(TSqlStatement statement, string variableName, WriteState state)
    {
        var directWrite = VariableWriteSites.InStatement(statement).FirstOrDefault(w => NameEquals(w.Name, variableName));
        if (directWrite.Name is not null)
        {
            var literal = VariableWriteSites.DirectLiteralAssignment(statement, variableName);
            return literal is not null ? new WriteState(true, true, literal) : new WriteState(true, false, null);
        }

        return statement switch
        {
            BeginEndBlockStatement block => AnalyzeList(block.StatementList.Statements, variableName, state),
            IfStatement ifStatement when TouchesVariable(AllBranches(ifStatement), variableName) => AnalyzeIf(ifStatement, variableName, state),
            WhileStatement whileStatement when TouchesVariable(ToStatementList(whileStatement.Statement), variableName) =>
                Intersect(state, AnalyzeList(ToStatementList(whileStatement.Statement), variableName, state)),
            TryCatchStatement tryCatch when TouchesVariable(tryCatch.TryStatements.Statements, variableName)
                || TouchesVariable(tryCatch.CatchStatements.Statements, variableName) =>
                Intersect(
                    AnalyzeList(tryCatch.TryStatements.Statements, variableName, state),
                    AnalyzeList(tryCatch.CatchStatements.Statements, variableName, state)),
            ReturnStatement or ThrowStatement or GoToStatement => state,
            _ => state,
        };
    }

    private static WriteState AnalyzeIf(IfStatement ifStatement, string variableName, WriteState entryState)
    {
        var thenState = AnalyzeList(ToStatementList(ifStatement.ThenStatement), variableName, entryState);
        var elseState = ifStatement.ElseStatement is not null
            ? AnalyzeList(ToStatementList(ifStatement.ElseStatement), variableName, entryState)
            : entryState;
        return Intersect(thenState, elseState);
    }

    private static WriteState AnalyzeList(IList<TSqlStatement> statements, string variableName, WriteState state)
    {
        foreach (var statement in statements)
        {
            if (statement is ReturnStatement or ThrowStatement or GoToStatement)
            {
                return state;
            }

            state = Advance(statement, variableName, state);
        }

        return state;
    }

    private static WriteState Intersect(WriteState a, WriteState b)
    {
        var written = a.Written && b.Written;
        if (!written)
        {
            return WriteState.Unwritten;
        }

        return a is { LiteralKnown: true } && b is { LiteralKnown: true } && LiteralTextEquals(a.Literal, b.Literal)
            ? new WriteState(true, true, a.Literal)
            : new WriteState(true, false, null);
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
