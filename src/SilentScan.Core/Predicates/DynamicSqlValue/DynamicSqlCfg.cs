using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public sealed class DynamicSqlCfg
{
    private const int MaxFixpointRounds = 50;

    private sealed class Block
    {
        public List<Action<Dictionary<string, SqlTextValue>, bool>> Steps { get; } = [];

        public List<int> Successors { get; } = [];
    }

    private readonly string _sourcePath;
    private readonly int _cap;
    private readonly Func<TSqlStatement, IReadOnlyList<string>, Action<Dictionary<string, SqlTextValue>, bool>> _compileLeaf;
    private readonly IReadOnlySet<string> _callerSeededVariableNames;
    private static readonly string[] NoActiveGuards = [];
    private static readonly HashSet<string> NoCallerSeededVariableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<string, int> _labelBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _guardTextByJoinBlock = [];
    private readonly Dictionary<int, BooleanExpression> _conditionByJoinBlock = [];
    private readonly Dictionary<int, int> _thenPredecessorByJoinBlock = [];
    private readonly Dictionary<int, int> _elsePredecessorByJoinBlock = [];
    private readonly Dictionary<int, SourceSpan> _blockSpan = [];
    private SourceSpan _defaultSpan;

public DynamicSqlCfg(
        string sourcePath, int cap, Func<TSqlStatement, IReadOnlyList<string>, Action<Dictionary<string, SqlTextValue>, bool>> compileLeaf,
        IReadOnlySet<string>? callerSeededVariableNames = null)
    {
        _sourcePath = sourcePath;
        _cap = cap;
        _compileLeaf = compileLeaf;
        _callerSeededVariableNames = callerSeededVariableNames ?? NoCallerSeededVariableNames;
    }

public Dictionary<string, SqlTextValue> Solve(IList<TSqlStatement> statements, Dictionary<string, SqlTextValue> initialSeed)
    {
        _defaultSpan = statements.Count > 0 ? Span(statements[0]) : new SourceSpan(_sourcePath, 1, 1);

        PreRegisterLabels(statements);
        var entryBlock = NewBlock();
        var exitBlocks = new List<int>();
        if (BuildSequence(statements, entryBlock, exitBlocks, new Stack<(int Header, int After)>(), NoActiveGuards) is { } scopeFallthrough)
        {
            exitBlocks.Add(scopeFallthrough);
        }

        var predecessors = BuildPredecessors();
        var outStates = new Dictionary<string, SqlTextValue>?[_blocks.Count];

        RunFixpoint(entryBlock, initialSeed, predecessors, outStates);
        RunFinalPass(entryBlock, initialSeed, predecessors, outStates, emit: true);

        return MergeExitStates(exitBlocks, outStates);
    }

    private int NewBlock()
    {
        _blocks.Add(new Block());
        return _blocks.Count - 1;
    }

    private SourceSpan Span(TSqlFragment fragment) => new(_sourcePath, fragment.StartLine, fragment.StartColumn);

    private static string NormalizeLabel(string labelValue) => labelValue.TrimEnd(':');

    private List<int>[] BuildPredecessors()
    {
        var predecessors = new List<int>[_blocks.Count];
        for (var i = 0; i < _blocks.Count; i++)
        {
            predecessors[i] = [];
        }

        for (var i = 0; i < _blocks.Count; i++)
        {
            foreach (var successor in _blocks[i].Successors)
            {
                predecessors[successor].Add(i);
            }
        }

        return predecessors;
    }

    private Dictionary<string, SqlTextValue>? ComputeBlockOutput(
        int index, int entryBlock, Dictionary<string, SqlTextValue> initialSeed, List<int>[] predecessors, Dictionary<string, SqlTextValue>?[] outStates, bool emit)
    {
        var merged = MergeEntry(index, entryBlock, initialSeed, predecessors, outStates);
        if (merged is null)
        {
            return null;
        }

        var working = new Dictionary<string, SqlTextValue>(merged, StringComparer.OrdinalIgnoreCase);

        if (emit && _guardTextByJoinBlock.ContainsKey(index))
        {
            ApplyGuardedAlternativeFixup(index, working, outStates);
            ApplyConstantConditionPruning(index, working, outStates);
        }

        foreach (var step in _blocks[index].Steps)
        {
            step(working, emit);
        }

        return working;
    }

private void ApplyGuardedAlternativeFixup(int joinBlock, Dictionary<string, SqlTextValue> working, Dictionary<string, SqlTextValue>?[] outStates)
    {
        var hasThen = _thenPredecessorByJoinBlock.TryGetValue(joinBlock, out var thenPredecessor);
        var hasElse = _elsePredecessorByJoinBlock.TryGetValue(joinBlock, out var elsePredecessor);
        var thenState = hasThen ? outStates[thenPredecessor] : null;
        var elseState = hasElse ? outStates[elsePredecessor] : null;
        if (thenState is null || elseState is null)
        {
            return;
        }

        var guardText = _guardTextByJoinBlock[joinBlock];
        foreach (var key in working.Keys.ToList())
        {
            var thenValue = thenState.GetValueOrDefault(key);
            var elseValue = elseState.GetValueOrDefault(key);

            if (thenValue is null || elseValue is null || SqlTextValue.StructurallyEqual(thenValue, elseValue))
            {
                continue;
            }

            var current = working[key];

            if (thenValue is SqlTextValue.Template thenTemplate && elseValue is SqlTextValue.Tainted elseTainted)
            {
                var declaredType = thenValue.DeclaredType ?? elseValue.DeclaredType;
                var tainted = new SqlTextValue.Tainted(elseTainted.Reason, elseTainted.Location) { DeclaredType = declaredType };
                current = SqlTextValue.WithGuardedAlternative(tainted, guardText, thenTemplate);
            }
            else if (elseValue is SqlTextValue.Template elseTemplate && thenValue is SqlTextValue.Tainted thenTainted)
            {
                var declaredType = thenValue.DeclaredType ?? elseValue.DeclaredType;
                var tainted = new SqlTextValue.Tainted(thenTainted.Reason, thenTainted.Location) { DeclaredType = declaredType };
                current = SqlTextValue.WithGuardedAlternative(tainted, guardText, elseTemplate);
            }
            else if (thenValue is SqlTextValue.Template thenTemplateBothResolved)
            {
                current = SqlTextValue.WithGuardedAlternative(current, guardText, thenTemplateBothResolved);
            }

            current = PropagateNestedGuardedAlternatives(current, elseValue);
            current = PropagateNestedGuardedAlternatives(current, thenValue);
            working[key] = current;
        }
    }

private static SqlTextValue PropagateNestedGuardedAlternatives(SqlTextValue current, SqlTextValue? branchValue) =>
        branchValue?.GuardedAlternatives is { Count: > 0 } nested
            ? nested.Aggregate(current, (value, alt) => SqlTextValue.WithGuardedAlternative(value, alt.GuardText, alt.Value))
            : current;

private void ApplyConstantConditionPruning(int joinBlock, Dictionary<string, SqlTextValue> working, Dictionary<string, SqlTextValue>?[] outStates)
    {
        if (!_conditionByJoinBlock.TryGetValue(joinBlock, out var condition)
            || !ConditionReferencesACallerSeededVariable(condition)
            || TryFoldBooleanCondition(condition, working) is not { } conditionValue)
        {
            return;
        }

        int? takenPredecessor = null;
        if (conditionValue && _thenPredecessorByJoinBlock.TryGetValue(joinBlock, out var thenPredecessor))
        {
            takenPredecessor = thenPredecessor;
        }
        else if (!conditionValue && _elsePredecessorByJoinBlock.TryGetValue(joinBlock, out var elsePredecessor))
        {
            takenPredecessor = elsePredecessor;
        }

        if (takenPredecessor is not { } predecessor || outStates[predecessor] is not { } takenState)
        {
            return;
        }

        foreach (var (key, value) in takenState)
        {
            working[key] = value;
        }
    }

private bool ConditionReferencesACallerSeededVariable(BooleanExpression condition)
    {
        if (_callerSeededVariableNames.Count == 0)
        {
            return false;
        }

        var collector = new VariableReferenceCollector();
        condition.Accept(collector);
        return collector.Names.Any(_callerSeededVariableNames.Contains);
    }

private bool? TryFoldBooleanCondition(BooleanExpression predicate, Dictionary<string, SqlTextValue> state) => predicate switch
    {
        BooleanParenthesisExpression paren => TryFoldBooleanCondition(paren.Expression, state),
        BooleanNotExpression not when TryFoldBooleanCondition(not.Expression, state) is { } inner => !inner,
        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and => CombineAnd(
            TryFoldBooleanCondition(and.FirstExpression, state), TryFoldBooleanCondition(and.SecondExpression, state)),
        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or => CombineOr(
            TryFoldBooleanCondition(or.FirstExpression, state), TryFoldBooleanCondition(or.SecondExpression, state)),
        BooleanComparisonExpression cmp
            when ExpressionEvaluator.FoldInteger(cmp.FirstExpression, state, _sourcePath, _cap, out var left)
                && ExpressionEvaluator.FoldInteger(cmp.SecondExpression, state, _sourcePath, _cap, out var right)
            => EvaluateComparison(cmp.ComparisonType, left, right),
        _ => null,
    };

    private static bool? CombineAnd(bool? left, bool? right)
    {
        if (left == false || right == false)
        {
            return false;
        }

        return left is true && right is true ? true : null;
    }

    private static bool? CombineOr(bool? left, bool? right)
    {
        if (left == true || right == true)
        {
            return true;
        }

        return left is false && right is false ? false : null;
    }

    private static bool? EvaluateComparison(BooleanComparisonType comparisonType, int left, int right) => comparisonType switch
    {
        BooleanComparisonType.Equals => left == right,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => left != right,
        BooleanComparisonType.GreaterThan => left > right,
        BooleanComparisonType.GreaterThanOrEqualTo => left >= right,
        BooleanComparisonType.LessThan => left < right,
        BooleanComparisonType.LessThanOrEqualTo => left <= right,
        BooleanComparisonType.NotGreaterThan => left <= right,
        BooleanComparisonType.NotLessThan => left >= right,
        _ => null,
    };

    private void RunFixpoint(int entryBlock, Dictionary<string, SqlTextValue> initialSeed, List<int>[] predecessors, Dictionary<string, SqlTextValue>?[] outStates)
    {
        for (var round = 0; round < MaxFixpointRounds; round++)
        {
            var changed = false;
            for (var i = 0; i < _blocks.Count; i++)
            {
                var working = ComputeBlockOutput(i, entryBlock, initialSeed, predecessors, outStates, emit: false);
                if (working is null)
                {
                    continue;
                }

                if (outStates[i] is null || !StatesEqual(outStates[i]!, working))
                {
                    changed = true;
                }

                outStates[i] = working;
            }

            if (!changed && round > 0)
            {
                break;
            }
        }
    }

private void RunFinalPass(int entryBlock, Dictionary<string, SqlTextValue> initialSeed, List<int>[] predecessors, Dictionary<string, SqlTextValue>?[] outStates, bool emit)
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            var working = ComputeBlockOutput(i, entryBlock, initialSeed, predecessors, outStates, emit);
            if (working is not null)
            {
                outStates[i] = working;
            }
        }
    }

    private static bool StatesEqual(Dictionary<string, SqlTextValue> a, Dictionary<string, SqlTextValue> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue) || !SqlTextValue.StructurallyEqual(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    private Dictionary<string, SqlTextValue> MergeExitStates(List<int> exitBlocks, Dictionary<string, SqlTextValue>?[] outStates)
    {
        Dictionary<string, SqlTextValue>? finalState = null;
        foreach (var exitBlock in exitBlocks)
        {
            if (outStates[exitBlock] is not { } exitState)
            {
                continue;
            }

            finalState = finalState is null
                ? new Dictionary<string, SqlTextValue>(exitState, StringComparer.OrdinalIgnoreCase)
                : MergeStateInto(finalState, exitState, guardText: string.Empty, SpanFor(exitBlock));
        }

        return finalState ?? new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, SqlTextValue>? MergeEntry(
        int block, int entryBlock, Dictionary<string, SqlTextValue> initialSeed, List<int>[] predecessors, Dictionary<string, SqlTextValue>?[] outStates)
    {
        Dictionary<string, SqlTextValue>? merged = block == entryBlock
            ? new Dictionary<string, SqlTextValue>(initialSeed, StringComparer.OrdinalIgnoreCase)
            : null;

        var guardText = _guardTextByJoinBlock.GetValueOrDefault(block, string.Empty);
        foreach (var predecessor in predecessors[block])
        {
            if (outStates[predecessor] is not { } predecessorState)
            {
                continue;
            }

            merged = merged is null
                ? new Dictionary<string, SqlTextValue>(predecessorState, StringComparer.OrdinalIgnoreCase)
                : MergeStateInto(merged, predecessorState, guardText, SpanFor(block));
        }

        return merged;
    }

    private SourceSpan SpanFor(int block) => _blockSpan.GetValueOrDefault(block, _defaultSpan);

    private Dictionary<string, SqlTextValue> MergeStateInto(Dictionary<string, SqlTextValue> a, Dictionary<string, SqlTextValue> b, string guardText, SourceSpan at)
    {
        var merged = new Dictionary<string, SqlTextValue>(a, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, bValue) in b)
        {
            merged[key] = merged.TryGetValue(key, out var aValue)
                ? SqlTextValue.Join(aValue, bValue, guardText, _cap, at)
                : bValue;
        }

        return merged;
    }

    private void PreRegisterLabels(IList<TSqlStatement> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LabelStatement label:
                    _labelBlocks[NormalizeLabel(label.Value)] = NewBlock();
                    break;

                case BeginEndBlockStatement block:
                    PreRegisterLabels(block.StatementList.Statements);
                    break;

                case IfStatement ifStatement:
                    PreRegisterLabels(NormalizeToStatementList(ifStatement.ThenStatement));
                    if (ifStatement.ElseStatement is not null)
                    {
                        PreRegisterLabels(NormalizeToStatementList(ifStatement.ElseStatement));
                    }

                    break;

                case WhileStatement whileStatement:
                    PreRegisterLabels(NormalizeToStatementList(whileStatement.Statement));
                    break;

                case TryCatchStatement tryCatch:
                    PreRegisterLabels(tryCatch.TryStatements.Statements);
                    PreRegisterLabels(tryCatch.CatchStatements.Statements);
                    break;
            }
        }
    }

    private static IList<TSqlStatement> NormalizeToStatementList(TSqlStatement statement) =>
        statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

private Action<Dictionary<string, SqlTextValue>, bool>? TryBuildSelfTrimBypassStep(IfStatement ifStatement, IReadOnlyList<string> activeGuards)
    {
        if (ifStatement.ElseStatement is not null
            || NormalizeToStatementList(ifStatement.ThenStatement) is not [SetVariableStatement setVar]
            || setVar.Expression is not FunctionCall { FunctionName.Value: var functionName, Parameters: [VariableReference sourceRef, var startExpr, var thirdArg] }
            || !string.Equals(functionName, "SUBSTRING", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceRef.Name, setVar.Variable.Name, StringComparison.OrdinalIgnoreCase)
            || !MatchesRecognizedTrimShape(startExpr, thirdArg, sourceRef.Name)
            || !IsPositiveLengthGuard(ifStatement.Predicate, setVar.Variable.Name))
        {
            return null;
        }

        var name = setVar.Variable.Name;
        var compiledStep = _compileLeaf(setVar, activeGuards);
        return (state, emit) =>
        {
            var prior = state.GetValueOrDefault(name);
            compiledStep(state, emit);

            if (prior is SqlTextValue.Template && state.TryGetValue(name, out var after) && after is SqlTextValue.Tainted)
            {
                state[name] = prior;
            }
        };
    }

private static bool MatchesRecognizedTrimShape(ScalarExpression startExpr, ScalarExpression thirdArg, string variableName) =>
        IsSelfLenReference(thirdArg, variableName)
        || (thirdArg is BinaryExpression { BinaryExpressionType: BinaryExpressionType.Subtract, FirstExpression: var lenSide }
            && IsSelfLenReference(lenSide, variableName)
            && startExpr is IntegerLiteral { Value: "1" });

    private static bool IsSelfLenReference(ScalarExpression expression, string variableName) =>
        expression is FunctionCall { FunctionName.Value: var lenName, Parameters: [VariableReference lenArgRef] }
        && string.Equals(lenName, "LEN", StringComparison.OrdinalIgnoreCase)
        && string.Equals(lenArgRef.Name, variableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsPositiveLengthGuard(BooleanExpression predicate, string variableName) => predicate switch
    {
        BooleanComparisonExpression
        {
            ComparisonType: BooleanComparisonType.GreaterThan,
            FirstExpression: FunctionCall { FunctionName.Value: var lenName, Parameters: [VariableReference lenRef] },
            SecondExpression: IntegerLiteral { Value: "0" },
        } => string.Equals(lenName, "LEN", StringComparison.OrdinalIgnoreCase) && string.Equals(lenRef.Name, variableName, StringComparison.OrdinalIgnoreCase),
        BooleanComparisonExpression
        {
            ComparisonType: BooleanComparisonType.GreaterThanOrEqualTo,
            FirstExpression: FunctionCall { FunctionName.Value: var lenName, Parameters: [VariableReference lenRef] },
            SecondExpression: IntegerLiteral { Value: "1" },
        } => string.Equals(lenName, "LEN", StringComparison.OrdinalIgnoreCase) && string.Equals(lenRef.Name, variableName, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static readonly Dictionary<string, SqlTextValue> EmptyLiteralFoldState = new(StringComparer.OrdinalIgnoreCase);

private Action<Dictionary<string, SqlTextValue>, bool>? TryBuildEqualityGuardedReturnNarrowingStep(IfStatement ifStatement, int joinBlock)
    {
        if (!_thenPredecessorByJoinBlock.ContainsKey(joinBlock)
            && TryGetSelfEqualityGuard(ifStatement.Predicate, out var variableName, out var literalExpression))
        {
            var literalValue = ExpressionEvaluator.Fold(literalExpression, EmptyLiteralFoldState, _sourcePath, _cap);
            return (state, _) =>
            {
                var declaredType = state.GetValueOrDefault(variableName)?.DeclaredType;
                state[variableName] = literalValue with { DeclaredType = declaredType };
            };
        }

        return null;
    }

    private static bool TryGetSelfEqualityGuard(BooleanExpression predicate, out string variableName, out ScalarExpression literalExpression)
    {
        if (predicate is BooleanComparisonExpression
            {
                ComparisonType: BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation,
            } comparison)
        {
            if (comparison.FirstExpression is VariableReference variable && comparison.SecondExpression is StringLiteral secondLiteral)
            {
                variableName = variable.Name;
                literalExpression = secondLiteral;
                return true;
            }

            if (comparison.SecondExpression is VariableReference variable2 && comparison.FirstExpression is StringLiteral firstLiteral)
            {
                variableName = variable2.Name;
                literalExpression = firstLiteral;
                return true;
            }
        }

        variableName = string.Empty;
        literalExpression = null!;
        return false;
    }

private int? BuildSequence(IList<TSqlStatement> statements, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
    {
        var reachable = true;
        foreach (var statement in statements)
        {
            if (!reachable)
            {
                current = NewBlock();
                reachable = true;
            }

            switch (statement)
            {
                case LabelStatement label:
                    var labelBlock = _labelBlocks[NormalizeLabel(label.Value)];
                    _blocks[current].Successors.Add(labelBlock);
                    current = labelBlock;
                    break;

                case GoToStatement goTo:
                    _blocks[current].Successors.Add(_labelBlocks[goTo.LabelName.Value]);
                    reachable = false;
                    break;

                case ReturnStatement:
                    exitBlocks.Add(current);
                    reachable = false;
                    break;

                case BeginEndBlockStatement block:
                    var afterBlock = BuildSequence(block.StatementList.Statements, current, exitBlocks, loopStack, activeGuards);
                    if (afterBlock is null)
                    {
                        reachable = false;
                    }
                    else
                    {
                        current = afterBlock.Value;
                    }

                    break;

                case IfStatement ifStatement:
                    current = BuildIfWithNarrowingBypasses(ifStatement, current, exitBlocks, loopStack, activeGuards);
                    break;

                case WhileStatement whileStatement:
                    current = BuildWhile(whileStatement, current, exitBlocks, loopStack, activeGuards);
                    break;

                case TryCatchStatement tryCatch:
                    current = BuildTryCatch(tryCatch, current, exitBlocks, loopStack, activeGuards);
                    break;

                case BreakStatement when loopStack.Count > 0:
                    _blocks[current].Successors.Add(loopStack.Peek().After);
                    reachable = false;
                    break;

                case ContinueStatement when loopStack.Count > 0:
                    _blocks[current].Successors.Add(loopStack.Peek().Header);
                    reachable = false;
                    break;

                default:
                    var captured = statement;
                    _blocks[current].Steps.Add(_compileLeaf(captured, activeGuards));
                    break;
            }
        }

        return reachable ? current : null;
    }

private int BuildIfWithNarrowingBypasses(IfStatement ifStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
    {
        if (TryBuildSelfTrimBypassStep(ifStatement, activeGuards) is { } bypassStep)
        {
            _blocks[current].Steps.Add(bypassStep);
            return current;
        }

        var join = BuildIf(ifStatement, current, exitBlocks, loopStack, activeGuards);
        if (TryBuildEqualityGuardedReturnNarrowingStep(ifStatement, join) is { } narrowingStep)
        {
            _blocks[join].Steps.Add(narrowingStep);
        }

        return join;
    }

    private int BuildIf(IfStatement ifStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
    {
        var guardText = FragmentTextRenderer.Render(ifStatement.Predicate);
        var thenEntry = NewBlock();
        _blocks[current].Successors.Add(thenEntry);
        var thenExit = BuildSequence(NormalizeToStatementList(ifStatement.ThenStatement), thenEntry, exitBlocks, loopStack, [.. activeGuards, guardText]);

        int? elseExit;
        if (ifStatement.ElseStatement is not null)
        {
            var elseEntry = NewBlock();
            _blocks[current].Successors.Add(elseEntry);
            elseExit = BuildSequence(NormalizeToStatementList(ifStatement.ElseStatement), elseEntry, exitBlocks, loopStack, activeGuards);
        }
        else
        {
            elseExit = current;
        }

        var join = NewBlock();
        _blockSpan[join] = Span(ifStatement);
        _guardTextByJoinBlock[join] = guardText;
        _conditionByJoinBlock[join] = ifStatement.Predicate;

        if (thenExit is { } te)
        {
            _blocks[te].Successors.Add(join);
            _thenPredecessorByJoinBlock[join] = te;
        }

        if (elseExit is { } ee)
        {
            _blocks[ee].Successors.Add(join);
            _elsePredecessorByJoinBlock[join] = ee;
        }

        return join;
    }

    private int BuildWhile(WhileStatement whileStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
    {
        var header = NewBlock();
        _blockSpan[header] = Span(whileStatement);
        _blocks[current].Successors.Add(header);

        var bodyEntry = NewBlock();
        var after = NewBlock();
        _blocks[header].Successors.Add(bodyEntry);
        _blocks[header].Successors.Add(after);

        var unboundedAccumulators = FindUnboundedAccumulators(whileStatement);
        if (unboundedAccumulators.Count > 0)
        {
            _blocks[bodyEntry].Steps.Add(SeedUnboundedAccumulatorTaint(unboundedAccumulators, whileStatement));
        }

        loopStack.Push((header, after));
        var bodyExit = BuildSequence(NormalizeToStatementList(whileStatement.Statement), bodyEntry, exitBlocks, loopStack, activeGuards);
        loopStack.Pop();

        if (bodyExit is { } be)
        {
            _blocks[be].Successors.Add(header);
        }

        return after;
    }

private static HashSet<string> FindUnboundedAccumulators(WhileStatement whileStatement)
    {
        var selfAccumulating = new SelfAccumulatingVariableCollector();
        whileStatement.Statement.Accept(selfAccumulating);
        if (selfAccumulating.Names.Count == 0)
        {
            return [];
        }

        var executed = new ExecutedVariableCollector();
        whileStatement.Statement.Accept(executed);

        selfAccumulating.Names.IntersectWith(executed.Names);
        return selfAccumulating.Names;
    }

private Action<Dictionary<string, SqlTextValue>, bool> SeedUnboundedAccumulatorTaint(IReadOnlyCollection<string> names, WhileStatement whileStatement)
    {
        var span = Span(whileStatement);
        var captured = names.ToArray();
        return (state, _) =>
        {
            foreach (var name in captured)
            {
                var declaredType = state.TryGetValue(name, out var existing) ? existing.DeclaredType : null;
                state[name] = new SqlTextValue.Tainted("while-loop-body:cardinality-cap", span) { DeclaredType = declaredType };
            }
        };
    }

    private sealed class SelfAccumulatingVariableCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(SetVariableStatement node)
        {
            var name = node.Variable.Name;
            if (node.AssignmentKind == AssignmentKind.AddEquals || (node.AssignmentKind == AssignmentKind.Equals && ReferencesVariable(node.Expression, name)))
            {
                Names.Add(name);
            }

            base.ExplicitVisit(node);
        }

        private static bool ReferencesVariable(ScalarExpression? expression, string name)
        {
            if (expression is null)
            {
                return false;
            }

            var visitor = new VariableReferenceCollector();
            expression.Accept(visitor);
            return visitor.Names.Contains(name);
        }
    }

    private sealed class ExecutedVariableCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(ExecuteStatement node)
        {
            var visitor = new VariableReferenceCollector();
            node.Accept(visitor);
            foreach (var name in visitor.Names)
            {
                Names.Add(name);
            }

            base.ExplicitVisit(node);
        }
    }

    private sealed class VariableReferenceCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(VariableReference node) => Names.Add(node.Name);
    }

    private int BuildTryCatch(TryCatchStatement tryCatch, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
    {
        var tryEntry = NewBlock();
        var catchEntry = NewBlock();
        _blocks[current].Successors.Add(tryEntry);

        _blocks[current].Successors.Add(catchEntry);

        _blocks[catchEntry].Steps.Add(SeedTryOnlyDeclarations(tryCatch));

        var tryExit = BuildSequence(tryCatch.TryStatements.Statements, tryEntry, exitBlocks, loopStack, activeGuards);
        var catchExit = BuildSequence(tryCatch.CatchStatements.Statements, catchEntry, exitBlocks, loopStack, activeGuards);

        var join = NewBlock();
        _blockSpan[join] = Span(tryCatch);

        if (tryExit is { } tex)
        {
            _blocks[tex].Successors.Add(join);
        }

        if (catchExit is { } cex)
        {
            _blocks[cex].Successors.Add(join);
        }

        return join;
    }

private Action<Dictionary<string, SqlTextValue>, bool> SeedTryOnlyDeclarations(TryCatchStatement tryCatch)
    {
        var collector = new DeclareVariableCollector();
        foreach (var statement in tryCatch.TryStatements.Statements)
        {
            statement.Accept(collector);
        }

        if (collector.Declarations.Count == 0)
        {
            return static (_, _) => { };
        }

        var span = Span(tryCatch);
        return (state, _) =>
        {
            foreach (var (name, type) in collector.Declarations)
            {
                if (!state.ContainsKey(name))
                {
                    state[name] = new SqlTextValue.Template([new TemplatePiece.Hole(type, span, HoleKind.TryOnlyDeclaration)]) { DeclaredType = type };
                }
            }
        };
    }

    private sealed class DeclareVariableCollector : TSqlFragmentVisitor
    {
        public List<(string Name, TypeInference.SqlType Type)> Declarations { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                var type = SqlTypeReferenceResolver.Resolve(element.DataType, columnCollation: null);
                if (type is not null)
                {
                    Declarations.Add((element.VariableName.Value, type));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
