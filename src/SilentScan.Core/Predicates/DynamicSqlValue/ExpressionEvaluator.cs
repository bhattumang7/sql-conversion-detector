using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// Folds a T-SQL scalar expression to a <see cref="SqlTextValue"/> against a variable state -
/// replaces the old scanner's <c>TryFoldExpression</c>/<c>TryFoldIntegerLiteral</c> dispatch.
/// Builtin-specific knowledge lives entirely in <see cref="BuiltinRegistry"/>; this class's own
/// job is purely mechanical: recurse into an expression tree, resolve each argument to a
/// <see cref="BuiltinArgument"/>, and hand the result to the registry - see
/// docs/dynamic-sql-rebuild-plan.md §3/§4.
/// </summary>
public static class ExpressionEvaluator
{
    private const string FnLeft = "LEFT";
    private const string FnRight = "RIGHT";
    private const string FnIsNull = "ISNULL";

    /// <summary>Folds a scalar expression to its <see cref="SqlTextValue"/> - a <see cref="SqlTextValue.Template"/> (possibly with holes/choices) or <see cref="SqlTextValue.Tainted"/> with a machine-readable reason, never a guess.</summary>
    public static SqlTextValue Fold(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        switch (expression)
        {
            case StringLiteral literal:
                var prefixLength = literal.IsNational ? 2 : 1;
                return new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, Span(sourcePath, literal), prefixLength)]);

            case VariableReference variableRef:
                return state.TryGetValue(variableRef.Name, out var value)
                    ? value
                    : new SqlTextValue.Tainted("variable-not-in-scope", Span(sourcePath, variableRef));

            case ParenthesisExpression paren:
                return Fold(paren.Expression, state, sourcePath, cap);

            // ScriptDOM can wrap an operand in a UnaryExpression carrying UnaryExpressionType.Positive
            // purely as an artifact of how it resolves adjacent tokens. Unary plus has no real effect
            // on a string operand, so folding through to the inner expression is exact.
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                return Fold(unary.Expression, state, sourcePath, cap);

            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary:
                return FoldConcatenation(binary, state, sourcePath, cap);

            case BinaryExpression:
                return new SqlTextValue.Tainted("non-literal-expression:unsupported-operator", Span(sourcePath, expression));

            case FunctionCall { FunctionName.Value: var functionName } isNullCall
                when string.Equals(functionName, FnIsNull, StringComparison.OrdinalIgnoreCase) && isNullCall.Parameters.Count == 2:
                // ISNULL(a, b): whenever `a` folds at all, that value is PROVABLY non-NULL - a
                // variable folds to a real value only by tracing a real literal/DECLARE/SET
                // chain, and a bare `SET @x = NULL` never folds (no NullLiteral case here) rather
                // than being treated as some placeholder value. `b` is never even inspected.
                return Fold(isNullCall.Parameters[0], state, sourcePath, cap);

            case CoalesceExpression { Expressions.Count: > 0 } coalesce:
                return Fold(coalesce.Expressions[0], state, sourcePath, cap);

            case FunctionCall functionCall:
                return FoldFunctionCall(functionCall.FunctionName.Value, functionCall.Parameters, functionCall, state, sourcePath, cap);

            case LeftFunctionCall leftCall:
                return FoldFunctionCall(FnLeft, leftCall.Parameters, leftCall, state, sourcePath, cap);

            case RightFunctionCall rightCall:
                return FoldFunctionCall(FnRight, rightCall.Parameters, rightCall, state, sourcePath, cap);

            case CastCall castCall:
                return FoldCastOrConvert(castCall.Parameter, castCall.DataType, castCall, state, sourcePath, cap);

            case ConvertCall convertCall:
                return FoldCastOrConvert(convertCall.Parameter, convertCall.DataType, convertCall, state, sourcePath, cap);

            case SimpleCaseExpression or SearchedCaseExpression or IIfCall:
                return FoldConditional(expression, state, sourcePath, cap);

            case ColumnReferenceExpression:
                return new SqlTextValue.Tainted("non-literal-expression:column-reference", Span(sourcePath, expression));

            case ScalarSubquery { QueryExpression: QuerySpecification { FromClause: not null } }:
                // A subquery reading a real FROM clause has its value living in a database row,
                // not anywhere in the source file - this can never fold without reading real
                // table data (forbidden for corpus code, CLAUDE.md).
                return new SqlTextValue.Tainted("non-literal-expression:sql-loaded-from-table", Span(sourcePath, expression));

            case ScalarSubquery:
                return new SqlTextValue.Tainted("non-literal-expression:subquery", Span(sourcePath, expression));

            default:
                return new SqlTextValue.Tainted("non-literal-expression:other", Span(sourcePath, expression));
        }
    }

    /// <summary>
    /// Always folds BOTH operands before delegating to <see cref="SqlTextValue.Concat"/> - folding
    /// is pure (reads <paramref name="state"/>, never mutates it), so there is no cost to skip by
    /// short-circuiting on a tainted left operand, and <see cref="SqlTextValue.Concat"/> itself
    /// needs the right operand's own value to extend a tainted left operand's own
    /// <see cref="SqlTextValue.GuardedAlternatives"/> (a short-circuit here would silently drop
    /// that extension, discarding a real, recoverable value for no benefit).
    /// </summary>
    private static SqlTextValue FoldConcatenation(BinaryExpression binary, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var left = Fold(binary.FirstExpression, state, sourcePath, cap);
        var right = Fold(binary.SecondExpression, state, sourcePath, cap);
        return SqlTextValue.Concat(left, right);
    }

    private static SqlTextValue FoldConditional(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        // SearchedCaseExpression/IIfCall each have a real BooleanExpression condition this
        // evaluator CAN sometimes prove true or false outright (see TryEvaluatePredicate) -
        // when it can, the whole conditional folds to exactly the branch T-SQL's own
        // short-circuit semantics would take, instead of unioning every branch as if all were
        // reachable. SimpleCaseExpression compares an input expression against several VALUES
        // (a different shape - no single boolean predicate to evaluate per branch) and keeps the
        // existing union-every-branch behavior below unconditionally (whenClauses stays null).
        IReadOnlyList<(BooleanExpression Condition, ScalarExpression Then)>? whenClauses = null;
        IEnumerable<ScalarExpression> thenExpressions = [];
        ScalarExpression? elseExpression = null;
        switch (expression)
        {
            case SimpleCaseExpression simpleCase:
                thenExpressions = simpleCase.WhenClauses.Select(w => w.ThenExpression);
                elseExpression = simpleCase.ElseExpression;
                break;

            case SearchedCaseExpression searchedCase:
                whenClauses = searchedCase.WhenClauses.Select(w => (w.WhenExpression, w.ThenExpression)).ToList();
                elseExpression = searchedCase.ElseExpression;
                break;

            case IIfCall iif:
                whenClauses = [(iif.Predicate, iif.ThenExpression)];
                elseExpression = iif.ElseExpression;
                break;
        }

        // A bare CASE with no matching WHEN and no ELSE returns SQL NULL, which this domain has
        // no representation for - silently omitting that outcome from the union would be
        // unsound, not merely imprecise, so this declines instead of guessing.
        if (elseExpression is null)
        {
            return new SqlTextValue.Tainted("non-literal-expression:conditional", Span(sourcePath, expression));
        }

        var at = Span(sourcePath, expression);
        var remainingBranches = thenExpressions;
        if (whenClauses is not null)
        {
            remainingBranches = ResolveDeterminableBranches(whenClauses, state, sourcePath, cap, out var decided);
            if (decided is { } decidedBranch)
            {
                return Fold(decidedBranch, state, sourcePath, cap);
            }
        }

        SqlTextValue? union = null;
        foreach (var branch in remainingBranches.Append(elseExpression))
        {
            var folded = Fold(branch, state, sourcePath, cap);
            if (folded is SqlTextValue.Tainted)
            {
                return new SqlTextValue.Tainted("non-literal-expression:conditional", at);
            }

            union = union is null ? folded : SqlTextValue.Join(union, folded, guardText: string.Empty, cap, at);
        }

        return union!;
    }

    /// <summary>
    /// Walks <paramref name="whenClauses"/> in T-SQL's own short-circuit order: a WHEN whose
    /// condition provably evaluates FALSE is excluded entirely (never a real possibility, so
    /// including it in the union would be needless imprecision - the bug this method fixes: a
    /// corpus CASE guarding <c>QUOTENAME(@col)</c> behind <c>COALESCE(@col, N'') &lt;&gt; N''</c>
    /// was including the QUOTENAME branch even when @col provably folded to the SAME empty
    /// literal the guard checks against, producing an invalid empty-bracket <c>[]</c> in the
    /// "fully known" reconstructed SQL); a WHEN provably TRUE means every EARLIER WHEN was
    /// provably false (or this loop would have already returned) and no LATER WHEN/ELSE can ever
    /// run - <paramref name="decided"/> carries that THEN expression out for the caller to fold
    /// as the whole conditional's own answer, short-circuiting immediately. The first WHEN whose
    /// condition can't be determined either way stops the walk: from there on, everything
    /// (that WHEN's own THEN, every later WHEN's THEN, and ELSE) is genuinely still reachable and
    /// returned for the caller's existing union-every-remaining-branch fallback.
    /// </summary>
    private static List<ScalarExpression> ResolveDeterminableBranches(
        IReadOnlyList<(BooleanExpression Condition, ScalarExpression Then)> whenClauses, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out ScalarExpression? decided)
    {
        decided = null;
        var remaining = new List<ScalarExpression>();
        foreach (var (condition, then) in whenClauses)
        {
            var determined = TryEvaluatePredicate(condition, state, sourcePath, cap);
            if (determined == true)
            {
                decided = then;
                return [];
            }

            if (determined == false)
            {
                continue;
            }

            remaining.Add(then);
        }

        return remaining;
    }

    /// <summary>
    /// Proves a WHEN condition true/false only for the one shape this corpus actually needs:
    /// an equality/inequality comparison where BOTH sides fold to a fully-known literal string
    /// (via <see cref="TryFoldToKnownLiteralText"/>). Anything else - a comparison this evaluator
    /// doesn't model, or either side resolving to a Hole/Choice/Tainted value - returns null
    /// (undetermined), never a guess: the caller's own fallback (union every still-reachable
    /// branch) stays sound for every predicate shape this doesn't recognize.
    /// </summary>
    private static bool? TryEvaluatePredicate(BooleanExpression predicate, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        if (predicate is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.NotEqualToBrackets } comparison)
        {
            return null;
        }

        var left = TryFoldToKnownLiteralText(comparison.FirstExpression, state, sourcePath, cap);
        var right = TryFoldToKnownLiteralText(comparison.SecondExpression, state, sourcePath, cap);
        if (left is null || right is null)
        {
            return null;
        }

        var equal = string.Equals(left, right, StringComparison.Ordinal);
        return comparison.ComparisonType == BooleanComparisonType.Equals ? equal : !equal;
    }

    /// <summary>
    /// COALESCE's own definite value, for predicate-evaluation purposes, is its FIRST argument's
    /// own value whenever that argument is a KNOWN literal (a literal is provably non-NULL, so
    /// COALESCE never even reaches its later arguments) - matching <see cref="Fold"/>'s own
    /// identical COALESCE handling elsewhere. Any other first argument (a Hole/Choice/Tainted -
    /// genuinely unknown whether it's NULL at runtime) makes COALESCE's own result unknowable
    /// too, so this returns null rather than guessing which argument wins.
    /// </summary>
    private static string? TryFoldToKnownLiteralText(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        if (expression is CoalesceExpression { Expressions.Count: > 0 } coalesce)
        {
            return TryFoldToKnownLiteralText(coalesce.Expressions[0], state, sourcePath, cap);
        }

        var folded = Fold(expression, state, sourcePath, cap);
        return folded is SqlTextValue.Template template && template.Pieces.All(p => p is TemplatePiece.Lit)
            ? string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text))
            : null;
    }

    private static SqlTextValue FoldCastOrConvert(
        ScalarExpression source, DataTypeReference dataType, TSqlFragment site, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var targetType = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
        if (targetType is null)
        {
            return new SqlTextValue.Tainted("non-literal-expression:cast-target-not-pinned", Span(sourcePath, site));
        }

        var argument = ToBuiltinArgument(Fold(source, state, sourcePath, cap));
        var result = BuiltinRegistry.FoldCastOrConvert(targetType, argument, Span(sourcePath, site));
        return ToSqlTextValue(result, Span(sourcePath, site));
    }

    /// <summary>Bundles the loose values every step of a function-call fold needs to pass along together (Sonar S107's 7-parameter cap) - <paramref name="Site"/> is the call's own already-1-based <see cref="SourceSpan"/>, computed once in <see cref="FoldFunctionCall"/>.</summary>
    private sealed record FunctionCallFoldContext(
        string FunctionName, IList<ScalarExpression> Parameters, SourceSpan Site, Dictionary<string, SqlTextValue> State, string SourcePath, int Cap);

    private static SqlTextValue FoldFunctionCall(
        string functionName, IList<ScalarExpression> parameters, TSqlFragment site, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var foldContext = new FunctionCallFoldContext(functionName, parameters, Span(sourcePath, site), state, sourcePath, cap);

        // Every non-integer argument position is folded EXACTLY ONCE here, up front - reused both
        // for TryFoldCrossProduct's own choice-detection scan and, via ToBuiltinArgument, for the
        // ordinary non-choice path below. A naive "fold once to check for a Choice, fold again in
        // the fallback path" design re-runs Fold on the SAME child expression twice per call,
        // which for a chain of nested function calls (REPLACE(REPLACE(REPLACE(@x, ...), ...), ...) -
        // a real dynamic-SQL-building pattern) doubles the cost at EVERY nesting level: O(2^depth)
        // instead of O(depth). Caching here keeps every call site linear regardless of how deeply
        // its arguments themselves nest other function calls.
        var foldedArguments = new SqlTextValue?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!IntegerArgumentPositions.Contains((functionName.ToUpperInvariant(), i)))
            {
                foldedArguments[i] = Fold(parameters[i], state, sourcePath, cap);
            }
        }

        if (TryFoldCrossProduct(foldContext, foldedArguments, out var crossProduct))
        {
            return crossProduct;
        }

        var arguments = new List<BuiltinArgument>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            arguments.Add(foldedArguments[i] is { } folded
                ? ToBuiltinArgument(folded)
                : FoldArgument(functionName, i, parameters[i], state, sourcePath, cap));
        }

        var call = new BuiltinCall(functionName, arguments, foldContext.Site);
        return ToSqlTextValue(BuiltinRegistry.Fold(call), foldContext.Site);
    }

    /// <summary>
    /// When exactly one non-integer argument position resolves to a genuine multi-alternative
    /// <see cref="TemplatePiece.Choice"/> (a variable that diverged across IF branches BEFORE
    /// reaching this call - e.g. <c>REPLACE(@sql, ...)</c> where @sql itself is a Choice), the
    /// whole builtin call is folded once per alternative - substituting just that one argument
    /// each time - and the results re-joined under the SAME guard text as a fresh Choice,
    /// instead of <see cref="ToBuiltinArgument"/> collapsing the whole argument straight to the
    /// generic "symbolic-value-in-function-argument" the moment it sees more than one possible
    /// value. If ANY alternative's own fold taints, the WHOLE result taints with that specific
    /// reason - a Choice means "one of these really happens", so a defect on even one path is a
    /// real defect, not something a partial union may silently drop. More than one diverging
    /// argument at once is a real, deliberately-left-declining feature gap (composing two
    /// independently-guarded choices into a cross product risks reporting combinations that can
    /// never actually co-occur together) - returns false so the ordinary single-fold path runs
    /// and reports its own generic reason instead.
    /// </summary>
    private static bool TryFoldCrossProduct(FunctionCallFoldContext context, SqlTextValue?[] foldedArguments, out SqlTextValue result)
    {
        result = null!;
        var (choiceIndex, choice) = FindSoleChoiceArgument(foldedArguments);
        if (choiceIndex < 0)
        {
            return false;
        }

        result = FoldOverChoiceAlternatives(context, choiceIndex, choice!, foldedArguments);
        return true;
    }

    /// <summary>Extracted from <see cref="TryFoldCrossProduct"/> solely to keep that method's own Cognitive Complexity (Sonar S3776) under the two nested-loop bodies it previously carried. Returns (-1, null) when no argument diverges, or when MORE than one does (the deliberately-declined multi-choice case - see <see cref="TryFoldCrossProduct"/>'s own doc comment).</summary>
    private static (int Index, TemplatePiece.Choice? Choice) FindSoleChoiceArgument(SqlTextValue?[] foldedArguments)
    {
        var choiceIndex = -1;
        TemplatePiece.Choice? choice = null;

        for (var i = 0; i < foldedArguments.Length; i++)
        {
            if (foldedArguments[i] is not SqlTextValue.Template { Pieces: [TemplatePiece.Choice c] })
            {
                continue;
            }

            if (choiceIndex >= 0)
            {
                return (-1, null);
            }

            choiceIndex = i;
            choice = c;
        }

        return (choiceIndex, choice);
    }

    /// <summary>Extracted from <see cref="TryFoldCrossProduct"/> for the same Cognitive Complexity reason as <see cref="FindSoleChoiceArgument"/>. Folds the whole call once per alternative in <paramref name="choice"/>, substituting it at <paramref name="choiceIndex"/> each time (every OTHER argument reuses <paramref name="foldedArguments"/>'s already-computed value rather than re-folding), and re-unions the results - or returns the first Tainted alternative's own value outright, per the "one bad path taints the whole Choice" policy documented on <see cref="TryFoldCrossProduct"/>.</summary>
    private static SqlTextValue FoldOverChoiceAlternatives(FunctionCallFoldContext context, int choiceIndex, TemplatePiece.Choice choice, SqlTextValue?[] foldedArguments)
    {
        SqlTextValue? union = null;
        foreach (var alternative in choice.Alternatives)
        {
            var arguments = new List<BuiltinArgument>(context.Parameters.Count);
            for (var i = 0; i < context.Parameters.Count; i++)
            {
                arguments.Add(ResolveArgumentAt(context, i, choiceIndex, alternative, foldedArguments));
            }

            var foldedCall = ToSqlTextValue(BuiltinRegistry.Fold(new BuiltinCall(context.FunctionName, arguments, context.Site)), context.Site);
            if (foldedCall is SqlTextValue.Tainted)
            {
                return foldedCall;
            }

            union = union is null ? foldedCall : SqlTextValue.Join(union, foldedCall, choice.GuardText, context.Cap, context.Site);
        }

        return union!;
    }

    /// <summary>The per-position argument resolution <see cref="FoldOverChoiceAlternatives"/>'s own loop needs, extracted to turn its nested ternary (Sonar S3358) into a named, independently-readable statement: the diverging position gets this alternative's own value, every other position reuses its already-cached fold (or, for an integer position never cached up front, folds it now).</summary>
    private static BuiltinArgument ResolveArgumentAt(FunctionCallFoldContext context, int index, int choiceIndex, SqlTextValue.Template alternative, SqlTextValue?[] foldedArguments)
    {
        if (index == choiceIndex)
        {
            return ToBuiltinArgument(alternative);
        }

        return foldedArguments[index] is { } cachedArgument
            ? ToBuiltinArgument(cachedArgument)
            : FoldArgument(context.FunctionName, index, context.Parameters[index], context.State, context.SourcePath, context.Cap);
    }

    /// <summary>Every (function, zero-based parameter index) pair whose argument is INTEGER-typed rather than string/hole-typed - LEFT/RIGHT's length, SUBSTRING's start/length, STR's length/decimal, CHAR/NCHAR's code point.</summary>
    private static readonly HashSet<(string Function, int Index)> IntegerArgumentPositions =
    [
        (FnLeft, 1), (FnRight, 1),
        ("SUBSTRING", 1), ("SUBSTRING", 2),
        ("STR", 1), ("STR", 2),
        ("CHAR", 0), ("NCHAR", 0),
        ("REPLICATE", 1),
    ];

    /// <summary>
    /// An integer-typed argument position resolves via <see cref="FoldInteger"/>, never the
    /// general string-value <see cref="Fold"/> - this evaluator tracks only string variable
    /// values, never numeric ones, so a numeric variable reference always fails here. Every other
    /// position resolves as an ordinary string/hole argument.
    /// </summary>
    private static BuiltinArgument FoldArgument(string functionName, int index, ScalarExpression parameter, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        if (IntegerArgumentPositions.Contains((functionName.ToUpperInvariant(), index)))
        {
            return FoldInteger(parameter, state, sourcePath, cap, out var value)
                ? new BuiltinArgument.Number(value)
                : new BuiltinArgument.Unresolved("non-literal-expression:function-call-argument-diverges", Span(sourcePath, parameter));
        }

        return ToBuiltinArgument(Fold(parameter, state, sourcePath, cap));
    }

    /// <summary>
    /// A builtin argument's own fold is frequently a MULTI-piece all-literal Template - e.g.
    /// <c>QUOTENAME(@TableName + '_' + @Suffix)</c>, where the argument expression is itself a
    /// concatenation of several literal variables, each contributing its own <see cref="TemplatePiece.Lit"/>
    /// piece rather than one single piece. Flattening every-piece-is-Lit down to one
    /// <see cref="BuiltinArgument.Text"/> (regardless of piece count) is what makes this a KNOWN
    /// value the registry can actually evaluate, matching the old scanner's own
    /// <c>TryFlatten</c>. A single bare <see cref="TemplatePiece.Hole"/> is the ONLY shape that
    /// transfers as a typed-unknown; anything else unresolved (a <see cref="TemplatePiece.Choice"/>
    /// not yet expanded, or a MIX of literal and hole pieces - REPLACE's own hole-splice is the
    /// one place that shape is handled, and it never reaches this general path) declines rather
    /// than guessing.
    /// </summary>
    private static BuiltinArgument ToBuiltinArgument(SqlTextValue value) => value switch
    {
        SqlTextValue.Tainted tainted => new BuiltinArgument.Unresolved(tainted.Reason, tainted.Location),
        SqlTextValue.Template { Pieces: [TemplatePiece.Hole hole] } => new BuiltinArgument.Hole(hole.Type, hole.Kind),
        SqlTextValue.Template { Pieces.Count: > 0 } template when template.Pieces.All(p => p is TemplatePiece.Lit)
            => new BuiltinArgument.Text(string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text))),
        _ => new BuiltinArgument.Unresolved("symbolic-value-in-function-argument", default),
    };

    private static SqlTextValue ToSqlTextValue(BuiltinFoldResult result, SourceSpan site) => result switch
    {
        BuiltinFoldResult.Ok ok => new SqlTextValue.Template(ok.Pieces),
        BuiltinFoldResult.Fail fail => new SqlTextValue.Tainted(fail.Reason, site),
        _ => new SqlTextValue.Tainted("non-literal-expression:other", site),
    };

    /// <summary>
    /// Folds an integer-valued argument: a bare literal, +/- of two such foldable integers (a
    /// "strip the trailing delimiter" idiom, e.g. <c>LEN(@x) - LEN(@y)</c>), or <c>LEN(...)</c>
    /// over a string this evaluator already folds to a single concrete value. Anything else (a
    /// plain variable, an unsupported function, a column reference) declines rather than
    /// guessing - this evaluator tracks only string variable values, never numeric ones.
    /// </summary>
    public static bool FoldInteger(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out int value)
    {
        switch (expression)
        {
            case IntegerLiteral literal when int.TryParse(literal.Value, out value):
                return true;

            case ParenthesisExpression paren:
                return FoldInteger(paren.Expression, state, sourcePath, cap, out value);

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary
                when FoldInteger(unary.Expression, state, sourcePath, cap, out var innerValue):
                value = -innerValue;
                return true;

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                return FoldInteger(unary.Expression, state, sourcePath, cap, out value);

            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Subtract } binary
                when FoldInteger(binary.FirstExpression, state, sourcePath, cap, out var left)
                    && FoldInteger(binary.SecondExpression, state, sourcePath, cap, out var right):
                value = binary.BinaryExpressionType == BinaryExpressionType.Add ? left + right : left - right;
                return true;

            case FunctionCall { FunctionName.Value: var functionName } lenCall
                when string.Equals(functionName, "LEN", StringComparison.OrdinalIgnoreCase) && lenCall.Parameters.Count == 1:
                return TryFoldLenArgument(lenCall.Parameters[0], state, sourcePath, cap, out value);

            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Oracle-verified: LEN trims TRAILING spaces before counting (unlike DATALENGTH, not folded here) - <see cref="string.TrimEnd(char[])"/> over the space character matches exactly. The inner string may be a MULTI-piece all-literal Template (e.g. a concatenation) - see <see cref="ToBuiltinArgument"/>'s own doc comment for why flattening every-piece-is-Lit, not just a single piece, matters.</summary>
    private static bool TryFoldLenArgument(ScalarExpression argument, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out int value)
    {
        var folded = Fold(argument, state, sourcePath, cap);
        if (folded is not SqlTextValue.Template { Pieces.Count: > 0 } template || !template.Pieces.All(p => p is TemplatePiece.Lit))
        {
            // A placeholder's LEN is not a number - this evaluator does not know the real value,
            // so it cannot know its length either.
            value = 0;
            return false;
        }

        value = string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text)).TrimEnd(' ').Length;
        return true;
    }

    private static SourceSpan Span(string sourcePath, TSqlFragment fragment) => new(sourcePath, fragment.StartLine, fragment.StartColumn);
}
