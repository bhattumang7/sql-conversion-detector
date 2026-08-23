using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates.Normalization;

public static class PredicateSurvivalAnalyzer
{
    public readonly record struct ColumnFacts(bool? IsNotNull, bool? IsCaseSensitiveCollation);

    public static IReadOnlySet<TSqlFragment> FindDeadComparisons(
        BooleanExpression? searchCondition, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        var dead = new HashSet<TSqlFragment>();
        if (searchCondition is not null)
        {
            MarkDead(searchCondition, resolveColumnFacts, dead);
        }

        return dead;
    }

    public static bool IsUnsatisfiable(BooleanExpression? searchCondition, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts) =>
        searchCondition is not null && Classify(searchCondition, resolveColumnFacts).NeverTrue;

    private readonly record struct ColumnKey(string Qualifier, string Name);

    private enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

    private static (bool NeverTrue, bool AlwaysTrue, bool AlwaysFalse) Classify(
        BooleanExpression node, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        switch (node)
        {
            case BooleanParenthesisExpression paren:
                return Classify(paren.Expression, resolveColumnFacts);

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And }:
            {
                var conjuncts = Flatten(node, BooleanBinaryExpressionType.And);
                var verdicts = conjuncts.Select(c => Classify(c, resolveColumnFacts)).ToList();

                var (contradiction, contradictionColumnConfirmedNotNull) = DetectContradiction(conjuncts, resolveColumnFacts);
                var neverTrue = verdicts.Any(v => v.NeverTrue) || contradiction;

                if (neverTrue)
                {

                    var alwaysFalse = verdicts.Any(v => v.AlwaysFalse) || (contradiction && contradictionColumnConfirmedNotNull);
                    return (true, false, alwaysFalse);
                }

                return (false, verdicts.All(v => v.AlwaysTrue), false);
            }

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }:
            {
                var disjuncts = Flatten(node, BooleanBinaryExpressionType.Or);
                var verdicts = disjuncts.Select(d => Classify(d, resolveColumnFacts)).ToList();

                var alwaysTrue = verdicts.Any(v => v.AlwaysTrue) || DetectTautology(disjuncts, resolveColumnFacts);
                if (alwaysTrue)
                {
                    return (false, true, false);
                }

                return (verdicts.All(v => v.NeverTrue), false, false);
            }

            case BooleanNotExpression not:
            {
                var (_, yAlwaysTrue, yAlwaysFalse) = Classify(not.Expression, resolveColumnFacts);

                return (yAlwaysTrue, yAlwaysFalse, yAlwaysTrue);
            }

            case BooleanTernaryExpression { TernaryExpressionType: BooleanTernaryExpressionType.Between } between:
                return ClassifyBetween(between, resolveColumnFacts);

            case BooleanComparisonExpression cmp:
                return ClassifyConstantComparison(cmp);

            default:
                return (false, false, false);
        }
    }

    private static void MarkAbsorbedDisjunctions(IReadOnlyList<BooleanExpression> conjuncts, HashSet<TSqlFragment> dead)
    {
        foreach (var disjunction in conjuncts.Where(c => c is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }))
        {
            var alternatives = Flatten(disjunction, BooleanBinaryExpressionType.Or);
            if (alternatives.Any(alternative => conjuncts.Any(conjunct => !ReferenceEquals(conjunct, disjunction) && SamePredicate(conjunct, alternative))))
            {
                MarkAllLeavesDead(disjunction, dead);
            }
        }
    }

    private static bool SamePredicate(BooleanExpression left, BooleanExpression right)
    {
        if (left is BooleanComparisonExpression a && right is BooleanComparisonExpression b
            && a.ComparisonType == b.ComparisonType)
        {
            return SameScalar(a.FirstExpression, b.FirstExpression) && SameScalar(a.SecondExpression, b.SecondExpression)
                || a.ComparisonType == BooleanComparisonType.Equals
                && SameScalar(a.FirstExpression, b.SecondExpression) && SameScalar(a.SecondExpression, b.FirstExpression);
        }

        return left is BooleanIsNullExpression leftNull && right is BooleanIsNullExpression rightNull
            && leftNull.IsNot == rightNull.IsNot
            && SameScalar(leftNull.Expression, rightNull.Expression);
    }

    private static bool SameScalar(ScalarExpression left, ScalarExpression right) => (left, right) switch
    {
        (ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { } leftIds }, ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { } rightIds }) =>
            leftIds.Count == rightIds.Count && leftIds.Zip(rightIds).All(pair => string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal)),
        (VariableReference leftVariable, VariableReference rightVariable) => string.Equals(leftVariable.Name, rightVariable.Name, StringComparison.Ordinal),
        (IntegerLiteral leftLiteral, IntegerLiteral rightLiteral) => string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.Ordinal),
        (NumericLiteral leftLiteral, NumericLiteral rightLiteral) => string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.Ordinal),
        (MoneyLiteral leftLiteral, MoneyLiteral rightLiteral) => string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.Ordinal),
        _ => false,
    };

    private static void MarkDead(BooleanExpression node, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts, HashSet<TSqlFragment> dead)
    {
        var (neverTrue, alwaysTrue, _) = Classify(node, resolveColumnFacts);
        if (neverTrue || alwaysTrue)
        {
            MarkAllLeavesDead(node, dead);
            return;
        }

        switch (node)
        {
            case BooleanParenthesisExpression paren:
                MarkDead(paren.Expression, resolveColumnFacts, dead);
                break;

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And }:
                var conjuncts = Flatten(node, BooleanBinaryExpressionType.And);
                MarkAbsorbedDisjunctions(conjuncts, dead);
                foreach (var c in conjuncts)
                {
                    MarkDead(c, resolveColumnFacts, dead);
                }

                break;

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }:
                foreach (var d in Flatten(node, BooleanBinaryExpressionType.Or))
                {
                    MarkDead(d, resolveColumnFacts, dead);
                }

                break;

            case BooleanNotExpression:

                break;

            default:
                break;
        }
    }

    private static List<BooleanExpression> Flatten(BooleanExpression node, BooleanBinaryExpressionType type)
    {
        var result = new List<BooleanExpression>();
        void Walk(BooleanExpression n)
        {
            while (n is BooleanParenthesisExpression paren)
            {
                n = paren.Expression;
            }

            if (n is BooleanBinaryExpression { } bin && bin.BinaryExpressionType == type)
            {
                Walk(bin.FirstExpression);
                Walk(bin.SecondExpression);
            }
            else
            {
                result.Add(n);
            }
        }

        Walk(node);
        return result;
    }

    private static void MarkAllLeavesDead(BooleanExpression node, HashSet<TSqlFragment> dead)
    {
        switch (node)
        {
            case BooleanParenthesisExpression paren:
                MarkAllLeavesDead(paren.Expression, dead);
                break;
            case BooleanBinaryExpression bin:
                MarkAllLeavesDead(bin.FirstExpression, dead);
                MarkAllLeavesDead(bin.SecondExpression, dead);
                break;
            case BooleanNotExpression not:
                MarkAllLeavesDead(not.Expression, dead);
                break;
            default:
                dead.Add(node);
                break;
        }
    }

    private static (bool, bool, bool) ClassifyBetween(
        BooleanTernaryExpression between, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        if (TryGetNumericLiteral(between.SecondExpression) is { } lower
            && TryGetNumericLiteral(between.ThirdExpression) is { } upper
            && lower > upper)
        {

            var alwaysFalse = between.FirstExpression is ColumnReferenceExpression colRef
                && resolveColumnFacts(colRef).IsNotNull == true;
            return (true, false, alwaysFalse);
        }

        return (false, false, false);
    }

    private static (bool, bool, bool) ClassifyConstantComparison(BooleanComparisonExpression cmp)
    {
        var op = ToCmpOp(cmp.ComparisonType);
        if (op is null)
        {
            return (false, false, false);
        }

        var result = EvaluateConstantComparison(cmp, op.Value);

        if (result == false)
        {
            return (true, false, true);
        }

        return (false, result == true, false);
    }

    private static bool? EvaluateConstantComparison(BooleanComparisonExpression comparison, CmpOp op)
    {
        if (TryGetNumericLiteral(comparison.FirstExpression) is { } leftNumber
            && TryGetNumericLiteral(comparison.SecondExpression) is { } rightNumber)
        {
            return EvaluateNumeric(op, leftNumber, rightNumber);
        }

        if (TryGetStringLiteral(comparison.FirstExpression) is { } leftString
            && TryGetStringLiteral(comparison.SecondExpression) is { } rightString)
        {
            return EvaluateString(op, leftString, rightString);
        }

        return null;
    }

    private static bool EvaluateNumeric(CmpOp op, decimal a, decimal b) => op switch
    {
        CmpOp.Eq => a == b,
        CmpOp.Ne => a != b,
        CmpOp.Lt => a < b,
        CmpOp.Le => a <= b,
        CmpOp.Gt => a > b,
        CmpOp.Ge => a >= b,
        _ => false,
    };

    private static bool? EvaluateString(CmpOp op, string a, string b) => op switch
    {
        CmpOp.Eq => string.Equals(a, b, StringComparison.Ordinal),
        CmpOp.Ne => !string.Equals(a, b, StringComparison.Ordinal),
        _ => null,
    };

    private static CmpOp? ToCmpOp(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => CmpOp.Eq,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => CmpOp.Ne,
        BooleanComparisonType.LessThan => CmpOp.Lt,
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => CmpOp.Le,
        BooleanComparisonType.GreaterThan => CmpOp.Gt,
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => CmpOp.Ge,

        _ => null,
    };

    private static CmpOp Flip(CmpOp op) => op switch
    {
        CmpOp.Lt => CmpOp.Gt,
        CmpOp.Gt => CmpOp.Lt,
        CmpOp.Le => CmpOp.Ge,
        CmpOp.Ge => CmpOp.Le,
        _ => op,
    };

    private static ColumnKey? TryGetColumnKey(ScalarExpression expr) =>
        expr is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } ids }
            ? new ColumnKey(
                string.Join(".", ids.Take(ids.Count - 1).Select(i => i.Value.ToLowerInvariant())),
                ids[^1].Value.ToLowerInvariant())
            : null;

    private static decimal? TryGetNumericLiteral(ScalarExpression expr) => expr switch
    {
        IntegerLiteral lit => ParseDecimal(lit.Value),
        NumericLiteral lit => ParseDecimal(lit.Value),
        MoneyLiteral lit => ParseDecimal(lit.Value),

        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary =>
            TryGetNumericLiteral(unary.Expression) is { } v ? -v : null,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary =>
            TryGetNumericLiteral(unary.Expression),
        _ => null,
    };

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? TryGetStringLiteral(ScalarExpression expr) =>
        expr is StringLiteral lit ? lit.Value : null;

    private readonly record struct LiteralConstraint(ColumnKey Column, CmpOp Op, decimal? Numeric, string? Str);

    private static LiteralConstraint? TryGetLiteralConstraint(BooleanComparisonExpression cmp)
    {
        var op = ToCmpOp(cmp.ComparisonType);
        if (op is null)
        {
            return null;
        }

        var rightLiteral = TryGetLiteralValue(cmp.SecondExpression);
        if (TryGetColumnKey(cmp.FirstExpression) is { } column && HasLiteralValue(rightLiteral))
        {
            return new LiteralConstraint(column, op.Value, rightLiteral.Numeric, rightLiteral.Str);
        }

        var leftLiteral = TryGetLiteralValue(cmp.FirstExpression);
        if (TryGetColumnKey(cmp.SecondExpression) is { } column2 && HasLiteralValue(leftLiteral))
        {
            return new LiteralConstraint(column2, Flip(op.Value), leftLiteral.Numeric, leftLiteral.Str);
        }

        return null;
    }

    private static (decimal? Numeric, string? Str) TryGetLiteralValue(ScalarExpression expr) =>

        expr is NullLiteral ? (null, null) : (TryGetNumericLiteral(expr), TryGetStringLiteral(expr));

    private static bool HasLiteralValue((decimal? Numeric, string? Str) value) => value.Numeric is not null || value.Str is not null;

    private static (bool Found, bool ColumnConfirmedNotNull) DetectContradiction(
        IReadOnlyList<BooleanExpression> conjuncts, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        foreach (var leaves in GroupByColumn(conjuncts).Select(group => group.Value))
        {
            var nullSeen = leaves.Any(c => c.IsNull);
            var notNullSeen = leaves.Any(c => c.IsNotNull);
            if (nullSeen && notNullSeen)
            {
                return (true, true);
            }

            if (DetectGroupContradiction(leaves, resolveColumnFacts) is { Found: true } contradiction)
            {
                return contradiction;
            }
        }

        return (false, false);
    }

    private static (bool Found, bool ColumnConfirmedNotNull) DetectGroupContradiction(
        IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        var confirmedNotNull = IsColumnNotNull(leaves, resolveColumnFacts) == true;
        var literalConstraints = leaves.Select(c => c.Constraint).Where(c => c is not null).Select(c => c!.Value).ToList();
        if (leaves.Any(c => c.IsNull) && literalConstraints.Count > 0)
        {
            return (true, confirmedNotNull);
        }

        var numeric = literalConstraints.Where(c => c.Numeric is not null)
            .Aggregate(NumericValueRangeSet.Universal, (ranges, constraint) => ranges.Intersect(ToRangeSet(constraint.Op, constraint.Numeric!.Value)));
        if (numeric.IsEmpty)
        {
            return (true, confirmedNotNull);
        }

        var (required, excluded) = PartitionStringConstraints(literalConstraints);
        if (required.Overlaps(excluded)
            || (required.Count >= 2 && IsColumnCaseSensitive(leaves, resolveColumnFacts) == true))
        {
            return (true, confirmedNotNull);
        }

        return (false, false);
    }

    private static bool DetectTautology(
        IReadOnlyList<BooleanExpression> disjuncts, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        foreach (var leaves in GroupByColumn(disjuncts).Select(group => group.Value))
        {
            var nullSeen = leaves.Any(c => c.IsNull);
            var notNullSeen = leaves.Any(c => c.IsNotNull);
            if (nullSeen && notNullSeen)
            {

                return true;
            }

            if (IsGroupTautology(leaves, resolveColumnFacts))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGroupTautology(IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        if (IsColumnNotNull(leaves, resolveColumnFacts) != true)
        {
            return false;
        }

        var literalConstraints = leaves.Select(c => c.Constraint).Where(c => c is not null).Select(c => c!.Value).ToList();
        var numericUnion = literalConstraints.Where(c => c.Numeric is not null)
            .Select(c => ToRangeSet(c.Op, c.Numeric!.Value))
            .Aggregate((NumericValueRangeSet?)null, Union);
        if (numericUnion?.HasFullCoverage == true)
        {
            return true;
        }

        var (required, excluded) = PartitionStringConstraints(literalConstraints);
        return required.Overlaps(excluded);
    }

    private static NumericValueRangeSet? Union(NumericValueRangeSet? current, NumericValueRangeSet next) => current is null ? next : current.Union(next);

    private static (HashSet<string> Required, HashSet<string> Excluded) PartitionStringConstraints(IEnumerable<LiteralConstraint> constraints)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in constraints.Where(constraint => constraint.Str is not null))
        {
            if (constraint.Op == CmpOp.Eq)
            {
                required.Add(constraint.Str!);
            }
            else if (constraint.Op == CmpOp.Ne)
            {
                excluded.Add(constraint.Str!);
            }
        }

        return (required, excluded);
    }

    private static NumericValueRangeSet ToRangeSet(CmpOp op, decimal value) => op switch
    {
        CmpOp.Eq => NumericValueRangeSet.ForEquals(value),
        CmpOp.Ne => NumericValueRangeSet.ForNotEquals(value),
        CmpOp.Lt => NumericValueRangeSet.ForLessThan(value),
        CmpOp.Le => NumericValueRangeSet.ForLessThanOrEqual(value),
        CmpOp.Gt => NumericValueRangeSet.ForGreaterThan(value),
        CmpOp.Ge => NumericValueRangeSet.ForGreaterThanOrEqual(value),
        _ => NumericValueRangeSet.Universal,
    };

    private readonly record struct GroupedLeaf(bool IsNull, bool IsNotNull, LiteralConstraint? Constraint, ColumnReferenceExpression ColumnRef);

    private static Dictionary<ColumnKey, List<GroupedLeaf>> GroupByColumn(IReadOnlyList<BooleanExpression> members)
    {
        var groups = new Dictionary<ColumnKey, List<GroupedLeaf>>();

        void Add(ColumnKey key, GroupedLeaf leaf)
        {
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(leaf);
        }

        foreach (var member in members)
        {
            var unwrapped = member;
            while (unwrapped is BooleanParenthesisExpression paren)
            {
                unwrapped = paren.Expression;
            }

            switch (unwrapped)
            {
                case BooleanIsNullExpression { Expression: ColumnReferenceExpression colRef } isNull
                    when TryGetColumnKey(colRef) is { } key:
                    Add(key, new GroupedLeaf(!isNull.IsNot, isNull.IsNot, null, colRef));
                    break;

                case BooleanComparisonExpression cmp when TryGetLiteralConstraint(cmp) is { } constraint:
                    var colRef2 = cmp.FirstExpression as ColumnReferenceExpression ?? cmp.SecondExpression as ColumnReferenceExpression;
                    if (colRef2 is not null)
                    {
                        Add(constraint.Column, new GroupedLeaf(false, false, constraint, colRef2));
                    }

                    break;

                case BooleanTernaryExpression { TernaryExpressionType: BooleanTernaryExpressionType.Between } between
                    when TryGetColumnKey(between.FirstExpression) is { } key
                        && between.FirstExpression is ColumnReferenceExpression colRef3
                        && TryGetNumericLiteral(between.SecondExpression) is { } lower
                        && TryGetNumericLiteral(between.ThirdExpression) is { } upper:
                    Add(key, new GroupedLeaf(false, false, new LiteralConstraint(key, CmpOp.Ge, lower, null), colRef3));
                    Add(key, new GroupedLeaf(false, false, new LiteralConstraint(key, CmpOp.Le, upper, null), colRef3));
                    break;
            }
        }

        return groups;
    }

    private static bool? IsColumnNotNull(
        IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts) =>
        leaves.Count > 0 ? resolveColumnFacts(leaves[0].ColumnRef).IsNotNull : null;

    private static bool? IsColumnCaseSensitive(
        IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts) =>
        leaves.Count > 0 ? resolveColumnFacts(leaves[0].ColumnRef).IsCaseSensitiveCollation : null;
}
