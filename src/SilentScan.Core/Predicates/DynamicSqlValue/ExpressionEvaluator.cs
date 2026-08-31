using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public static class ExpressionEvaluator
{
    private const string FnLeft = "LEFT";
    private const string FnRight = "RIGHT";
    private const string FnIsNull = "ISNULL";
    private const string FnSubstring = "SUBSTRING";
    private const string NonLiteralOther = "non-literal-expression:other";

    private static readonly Dictionary<string, SqlType> GlobalVariableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@@TRANCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@ROWCOUNT"] = new SqlType(SqlTypeCategory.Int),
        ["@@ERROR"] = new SqlType(SqlTypeCategory.Int),
        ["@@IDENTITY"] = new SqlType(SqlTypeCategory.Decimal, Precision: 38, Scale: 0),
        ["@@NESTLEVEL"] = new SqlType(SqlTypeCategory.Int),
        ["@@SPID"] = new SqlType(SqlTypeCategory.SmallInt),
        ["@@FETCH_STATUS"] = new SqlType(SqlTypeCategory.Int),
    };

    private static readonly ConditionalWeakTable<ScalarExpression, object> ConditionalGuardIds = new();

    private static int ConditionalGuardId(ScalarExpression expression) =>
        (int)ConditionalGuardIds.GetValue(expression, static _ => SqlTextValue.NewGuardId());

    public static SqlTextValue Fold(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog = null)
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
                return Fold(paren.Expression, state, sourcePath, cap, catalog);

            case GlobalVariableExpression { Name: { } globalName } when GlobalVariableTypes.TryGetValue(globalName, out var globalType):

                return new SqlTextValue.Template([new TemplatePiece.Hole(globalType, Span(sourcePath, expression), HoleKind.EnvironmentDependent)]);

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                return Fold(unary.Expression, state, sourcePath, cap, catalog);

            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary:
                return FoldConcatenation(binary, state, sourcePath, cap, catalog);

            case BinaryExpression:
                return new SqlTextValue.Tainted("non-literal-expression:unsupported-operator", Span(sourcePath, expression));

            case FunctionCall { FunctionName.Value: var functionName } isNullCall
                when string.Equals(functionName, FnIsNull, StringComparison.OrdinalIgnoreCase) && isNullCall.Parameters.Count == 2:

                return Fold(isNullCall.Parameters[0], state, sourcePath, cap, catalog);

            case CoalesceExpression { Expressions.Count: > 0 } coalesce:
                return Fold(coalesce.Expressions[0], state, sourcePath, cap, catalog);

            case FunctionCall
            {
                FunctionName.Value: var substringName,
                Parameters: [VariableReference sourceRef, var startExpr, FunctionCall { FunctionName.Value: var lenName, Parameters: [VariableReference lenArgRef] }],
            }
                when string.Equals(substringName, FnSubstring, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(lenName, "LEN", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sourceRef.Name, lenArgRef.Name, StringComparison.OrdinalIgnoreCase)
                    && FoldInteger(startExpr, state, sourcePath, cap, out var substringStart)
                    && substringStart >= 1
                    && TryTrimThroughAlternatives(Fold(sourceRef, state, sourcePath, cap, catalog), substringStart - 1, TryTrimLeadingCharacters) is { } trimmedFromStart:

                return trimmedFromStart;

            case FunctionCall
            {
                FunctionName.Value: var zeroStartSubstringName,
                Parameters: [VariableReference zeroStartSourceRef, var zeroStartExpr, FunctionCall { FunctionName.Value: var zeroStartLenName, Parameters: [VariableReference zeroStartLenArgRef] }],
            }
                when string.Equals(zeroStartSubstringName, FnSubstring, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(zeroStartLenName, "LEN", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(zeroStartSourceRef.Name, zeroStartLenArgRef.Name, StringComparison.OrdinalIgnoreCase)
                    && FoldInteger(zeroStartExpr, state, sourcePath, cap, out var zeroStart)
                    && zeroStart == 0
                    && TryTrimThroughAlternatives(Fold(zeroStartSourceRef, state, sourcePath, cap, catalog), 1, TryTrimTrailingCharacters) is { } trimmedForZeroStart:

                return trimmedForZeroStart;

            case FunctionCall
            {
                FunctionName.Value: var trailingSubstringName,
                Parameters: [VariableReference trailingSourceRef, var trailingStartExpr, BinaryExpression
                {
                    BinaryExpressionType: BinaryExpressionType.Subtract,
                    FirstExpression: FunctionCall { FunctionName.Value: var trailingLenName, Parameters: [VariableReference trailingLenArgRef] },
                    SecondExpression: var trimCountExpr,
                }],
            }
                when string.Equals(trailingSubstringName, FnSubstring, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trailingLenName, "LEN", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(trailingSourceRef.Name, trailingLenArgRef.Name, StringComparison.OrdinalIgnoreCase)
                    && FoldInteger(trailingStartExpr, state, sourcePath, cap, out var trailingStart)
                    && trailingStart == 1
                    && FoldInteger(trimCountExpr, state, sourcePath, cap, out var trimCount)
                    && trimCount >= 0
                    && TryTrimThroughAlternatives(Fold(trailingSourceRef, state, sourcePath, cap, catalog), trimCount, TryTrimTrailingCharacters) is { } trimmedFromEnd:

                return trimmedFromEnd;

            case FunctionCall functionCall:
                return FoldFunctionCall(functionCall.FunctionName.Value, functionCall.Parameters, functionCall, state, sourcePath, cap, catalog);

            case LeftFunctionCall leftCall:
                return FoldFunctionCall(FnLeft, leftCall.Parameters, leftCall, state, sourcePath, cap, catalog);

            case RightFunctionCall rightCall:
                return FoldFunctionCall(FnRight, rightCall.Parameters, rightCall, state, sourcePath, cap, catalog);

            case CastCall castCall:
                return FoldCastOrConvert(castCall.Parameter, castCall.DataType, castCall, state, sourcePath, cap, catalog);

            case ConvertCall convertCall:
                return FoldCastOrConvert(convertCall.Parameter, convertCall.DataType, convertCall, state, sourcePath, cap, catalog);

            case SimpleCaseExpression or SearchedCaseExpression or IIfCall:
                return FoldConditional(expression, state, sourcePath, cap, catalog);

            case ColumnReferenceExpression:
                return new SqlTextValue.Tainted("non-literal-expression:column-reference", Span(sourcePath, expression));

            case ScalarSubquery { QueryExpression: QuerySpecification { FromClause: not null } }:

                return new SqlTextValue.Tainted("non-literal-expression:sql-loaded-from-table", Span(sourcePath, expression));

            case ScalarSubquery
            {
                QueryExpression: QuerySpecification
                {
                    FromClause: null,
                    SelectElements: [SelectScalarExpression { Expression: { } wrappedExpression }],
                },
            }:

                return Fold(wrappedExpression, state, sourcePath, cap, catalog);

            case ScalarSubquery:
                return new SqlTextValue.Tainted("non-literal-expression:subquery", Span(sourcePath, expression));

            default:
                return new SqlTextValue.Tainted(NonLiteralOther, Span(sourcePath, expression));
        }
    }

    private static SqlTextValue FoldConcatenation(BinaryExpression binary, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        var left = Fold(binary.FirstExpression, state, sourcePath, cap, catalog);
        var right = Fold(binary.SecondExpression, state, sourcePath, cap, catalog);
        return SqlTextValue.Concat(left, right);
    }

    private static SqlTextValue FoldConditional(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {

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

        if (elseExpression is null)
        {
            return new SqlTextValue.Tainted("non-literal-expression:conditional", Span(sourcePath, expression));
        }

        var at = Span(sourcePath, expression);
        var remainingBranches = thenExpressions;
        if (whenClauses is not null)
        {
            remainingBranches = ResolveDeterminableBranches(whenClauses, state, sourcePath, cap, catalog, out var decided);
            if (decided is { } decidedBranch)
            {
                return Fold(decidedBranch, state, sourcePath, cap, catalog);
            }
        }

        SqlTextValue? union = null;
        var guardId = ConditionalGuardId(expression);
        foreach (var branch in remainingBranches.Append(elseExpression))
        {
            var folded = Fold(branch, state, sourcePath, cap, catalog);
            union = union is null ? folded : SqlTextValue.Join(union, folded, guardId, cap, at);
        }

        return union!;
    }

    private static List<ScalarExpression> ResolveDeterminableBranches(
        IReadOnlyList<(BooleanExpression Condition, ScalarExpression Then)> whenClauses, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog, out ScalarExpression? decided)
    {
        decided = null;
        var remaining = new List<ScalarExpression>();
        foreach (var (condition, then) in whenClauses)
        {
            var determined = TryEvaluatePredicate(condition, state, sourcePath, cap, catalog);
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

    private static bool? TryEvaluatePredicate(BooleanExpression predicate, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        if (predicate is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.NotEqualToBrackets } comparison)
        {
            return null;
        }

        var left = TryFoldToKnownLiteralText(comparison.FirstExpression, state, sourcePath, cap, catalog);
        var right = TryFoldToKnownLiteralText(comparison.SecondExpression, state, sourcePath, cap, catalog);
        if (left is null || right is null)
        {
            return null;
        }

        var equal = string.Equals(left, right, StringComparison.Ordinal);
        return comparison.ComparisonType == BooleanComparisonType.Equals ? equal : !equal;
    }

    private static string? TryFoldToKnownLiteralText(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        if (expression is CoalesceExpression { Expressions.Count: > 0 } coalesce)
        {
            return TryFoldToKnownLiteralText(coalesce.Expressions[0], state, sourcePath, cap, catalog);
        }

        var folded = Fold(expression, state, sourcePath, cap, catalog);
        return folded is SqlTextValue.Template template && template.Pieces.All(p => p is TemplatePiece.Lit)
            ? string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text))
            : null;
    }

    private static SqlTextValue FoldCastOrConvert(
        ScalarExpression source, DataTypeReference dataType, TSqlFragment site, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        var targetType = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
        if (targetType is null)
        {
            return new SqlTextValue.Tainted("non-literal-expression:cast-target-not-pinned", Span(sourcePath, site));
        }

        var argument = ToBuiltinArgument(Fold(source, state, sourcePath, cap, catalog));
        var result = BuiltinRegistry.FoldCastOrConvert(targetType, argument, Span(sourcePath, site));
        return ToSqlTextValue(result, Span(sourcePath, site));
    }

    private sealed record FunctionCallFoldContext(
        string FunctionName, IList<ScalarExpression> Parameters, SourceSpan Site, Dictionary<string, SqlTextValue> State, string SourcePath, int Cap, DatabaseCatalog? Catalog);

    private static SqlTextValue FoldFunctionCall(
        string functionName, IList<ScalarExpression> parameters, TSqlFragment site, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        if (!BuiltinRegistry.IsKnownBuiltin(functionName)
            && site is FunctionCall userFunctionCall
            && TryFoldUserScalarFunction(userFunctionCall, catalog, sourcePath, out var userFunctionResult))
        {
            return userFunctionResult;
        }

        var foldContext = new FunctionCallFoldContext(functionName, parameters, Span(sourcePath, site), state, sourcePath, cap, catalog);

        var foldedArguments = new SqlTextValue?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!IntegerArgumentPositions.Contains((functionName.ToUpperInvariant(), i)))
            {
                foldedArguments[i] = Fold(parameters[i], state, sourcePath, cap, catalog);
            }
        }

        if (TryFoldReplaceWithMixedSource(foldContext, foldedArguments, out var mixedSourceResult))
        {
            return mixedSourceResult;
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
                : FoldArgument(functionName, i, parameters[i], state, sourcePath, cap, catalog));
        }

        var call = new BuiltinCall(functionName, arguments, foldContext.Site);
        return ToSqlTextValue(BuiltinRegistry.Fold(call), foldContext.Site);
    }

    private static bool TryFoldUserScalarFunction(FunctionCall site, DatabaseCatalog? catalog, string sourcePath, out SqlTextValue result)
    {
        result = null!;
        if (catalog is not { } knownCatalog)
        {
            return false;
        }

        var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(site);
        if (!knownCatalog.TryGetScalarFunctionReturnType(qualifiedName, out var returnType) || returnType is not { } known)
        {
            return false;
        }

        result = new SqlTextValue.Template([new TemplatePiece.Hole(known, Span(sourcePath, site), HoleKind.UserFunctionDeclaredReturnType)]);
        return true;
    }

    private static bool TryFoldReplaceWithMixedSource(FunctionCallFoldContext context, SqlTextValue?[] foldedArguments, out SqlTextValue result)
    {
        result = null!;
        if (!string.Equals(context.FunctionName, "REPLACE", StringComparison.OrdinalIgnoreCase) || context.Parameters.Count != 3)
        {
            return false;
        }

        if (foldedArguments[0] is not SqlTextValue.Template { Pieces.Count: > 1 } sourceTemplate
            || !sourceTemplate.Pieces.All(p => p is TemplatePiece.Lit or TemplatePiece.Hole or TemplatePiece.Choice)
            || !sourceTemplate.Pieces.Any(p => p is TemplatePiece.Hole or TemplatePiece.Choice))
        {
            return false;
        }

        var patternArgument = ToBuiltinArgument(foldedArguments[1] ?? new SqlTextValue.Tainted(NonLiteralOther, context.Site));
        var replacementArgument = ToBuiltinArgument(foldedArguments[2] ?? new SqlTextValue.Tainted(NonLiteralOther, context.Site));

        result = FoldReplaceOverPiecesPreservingChoices(sourceTemplate.Pieces, patternArgument, replacementArgument, context.Site);
        return true;
    }

    private static SqlTextValue FoldReplaceOverPiecesPreservingChoices(IReadOnlyList<TemplatePiece> pieces, BuiltinArgument patternArgument, BuiltinArgument replacementArgument, SourceSpan site)
    {
        var newPieces = new List<TemplatePiece>();
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case TemplatePiece.Lit lit:
                    var segmentCall = new BuiltinCall("REPLACE", [new BuiltinArgument.Text(lit.Text), patternArgument, replacementArgument], site);
                    var segmentResult = BuiltinRegistry.Fold(segmentCall);
                    if (segmentResult is BuiltinFoldResult.Fail fail)
                    {
                        return new SqlTextValue.Tainted(fail.Reason, site);
                    }

                    newPieces.AddRange(((BuiltinFoldResult.Ok)segmentResult).Pieces);
                    break;

                case TemplatePiece.Choice choice:
                    var transformedAlternatives = new List<SqlTextValue.Template>();
                    foreach (var alternative in choice.Alternatives)
                    {
                        var transformedAlternative = FoldReplaceOverPiecesPreservingChoices(alternative.Pieces, patternArgument, replacementArgument, site);
                        if (transformedAlternative is SqlTextValue.Tainted taintedAlternative)
                        {
                            return taintedAlternative;
                        }

                        transformedAlternatives.Add((SqlTextValue.Template)transformedAlternative);
                    }

                    newPieces.Add(new TemplatePiece.Choice(choice.GuardId, transformedAlternatives));
                    break;

                default:
                    newPieces.Add(piece);
                    break;
            }
        }

        return new SqlTextValue.Template(newPieces);
    }

    private sealed record EmbeddedChoice(int Index, IReadOnlyList<TemplatePiece> Prefix, TemplatePiece.Choice Choice, IReadOnlyList<TemplatePiece> Suffix);

    private static bool TryFoldCrossProduct(FunctionCallFoldContext context, SqlTextValue?[] foldedArguments, out SqlTextValue result)
    {
        result = null!;
        var embedded = FindSoleChoiceArgument(foldedArguments);
        if (embedded is null)
        {
            return false;
        }

        result = FoldOverChoiceAlternatives(context, embedded, foldedArguments);
        return true;
    }

    private static EmbeddedChoice? FindSoleChoiceArgument(SqlTextValue?[] foldedArguments)
    {
        EmbeddedChoice? found = null;
        for (var i = 0; i < foldedArguments.Length; i++)
        {
            if (foldedArguments[i] is not SqlTextValue.Template template || !TryExtractSoleEmbeddedChoice(template, i, out var embedded))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = embedded;
        }

        return found;
    }

    private static bool TryExtractSoleEmbeddedChoice(SqlTextValue.Template template, int index, out EmbeddedChoice embedded)
    {
        embedded = null!;
        var choiceAt = -1;
        for (var i = 0; i < template.Pieces.Count; i++)
        {
            switch (template.Pieces[i])
            {
                case TemplatePiece.Choice when choiceAt >= 0:
                    return false;
                case TemplatePiece.Choice:
                    choiceAt = i;
                    break;
                case TemplatePiece.Lit:
                    break;
                default:
                    return false;
            }
        }

        if (choiceAt < 0)
        {
            return false;
        }

        embedded = new EmbeddedChoice(index, template.Pieces.Take(choiceAt).ToList(), (TemplatePiece.Choice)template.Pieces[choiceAt], template.Pieces.Skip(choiceAt + 1).ToList());
        return true;
    }

    private static SqlTextValue FoldOverChoiceAlternatives(FunctionCallFoldContext context, EmbeddedChoice embedded, SqlTextValue?[] foldedArguments)
    {
        SqlTextValue? union = null;
        foreach (var alternative in embedded.Choice.Alternatives)
        {
            var spliced = embedded.Prefix.Count == 0 && embedded.Suffix.Count == 0
                ? alternative
                : new SqlTextValue.Template([.. embedded.Prefix, .. alternative.Pieces, .. embedded.Suffix]);

            var arguments = new List<BuiltinArgument>(context.Parameters.Count);
            for (var i = 0; i < context.Parameters.Count; i++)
            {
                arguments.Add(ResolveArgumentAt(context, i, embedded.Index, spliced, foldedArguments));
            }

            var foldedCall = ToSqlTextValue(BuiltinRegistry.Fold(new BuiltinCall(context.FunctionName, arguments, context.Site)), context.Site);
            if (foldedCall is SqlTextValue.Tainted)
            {
                return foldedCall;
            }

            union = union is null ? foldedCall : SqlTextValue.Join(union, foldedCall, embedded.Choice.GuardId, context.Cap, context.Site);
        }

        return union!;
    }

    private static BuiltinArgument ResolveArgumentAt(FunctionCallFoldContext context, int index, int choiceIndex, SqlTextValue.Template splicedAlternative, SqlTextValue?[] foldedArguments)
    {
        if (index == choiceIndex)
        {
            return ToBuiltinArgument(splicedAlternative);
        }

        return foldedArguments[index] is { } cachedArgument
            ? ToBuiltinArgument(cachedArgument)
            : FoldArgument(context.FunctionName, index, context.Parameters[index], context.State, context.SourcePath, context.Cap, context.Catalog);
    }

    private static readonly HashSet<(string Function, int Index)> IntegerArgumentPositions =
    [
        (FnLeft, 1), (FnRight, 1),
        (FnSubstring, 1), (FnSubstring, 2),
        ("STR", 1), ("STR", 2),
        ("CHAR", 0), ("NCHAR", 0),
        ("REPLICATE", 1),
    ];

    private static BuiltinArgument FoldArgument(string functionName, int index, ScalarExpression parameter, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, DatabaseCatalog? catalog)
    {
        if (IntegerArgumentPositions.Contains((functionName.ToUpperInvariant(), index)))
        {
            return FoldInteger(parameter, state, sourcePath, cap, out var value)
                ? new BuiltinArgument.Number(value)
                : new BuiltinArgument.Unresolved("non-literal-expression:function-call-argument-diverges", Span(sourcePath, parameter));
        }

        return ToBuiltinArgument(Fold(parameter, state, sourcePath, cap, catalog));
    }

    private static BuiltinArgument ToBuiltinArgument(SqlTextValue value) => value switch
    {
        SqlTextValue.Tainted tainted => new BuiltinArgument.Unresolved(tainted.Reason, tainted.Location, tainted.DeclaredType),
        SqlTextValue.Template { Pieces: [TemplatePiece.Hole hole] } => new BuiltinArgument.Hole(hole.Type, hole.Kind),
        SqlTextValue.Template { Pieces.Count: > 0 } template when template.Pieces.All(p => p is TemplatePiece.Lit)
            => new BuiltinArgument.Text(string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text))),
        _ => new BuiltinArgument.Unresolved("symbolic-value-in-function-argument", default, value.DeclaredType),
    };

    private static SqlTextValue ToSqlTextValue(BuiltinFoldResult result, SourceSpan site) => result switch
    {
        BuiltinFoldResult.Ok ok => new SqlTextValue.Template(ok.Pieces),
        BuiltinFoldResult.Fail fail => new SqlTextValue.Tainted(fail.Reason, site),
        _ => new SqlTextValue.Tainted(NonLiteralOther, site),
    };

    public static bool FoldInteger(ScalarExpression expression, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out int value)
    {
        switch (expression)
        {
            case IntegerLiteral literal when int.TryParse(literal.Value, out value):
                return true;

            case VariableReference variableRef when state.TryGetValue(variableRef.Name, out var variableValue) && TryLiteralAsInteger(variableValue, out value):
                return true;

            case ParenthesisExpression paren:
                return FoldInteger(paren.Expression, state, sourcePath, cap, out value);

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary
                when FoldInteger(unary.Expression, state, sourcePath, cap, out var innerValue):
                value = -innerValue;
                return true;

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                return FoldInteger(unary.Expression, state, sourcePath, cap, out value);

            case BinaryExpression
            {
                BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Subtract
                    or BinaryExpressionType.BitwiseAnd or BinaryExpressionType.BitwiseOr or BinaryExpressionType.BitwiseXor,
            } binary
                when FoldInteger(binary.FirstExpression, state, sourcePath, cap, out var left)
                    && FoldInteger(binary.SecondExpression, state, sourcePath, cap, out var right):
                value = binary.BinaryExpressionType switch
                {
                    BinaryExpressionType.Add => left + right,
                    BinaryExpressionType.Subtract => left - right,
                    BinaryExpressionType.BitwiseAnd => left & right,
                    BinaryExpressionType.BitwiseOr => left | right,
                    _ => left ^ right,
                };
                return true;

            case FunctionCall { FunctionName.Value: var functionName } lenCall
                when string.Equals(functionName, "LEN", StringComparison.OrdinalIgnoreCase) && lenCall.Parameters.Count == 1:
                return TryFoldLenArgument(lenCall.Parameters[0], state, sourcePath, cap, out value);

            default:
                value = 0;
                return false;
        }
    }

    internal static bool TryLiteralAsInteger(SqlTextValue value, out int result)
    {
        if (value is SqlTextValue.Template { Pieces: [TemplatePiece.Lit lit] } && int.TryParse(lit.Text, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryFoldLenArgument(ScalarExpression argument, Dictionary<string, SqlTextValue> state, string sourcePath, int cap, out int value)
    {
        var folded = Fold(argument, state, sourcePath, cap);
        if (folded is not SqlTextValue.Template { Pieces.Count: > 0 } template || !template.Pieces.All(p => p is TemplatePiece.Lit))
        {

            value = 0;
            return false;
        }

        value = string.Concat(template.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text)).TrimEnd(' ').Length;
        return true;
    }

    private static SqlTextValue? TryTrimLeadingCharacters(SqlTextValue value, int count)
    {
        if (count == 0)
        {
            return value;
        }

        if (value is not SqlTextValue.Template template)
        {
            return null;
        }

        return TrimLeadingFromPieces(template.Pieces, count) is { } trimmedPieces
            ? new SqlTextValue.Template(trimmedPieces)
            : null;
    }

    private static List<TemplatePiece>? TrimLeadingFromPieces(IReadOnlyList<TemplatePiece> pieces, int count)
    {
        var remaining = count;
        var index = 0;
        while (index < pieces.Count && pieces[index] is TemplatePiece.Lit lit)
        {
            if (lit.Text.Length >= remaining)
            {
                var trimmedText = lit.Text[remaining..];
                var tail = pieces.Skip(index + 1);
                return (trimmedText.Length == 0 ? tail : tail.Prepend(new TemplatePiece.Lit(trimmedText, lit.Origin, PrefixLength: 0))).ToList();
            }

            remaining -= lit.Text.Length;
            index++;
        }

        if (index < pieces.Count && pieces[index] is TemplatePiece.Choice choice)
        {
            var trimmedAlternatives = choice.Alternatives
                .Select(alternative => TrimLeadingFromPieces(alternative.Pieces, remaining))
                .Where(trimmedAlternativePieces => trimmedAlternativePieces is not null)
                .Select(trimmedAlternativePieces => new SqlTextValue.Template(trimmedAlternativePieces!))
                .ToList();

            if (trimmedAlternatives.Count == 0)
            {
                return null;
            }

            return pieces.Skip(index + 1).Prepend(new TemplatePiece.Choice(choice.GuardId, trimmedAlternatives)).ToList();
        }

        return null;
    }

    private static SqlTextValue? TryTrimTrailingCharacters(SqlTextValue value, int count)
    {
        if (count == 0)
        {
            return value;
        }

        if (value is not SqlTextValue.Template template)
        {
            return null;
        }

        return TrimTrailingFromPieces(template.Pieces, count) is { } trimmedPieces
            ? new SqlTextValue.Template(trimmedPieces)
            : null;
    }

    private static List<TemplatePiece>? TrimTrailingFromPieces(IReadOnlyList<TemplatePiece> pieces, int count)
    {
        var remaining = count;
        var index = pieces.Count - 1;
        while (index >= 0 && pieces[index] is TemplatePiece.Lit lit)
        {
            if (lit.Text.Length >= remaining)
            {
                var trimmedText = lit.Text[..^remaining];
                var head = pieces.Take(index);
                return (trimmedText.Length == 0 ? head : head.Append(new TemplatePiece.Lit(trimmedText, lit.Origin, lit.PrefixLength))).ToList();
            }

            remaining -= lit.Text.Length;
            index--;
        }

        if (index >= 0 && pieces[index] is TemplatePiece.Choice choice)
        {
            var trimmedAlternatives = choice.Alternatives
                .Select(alternative => TrimTrailingFromPieces(alternative.Pieces, remaining))
                .Where(trimmedAlternativePieces => trimmedAlternativePieces is not null)
                .Select(trimmedAlternativePieces => new SqlTextValue.Template(trimmedAlternativePieces!))
                .ToList();

            if (trimmedAlternatives.Count == 0)
            {
                return null;
            }

            return pieces.Take(index).Append(new TemplatePiece.Choice(choice.GuardId, trimmedAlternatives)).ToList();
        }

        return null;
    }

    private static SqlTextValue? TryTrimThroughAlternatives(SqlTextValue value, int count, Func<SqlTextValue, int, SqlTextValue?> trim)
    {
        if (trim(value, count) is { } direct)
        {
            return direct;
        }

        if (value is not SqlTextValue.Tainted { GuardedAlternatives: { Count: > 0 } alternatives } tainted)
        {
            return null;
        }

        var trimmedAlternatives = alternatives
            .Select(alt => trim(alt.Value, count) is SqlTextValue.Template trimmed ? alt with { Value = trimmed } : null)
            .Where(alt => alt is not null)
            .Select(alt => alt!)
            .ToList();

        return trimmedAlternatives.Count > 0 ? tainted with { GuardedAlternatives = trimmedAlternatives } : null;
    }

    private static SourceSpan Span(string sourcePath, TSqlFragment fragment) => new(sourcePath, fragment.StartLine, fragment.StartColumn);
}
