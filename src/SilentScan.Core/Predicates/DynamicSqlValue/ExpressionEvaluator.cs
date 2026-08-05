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

    private static SqlTextValue FoldConcatenation(BinaryExpression binary, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var left = Fold(binary.FirstExpression, state, sourcePath, cap);
        if (left is SqlTextValue.Tainted)
        {
            return left;
        }

        var right = Fold(binary.SecondExpression, state, sourcePath, cap);
        return SqlTextValue.Concat(left, right);
    }

    private static SqlTextValue FoldConditional(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var (branches, elseExpression) = expression switch
        {
            SimpleCaseExpression simpleCase => (simpleCase.WhenClauses.Select(w => w.ThenExpression), simpleCase.ElseExpression),
            SearchedCaseExpression searchedCase => (searchedCase.WhenClauses.Select(w => w.ThenExpression), searchedCase.ElseExpression),
            IIfCall iif => (new[] { iif.ThenExpression }.AsEnumerable(), iif.ElseExpression),
            _ => (Enumerable.Empty<ScalarExpression>(), null),
        };

        // A bare CASE with no matching WHEN and no ELSE returns SQL NULL, which this domain has
        // no representation for - silently omitting that outcome from the union would be
        // unsound, not merely imprecise, so this declines instead of guessing.
        if (elseExpression is null)
        {
            return new SqlTextValue.Tainted("non-literal-expression:conditional", Span(sourcePath, expression));
        }

        var at = Span(sourcePath, expression);
        SqlTextValue? union = null;
        foreach (var branch in branches.Append(elseExpression))
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

    private static SqlTextValue FoldFunctionCall(
        string functionName, IList<ScalarExpression> parameters, TSqlFragment site, Dictionary<string, SqlTextValue> state, string sourcePath, int cap)
    {
        var site1Based = Span(sourcePath, site);
        var arguments = new List<BuiltinArgument>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++)
        {
            arguments.Add(FoldArgument(functionName, i, parameters[i], state, sourcePath, cap));
        }

        var call = new BuiltinCall(functionName, arguments, site1Based);
        return ToSqlTextValue(BuiltinRegistry.Fold(call), site1Based);
    }

    /// <summary>Every (function, zero-based parameter index) pair whose argument is INTEGER-typed rather than string/hole-typed - LEFT/RIGHT's length, SUBSTRING's start/length, STR's length/decimal, CHAR/NCHAR's code point.</summary>
    private static readonly HashSet<(string Function, int Index)> IntegerArgumentPositions =
    [
        (FnLeft, 1), (FnRight, 1),
        ("SUBSTRING", 1), ("SUBSTRING", 2),
        ("STR", 1), ("STR", 2),
        ("CHAR", 0), ("NCHAR", 0),
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

    private static BuiltinArgument ToBuiltinArgument(SqlTextValue value) => value switch
    {
        SqlTextValue.Tainted tainted => new BuiltinArgument.Unresolved(tainted.Reason, tainted.Location),
        SqlTextValue.Template { Pieces: [TemplatePiece.Lit lit] } => new BuiltinArgument.Text(lit.Text),
        SqlTextValue.Template { Pieces: [TemplatePiece.Hole hole] } => new BuiltinArgument.Hole(hole.Type, hole.Kind),
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

    /// <summary>Oracle-verified: LEN trims TRAILING spaces before counting (unlike DATALENGTH, not folded here) - <see cref="string.TrimEnd(char[])"/> over the space character matches exactly.</summary>
    private static bool TryFoldLenArgument(ScalarExpression argument, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out int value)
    {
        var folded = Fold(argument, state, sourcePath, cap);
        if (folded is not SqlTextValue.Template { Pieces: [TemplatePiece.Lit lit] })
        {
            // A placeholder's LEN is not a number - this evaluator does not know the real value,
            // so it cannot know its length either.
            value = 0;
            return false;
        }

        value = lit.Text.TrimEnd(' ').Length;
        return true;
    }

    private static SourceSpan Span(string sourcePath, TSqlFragment fragment) => new(sourcePath, fragment.StartLine, fragment.StartColumn);
}
