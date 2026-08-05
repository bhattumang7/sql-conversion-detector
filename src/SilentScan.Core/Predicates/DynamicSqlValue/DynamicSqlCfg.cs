using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// The one dataflow engine for every T-SQL control-flow construct - IF, WHILE, TRY/CATCH,
/// BEGIN/END, GOTO/LABEL, BREAK/CONTINUE all lower to blocks and edges in the SAME graph, solved
/// by the SAME fixpoint, joined by the SAME <see cref="SqlTextValue.Join"/> operation. Replaces
/// the old scanner's split design, where this exact block/fixpoint machinery existed but ran
/// ONLY for a GOTO-bearing scope - every other scope used separate, hand-written
/// <c>HandleIf</c>/<c>HandleWhile</c>/<c>HandleTryCatch</c> methods, each with its own merge
/// logic and its own "guarded alternatives" correlation subsystem. That subsystem is gone here:
/// <see cref="SqlTextValue.Join"/> already merges two same-guard branches into one
/// <see cref="TemplatePiece.Choice"/> internally, so an IF's join block just needs to look up
/// which guard text governs it - no separate correlation machinery required.
/// <see cref="TSqlStatement"/>s this class does not itself understand (everything except IF/
/// WHILE/TRY-CATCH/BEGIN-END/GOTO/LABEL/BREAK/CONTINUE) are handed to the caller-supplied
/// leaf-compiler constructor delegate as opaque leaf steps - see
/// docs/dynamic-sql-rebuild-plan.md §4 for the transfer-function side of this split.
/// </summary>
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
    private readonly Func<TSqlStatement, Action<Dictionary<string, SqlTextValue>, bool>> _compileLeaf;
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<string, int> _labelBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _guardTextByJoinBlock = [];
    private readonly Dictionary<int, SourceSpan> _blockSpan = [];
    private SourceSpan _defaultSpan;

    /// <param name="sourcePath">The file this scope's statements came from - used to build a <see cref="SourceSpan"/> for a block whose own triggering statement isn't otherwise available.</param>
    /// <param name="cap">The per-join assembly cap forwarded to every <see cref="SqlTextValue.Join"/> call - the same <c>MaxAssembliesPerVariable</c> the old scanner used.</param>
    /// <param name="compileLeaf">Compiles one non-control-flow statement into a step - invoked with <c>emit: false</c> during the fixpoint (side effects like EXEC emission suppressed) and once more with <c>emit: true</c> once state has stabilized.</param>
    public DynamicSqlCfg(string sourcePath, int cap, Func<TSqlStatement, Action<Dictionary<string, SqlTextValue>, bool>> compileLeaf)
    {
        _sourcePath = sourcePath;
        _cap = cap;
        _compileLeaf = compileLeaf;
    }

    /// <summary>
    /// Solves <paramref name="statements"/> to the state every fall-through exit point converges
    /// to - what a caller reads a scope's OWN final variable states back from (e.g. an OUTPUT
    /// parameter's value at the end of a procedure body). RETURN and an unconditional GOTO each
    /// end their own block with no fallthrough successor, so they contribute nothing to the
    /// result - exiting through either one never reaches a scope's own implicit end.
    /// </summary>
    public Dictionary<string, SqlTextValue> Solve(IList<TSqlStatement> statements, Dictionary<string, SqlTextValue> initialSeed)
    {
        _defaultSpan = statements.Count > 0 ? Span(statements[0]) : new SourceSpan(_sourcePath, 1, 1);

        PreRegisterLabels(statements);
        var entryBlock = NewBlock();
        var exitBlocks = new List<int>();
        // The ONE place a fallthrough (as opposed to RETURN) completion adds to exitBlocks - see
        // BuildSequence's own doc comment for why it must not do this itself on every recursive
        // call (a THEN/ELSE/TRY/CATCH/loop body's own fallthrough is not a scope-level exit; it
        // is only ever consumed via BuildSequence's return value, by BuildIf/BuildWhile/
        // BuildTryCatch wiring it into their own join block).
        if (BuildSequence(statements, entryBlock, exitBlocks, new Stack<(int Header, int After)>()) is { } scopeFallthrough)
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
        foreach (var step in _blocks[index].Steps)
        {
            step(working, emit);
        }

        return working;
    }

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

    /// <summary>States are stable now - re-run once more with emission enabled. Same inputs, same steps, so this reproduces the exact same outputs; the only difference is that emission-gated side effects (EXEC/output-summary recording) are no longer suppressed.</summary>
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

        // A variable A has but B doesn't (declared only on A's path) is exactly as unresolved on
        // B's path as an explicit Tainted would be - Join already treats "missing" and "declared
        // fresh on one branch only" identically by simply keeping A's own value unmerged here,
        // matching the old scanner's TryMergeFreshlyDeclaredInOneBranchOnly for the common case
        // where the fresh declaration's own type recovers a typed Hole on the OTHER path only
        // if a later read joins it against something else - this method itself does not need to
        // manufacture that Hole, since a variable A has but B doesn't was never live on B's path
        // to begin with (T-SQL requires DECLARE before any read, so it's structurally unreadable
        // there) and reaching this point with mismatched keys just means "B's path never touched
        // it" - keeping A's value is correct, not a widening.
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

    /// <summary>
    /// Links <paramref name="statements"/> into the block graph starting at <paramref
    /// name="current"/>, returning the block execution falls through to afterward, or null when
    /// this sequence can never fall through (RETURN, an unconditional GOTO, or every branch of
    /// its last construct exits some other way). Deliberately does NOT add its own fallthrough
    /// block to <paramref name="exitBlocks"/> - only a RETURN does that, since RETURN is the one
    /// way execution can leave the CURRENT scope from PARTWAY through a nested THEN/ELSE/TRY/
    /// CATCH/loop body. An ordinary fallthrough completion is never itself a scope-level exit: it
    /// is consumed ONLY via this method's own return value, by whichever caller invoked it
    /// (<see cref="BuildIf"/>/<see cref="BuildWhile"/>/<see cref="BuildTryCatch"/> wiring it into
    /// their own join block, or <see cref="Solve"/> itself for the outermost call). Adding it to
    /// the shared <paramref name="exitBlocks"/> list here too - which an earlier version of this
    /// class did - double-counts every nested branch's own fallthrough as an INDEPENDENT
    /// scope-level exit, corrupting <see cref="MergeExitStates"/>'s result with stale,
    /// already-superseded intermediate states once the branch's value has moved on past its own
    /// join point.
    /// </summary>
    private int? BuildSequence(IList<TSqlStatement> statements, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
    {
        var reachable = true;
        foreach (var statement in statements)
        {
            if (!reachable)
            {
                // Dead code unless a label here makes it reachable via GOTO - resume building
                // into a fresh, currently-unlinked block so a label buried inside still gets its
                // own contents populated correctly, without spuriously wiring it as a successor
                // of whatever came before.
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
                    var afterBlock = BuildSequence(block.StatementList.Statements, current, exitBlocks, loopStack);
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
                    current = BuildIf(ifStatement, current, exitBlocks, loopStack);
                    break;

                case WhileStatement whileStatement:
                    current = BuildWhile(whileStatement, current, exitBlocks, loopStack);
                    break;

                case TryCatchStatement tryCatch:
                    current = BuildTryCatch(tryCatch, current, exitBlocks, loopStack);
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
                    _blocks[current].Steps.Add(_compileLeaf(captured));
                    break;
            }
        }

        return reachable ? current : null;
    }

    private int BuildIf(IfStatement ifStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
    {
        var thenEntry = NewBlock();
        _blocks[current].Successors.Add(thenEntry);
        var thenExit = BuildSequence(NormalizeToStatementList(ifStatement.ThenStatement), thenEntry, exitBlocks, loopStack);

        int? elseExit;
        if (ifStatement.ElseStatement is not null)
        {
            var elseEntry = NewBlock();
            _blocks[current].Successors.Add(elseEntry);
            elseExit = BuildSequence(NormalizeToStatementList(ifStatement.ElseStatement), elseEntry, exitBlocks, loopStack);
        }
        else
        {
            // No ELSE: the condition being false falls straight through from the IF's own block.
            elseExit = current;
        }

        var join = NewBlock();
        _blockSpan[join] = Span(ifStatement);
        _guardTextByJoinBlock[join] = FragmentTextRenderer.Render(ifStatement.Predicate);

        if (thenExit is { } te)
        {
            _blocks[te].Successors.Add(join);
        }

        if (elseExit is { } ee)
        {
            _blocks[ee].Successors.Add(join);
        }

        return join;
    }

    private int BuildWhile(WhileStatement whileStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
    {
        var header = NewBlock();
        _blockSpan[header] = Span(whileStatement);
        _blocks[current].Successors.Add(header);

        var bodyEntry = NewBlock();
        var after = NewBlock();
        _blocks[header].Successors.Add(bodyEntry);
        _blocks[header].Successors.Add(after);

        loopStack.Push((header, after));
        var bodyExit = BuildSequence(NormalizeToStatementList(whileStatement.Statement), bodyEntry, exitBlocks, loopStack);
        loopStack.Pop();

        if (bodyExit is { } be)
        {
            _blocks[be].Successors.Add(header);
        }

        return after;
    }

    private int BuildTryCatch(TryCatchStatement tryCatch, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
    {
        var tryEntry = NewBlock();
        var catchEntry = NewBlock();
        _blocks[current].Successors.Add(tryEntry);

        // CATCH only runs if TRY throws mid-way, so how far TRY got is unknowable - an edge
        // straight from the PRE-TRY block, never from any point inside TRY itself.
        _blocks[current].Successors.Add(catchEntry);

        var tryExit = BuildSequence(tryCatch.TryStatements.Statements, tryEntry, exitBlocks, loopStack);
        var catchExit = BuildSequence(tryCatch.CatchStatements.Statements, catchEntry, exitBlocks, loopStack);

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
}
