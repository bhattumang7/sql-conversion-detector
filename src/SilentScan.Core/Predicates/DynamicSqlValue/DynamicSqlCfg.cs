using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
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
/// logic. <see cref="SqlTextValue.Join"/> already merges two same-guard branches into one
/// <see cref="TemplatePiece.Choice"/> internally when BOTH sides resolve to a real value, so an
/// IF's join block just needs to look up which guard text governs it for that case - no separate
/// machinery required. When only ONE side resolves (the other taints - a genuinely common shape:
/// "if X, append known text; else, read from a table"), <see cref="SqlTextValue.Join"/> itself
/// stays branch-agnostic (it has no notion of "then" vs "else"), so THIS class applies
/// <see cref="SqlTextValue.WithGuardedAlternative"/> separately, in <see cref="ApplyGuardedAlternativeFixup"/> -
/// the one place branch identity (<see cref="_thenPredecessorByJoinBlock"/>) is still known,
/// preserving the old scanner's own guarded-alternatives capability without baking
/// branch-awareness into the general-purpose <see cref="SqlTextValue.Join"/> that every OTHER
/// join point (loop back-edges, TRY/CATCH, GOTO convergence) also uses.
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
    private readonly Func<TSqlStatement, IReadOnlyList<string>, Action<Dictionary<string, SqlTextValue>, bool>> _compileLeaf;
    private static readonly string[] NoActiveGuards = [];
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<string, int> _labelBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _guardTextByJoinBlock = [];
    private readonly Dictionary<int, int> _thenPredecessorByJoinBlock = [];
    private readonly Dictionary<int, int> _elsePredecessorByJoinBlock = [];
    private readonly Dictionary<int, SourceSpan> _blockSpan = [];
    private SourceSpan _defaultSpan;

    /// <param name="sourcePath">The file this scope's statements came from - used to build a <see cref="SourceSpan"/> for a block whose own triggering statement isn't otherwise available.</param>
    /// <param name="cap">The per-join assembly cap forwarded to every <see cref="SqlTextValue.Join"/> call - the same <c>MaxAssembliesPerVariable</c> the old scanner used.</param>
    /// <param name="compileLeaf">Compiles one non-control-flow statement into a step - invoked with <c>emit: false</c> during the fixpoint (side effects like EXEC emission suppressed) and once more with <c>emit: true</c> once state has stabilized.</param>
    public DynamicSqlCfg(string sourcePath, int cap, Func<TSqlStatement, IReadOnlyList<string>, Action<Dictionary<string, SqlTextValue>, bool>> compileLeaf)
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

        // BEFORE this block's own steps run, not after: BuildSequence keeps appending whatever
        // statements FOLLOW an IF as steps on that SAME join block (no new block boundary), so a
        // statement immediately after the IF - `SET @x = @x + '...'`, or the EXEC itself - reads
        // straight out of `working`. Fixing it up only AFTER the steps ran would be one statement
        // too late: the very first following statement would already have consumed the
        // un-recovered value. Only in the final pass: by then the fixpoint has already fully
        // converged, so both predecessors' own outStates entries (read here, however this block's
        // own index relates to either of them) are already STABLE values.
        if (emit && _guardTextByJoinBlock.ContainsKey(index))
        {
            ApplyGuardedAlternativeFixup(index, working, outStates);
        }

        foreach (var step in _blocks[index].Steps)
        {
            step(working, emit);
        }

        return working;
    }

    /// <summary>
    /// Restores the old scanner's guarded-alternatives capability, generalized beyond its
    /// original single case. Reads both predecessors' OWN raw values directly - never
    /// <paramref name="working"/>'s already-merged result for the key, which can ALREADY be a
    /// generic typed <see cref="HoleKind.WidenedChoice"/> hole or a live
    /// <see cref="TemplatePiece.Choice"/> rather than <see cref="SqlTextValue.Tainted"/>
    /// (<see cref="SqlTextValue.Join"/>'s own uniform-declared-type/Choice-merge recovery fires
    /// whenever both branches resolve to real Templates - the overwhelmingly common case for one
    /// variable reassigned on both sides of the SAME IF - which would otherwise pre-empt this
    /// fixup entirely). Three cases: (1) exactly one branch resolves and the other is
    /// <see cref="SqlTextValue.Tainted"/> - OVERRIDES <paramref name="working"/> with a fresh
    /// Tainted carrying the known branch as a <see cref="GuardedAlternative"/>, so an EXEC fed
    /// this value can recover the exact known text instead of an unresolvable placeholder; (2)
    /// the THEN branch resolves (regardless of what the ELSE branch or the general Join produced)
    /// - its own value is ALSO attached as a GuardedAlternative directly onto whatever
    /// <paramref name="working"/> already is (a live Choice, a widened Hole, ...), so a LATER
    /// EXEC guarded by the EXACT SAME predicate text can narrow straight to it even when the
    /// overall merged value is perfectly usable on its own; (3) either branch's OWN value already
    /// carries GuardedAlternatives from a NESTED join (an "ELSE IF" chain's own inner guard) -
    /// Join never carries these forward on its own (a freshly merged value starts with none), so
    /// they are re-attached here, letting a later EXEC narrow against a guard several IF/ELSE-IF
    /// arms back. All three are additive, side-channel-only (see <see cref="SqlTextValue.GuardedAlternatives"/>) -
    /// see <see cref="DynamicSqlTransfer"/>'s own EmitScriptsOrFinding for the consuming side:
    /// narrowing only actually happens when a LATER EXEC's own active guard stack exactly matches
    /// one of these tags (soundness-first exact-text matching, never implication).
    /// </summary>
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

            // Nothing diverged for this variable at THIS join: either one side never even set it
            // (never live on the other branch - see MergeStateInto's own doc comment, not a
            // divergence), or both branches agree completely (Join's own StructurallyEqual
            // shortcut already returned the shared value unchanged, so `working[key]` already
            // equals it exactly - attaching it to itself as its own alternative is pure
            // redundancy). This is the OVERWHELMING common case across the many non-dynamic-SQL
            // variables a large real-world proc tracks (most IF statements don't touch most
            // variables), so skipping it here - rather than doing WithGuardedAlternative/
            // propagation work per key regardless of whether anything actually diverged - is
            // what keeps this fixup's cost proportional to ACTUAL divergence instead of
            // O(tracked variables x IF joins): a real corpus repo with a genuinely huge proc
            // (thousands of DECLAREs/IFs) blew this up to tens of GB before this early-out.
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

            // elseValue's own nested alternatives apply FIRST, thenValue's SECOND (so thenValue
            // wins a guard-text collision) - the common real shape motivating this ordering is an
            // unconditional-no-else `IF g SET @x = f(@x)` where BOTH branches trace back to the
            // SAME ancestor guard's alternative (elseValue = the pre-@x-assignment snapshot,
            // thenValue = that snapshot after f's own transfer function ran on top of it, e.g. a
            // trim - see ExpressionEvaluator.TryTrimThroughAlternatives): thenValue's copy is
            // strictly more refined/up to date for that guard, never less correct, so it must not
            // be clobbered back to the pre-transfer value by applying elseValue's copy last.
            current = PropagateNestedGuardedAlternatives(current, elseValue);
            current = PropagateNestedGuardedAlternatives(current, thenValue);
            working[key] = current;
        }
    }

    /// <summary>Re-attaches any GuardedAlternatives <paramref name="branchValue"/> already carries (from a NESTED join further up an "ELSE IF" chain) onto <paramref name="current"/> - see <see cref="ApplyGuardedAlternativeFixup"/>'s own doc comment, case 3.</summary>
    private static SqlTextValue PropagateNestedGuardedAlternatives(SqlTextValue current, SqlTextValue? branchValue) =>
        branchValue?.GuardedAlternatives is { Count: > 0 } nested
            ? nested.Aggregate(current, (value, alt) => SqlTextValue.WithGuardedAlternative(value, alt.GuardText, alt.Value))
            : current;

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
    /// Recognizes the narrow, no-ELSE <c>IF LEN(x) &gt; 0 SET x = SUBSTRING(x, ...)</c> idiom (a
    /// common "strip a fixed number of characters, but only when there's enough length to do so"
    /// guard wrapped around one of <see cref="ExpressionEvaluator"/>'s own already-sound trim
    /// folds - e.g. stripping a trailing separator left by repeated concatenation) and compiles
    /// it as ONE unconditional step instead of a real branch. This is necessary because
    /// <see cref="BuildIf"/>'s ordinary join unions the THEN branch's (correctly trimmed) result
    /// with the implicit ELSE branch's (x's own UNCHANGED, still-untrimmed) result - sound in
    /// general (both are real, independently-possible runtime outcomes), but NOT here: the exact
    /// same "does x have enough trailing/leading literal content" test decides BOTH whether the
    /// guard is true AND whether the trim fold itself succeeds (see
    /// <see cref="ExpressionEvaluator.TryTrimThroughAlternatives"/>'s own "drop-too-short-
    /// alternative" policy), so joining them produces stale untrimmed duplicates of every
    /// already-correctly-trimmed candidate rather than two genuinely different outcomes -
    /// confirmed against a real corpus site where this collapsed 31 candidate scripts (16
    /// still carrying the untrimmed trailing comma, all failing to parse) down to the 15 that
    /// were already correct.
    /// </summary>
    /// <remarks>
    /// Falls back to x's own PRIOR value - never inventing a value, never assuming the guard was
    /// true when the trim fold itself can't prove it - whenever that fold declines, which keeps
    /// this sound even when the trim can't be resolved (x stays exactly what it would have been
    /// had this whole IF been skipped, matching the guard-false outcome). Deliberately narrow:
    /// the THEN statement must be exactly one <c>SET</c> assigning a <c>SUBSTRING(x, ..., LEN(x)
    /// [-k])</c> call BACK onto the SAME variable x that the guard's own <c>LEN(...)</c> tests -
    /// this is what guarantees the trim-succeeds/guard-true correlation this bypass relies on.
    /// Anything else (an ELSE branch, a BEGIN/END body with more than one statement, a different
    /// RHS shape, a guard testing a different variable) returns null and falls back to the
    /// ordinary branch/join construction, completely unchanged.
    /// </remarks>
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

            // Only revert a genuine LOSS: a fully-known prior Template collapsing to a Tainted
            // decline (the trim couldn't find any literal content to trim, i.e. x's prior value
            // was already empty - matching the guard-false outcome exactly). When prior was
            // ALREADY Tainted, the compiled step's own TryTrimThroughAlternatives fallback (which
            // also trims through a Tainted value's own GuardedAlternatives) may have refined it -
            // e.g. narrowing a stale untrimmed GuardedAlternative to a trimmed one - and that
            // refinement must be kept, not discarded back to the stale prior.
            if (prior is SqlTextValue.Template && state.TryGetValue(name, out var after) && after is SqlTextValue.Tainted)
            {
                state[name] = prior;
            }
        };
    }

    /// <summary>
    /// True for exactly the three SUBSTRING(x, start, length) shapes <see cref="ExpressionEvaluator.Fold"/>
    /// already recognizes as sound self-trims of <paramref name="variableName"/>: <c>SUBSTRING(x, n, LEN(x))</c>
    /// (leading trim, n &gt;= 1), <c>SUBSTRING(x, 0, LEN(x))</c> (drop the last character), and
    /// <c>SUBSTRING(x, 1, LEN(x) - k)</c> (trailing trim - the idiom real corpus code overwhelmingly
    /// uses to strip a trailing separator). Deliberately checks only the STRUCTURAL shape, not the
    /// concrete start/count values - <see cref="ExpressionEvaluator"/>'s own fold re-validates those
    /// (e.g. that <c>n &gt;= 1</c>) when the compiled step actually runs, and any shape it declines
    /// falls back to x's prior value exactly like every other decline this bypass handles.
    /// </summary>
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

    /// <summary>
    /// Recognizes the "parameter validation" idiom real corpus procs use to narrow an input
    /// parameter to one exact, known literal for the rest of the routine: <c>IF x &lt;&gt; 'literal'
    /// BEGIN RAISERROR(...) RETURN ... END</c> (a guard clause rejecting any other value, found
    /// via dbo.spRelationshipReconcileSharedTripByOdometer's own <c>@SourceTable</c> validation).
    /// Ordinarily this scanner never narrows a variable's value from a comparison - a T-SQL IF
    /// doesn't literally assign anything - but THIS shape is different: <see cref="BuildIf"/>'s
    /// own reachability computation (not re-derived here - read directly off
    /// <see cref="_thenPredecessorByJoinBlock"/>, the exact ground truth it already computed) can
    /// prove the THEN branch never reaches the join at all (it always exits the routine via
    /// RETURN), so the ONLY way execution reaches anything AFTER the IF is via the guard's
    /// predicate being FALSE - which for <c>x &lt;&gt; 'literal'</c> means x provably EQUALS
    /// 'literal' downstream, a hard fact, not a guess. Applies regardless of whether an explicit
    /// ELSE exists (same argument holds either way) or which operand order the literal appears in.
    /// Deliberately does NOT re-run <see cref="BuildSequence"/> on the THEN body itself to check
    /// this (that would double-register its blocks/labels) - it reads the fact BuildIf already
    /// established as a side effect of building the real branch.
    /// </summary>
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
    private int? BuildSequence(IList<TSqlStatement> statements, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack, IReadOnlyList<string> activeGuards)
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

    /// <summary>
    /// Wraps <see cref="BuildIf"/> with the two narrow, self-contained recognizers that bypass or
    /// augment its ordinary branch/join construction - <see cref="TryBuildSelfTrimBypassStep"/>
    /// (compiles a self-guarded SUBSTRING trim as one unconditional step instead of a real
    /// branch) and <see cref="TryBuildEqualityGuardedReturnNarrowingStep"/> (adds a value-
    /// narrowing step after an ordinary guard-clause IF whose THEN branch always RETURNs).
    /// Factored out of <see cref="BuildSequence"/>'s own switch purely to keep that method's
    /// cognitive complexity within the Sonar gate - no behavioral difference from inlining it.
    /// </summary>
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
            // No ELSE: the condition being false falls straight through from the IF's own block.
            elseExit = current;
        }

        var join = NewBlock();
        _blockSpan[join] = Span(ifStatement);
        _guardTextByJoinBlock[join] = guardText;

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

    /// <summary>
    /// A variable both (1) self-referentially reassigned somewhere inside this loop's own body
    /// (<c>SET @x += ...</c> or <c>SET @x = @x + ...</c>) and (2) read by an EXEC/sp_executesql
    /// call somewhere inside the SAME body is a genuine unbounded accumulator this scanner
    /// cannot enumerate: this dataflow engine tracks per-round STATE, never trip counts, so
    /// nothing here bounds how many times the loop actually runs - each additional run appends
    /// more text, and a fixpoint over that shape either never converges or converges to an
    /// arbitrary intermediate snapshot purely as an artifact of how many rounds
    /// <see cref="RunFixpoint"/> happens to take, neither of which is a sound answer for what the
    /// variable could ever actually hold. Detected structurally (self-reference + a same-body
    /// EXEC), not by inspecting string content - consistent with CLAUDE.md's "no heuristic
    /// string guessing": this looks at program STRUCTURE, the same way every other shape in this
    /// class (IF/TRY-CATCH/GOTO) is recognized syntactically rather than guessed.
    /// </summary>
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

    /// <summary>
    /// Forces every name in <paramref name="names"/> to a fresh <see cref="SqlTextValue.Tainted"/>
    /// carrying the "while-loop-body:cardinality-cap" reason as the FIRST step of the loop body's
    /// own entry block, on every fixpoint round (both suppressed and the final emitting pass) -
    /// this both makes the loop's own EXEC read decline with the correct, specific reason and
    /// keeps the value stable across rounds (a repeated identical Tainted trivially satisfies
    /// <see cref="StatesEqual"/>, so this cannot itself prevent the surrounding fixpoint from
    /// converging), rather than letting the ordinary Join/Widen machinery either race to
    /// <see cref="MaxFixpointRounds"/> or accidentally stabilize on an arbitrary
    /// under-cap intermediate snapshot.
    /// </summary>
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

        // CATCH only runs if TRY throws mid-way, so how far TRY got is unknowable - an edge
        // straight from the PRE-TRY block, never from any point inside TRY itself.
        _blocks[current].Successors.Add(catchEntry);

        // T-SQL locals are batch/proc scoped, not block-scoped: a variable DECLAREd only inside
        // TRY (the classic "log the dynamic SQL that just failed" pattern) is still legal to
        // reference from CATCH, since storage for every local is allocated at parse time
        // regardless of whether the DECLARE line itself ever ran. Seeded as its own step on
        // catchEntry - prepended before BuildSequence appends CATCH's own statements below, so it
        // runs before anything that might reference the name.
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

    /// <summary>
    /// Every DECLARE anywhere inside the TRY block (at any nesting depth - a batch-scoped local
    /// declared inside a nested IF/WHILE within TRY is still visible from CATCH) whose type
    /// resolves, seeded as a typed <see cref="HoleKind.TryOnlyDeclaration"/> placeholder the
    /// moment CATCH's own state doesn't already have an entry for that name - i.e. only when
    /// nothing outside TRY (a pre-TRY DECLARE, or an outer-scope formal parameter) already
    /// provides one. An untyped DECLARE (its <see cref="Catalog.SqlType"/> didn't resolve) is left
    /// exactly as unseeded as it always was; this only recovers the case the old scanner recovered.
    /// </summary>
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
        public List<(string Name, Catalog.SqlType Type)> Declarations { get; } = [];

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
