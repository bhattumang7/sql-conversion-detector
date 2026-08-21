using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates.Normalization;

/// <summary>
/// docs/detection-reference.md "Predicate survival (normalization/simplification)": the engine
/// rewrites, and sometimes eliminates, predicates before sargability is ever considered - a
/// predicate a scanner flags may never survive to reach the plan in the form flagged, which makes
/// the finding wrong. This is the missing normalization stage, built the same way the real
/// optimizer does it: build the set of still-possible values for a column from every literal
/// comparison touching it, intersect that set across an AND (a real contradiction is exactly the
/// intersection going empty), union it across an OR (a real tautology is exactly the union
/// covering every possible value, NULL included). <see cref="FindDeadComparisons"/> returns the
/// comparison-shaped fragments that live inside a branch proven this way to never contribute a
/// selected row - callers decline to report a finding whose own site is in that set, the same way
/// they already decline one that never resolves to a real base column.
///
/// Deliberately bounded to what the value-range algebra can prove without guessing: numeric
/// literals get the full ordered range algebra (<see cref="NumericValueRangeSet"/>) since numeric
/// ordering is collation-independent; string literals get equality/inequality only (range
/// operators on strings depend on collation ordering this analyzer does not model) and a
/// cross-literal equality conflict is only ever concluded when the caller confirms the column's
/// collation is case-sensitive/binary - two different string literals can otherwise legally
/// collate equal, so "different text" alone is never proof of "different value." A tautology
/// conclusion additionally requires the caller to confirm the column is NOT NULL, except for the
/// one case that needs no such confirmation at all: <c>col IS NULL OR col IS NOT NULL</c>, which
/// is unconditionally true regardless of nullability since those two predicates are themselves
/// never UNKNOWN. <c>NOT</c> is treated as a boundary in the tautology direction (this analyzer
/// does not attempt to prove an expression is always strictly FALSE, only "never TRUE" and "always
/// TRUE", so it cannot conclude what <c>NOT</c> of an always-FALSE operand would be) but is
/// followed in the "never true" direction: <c>NOT</c> of a proven tautology is itself proven dead.
/// </summary>
public static class PredicateSurvivalAnalyzer
{
    /// <summary>Per-column facts the caller already has from the catalog - both default to the
    /// safe "cannot conclude" direction when unresolved (<see langword="null"/>).</summary>
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

    private readonly record struct ColumnKey(string Qualifier, string Name);

    private enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

    // NeverTrue: True-or-Unknown is impossible, the value is False-or-Unknown for every row (drives
    // "dead": a NeverTrue subtree never contributes a selected row). AlwaysTrue: True for every row,
    // Unknown is impossible too (the strict tautology direction - this already requires ruling out
    // NULL, so nothing else needs to distinguish "weak" vs "strict" true). AlwaysFalse: the strict
    // dual of AlwaysTrue - False for every row, Unknown is impossible too. AlwaysFalse implies
    // NeverTrue but is a strictly stronger claim, needed only to justify what NOT does: NOT of a
    // merely-NeverTrue operand is NOT provably anything (e.g. NOT(x=1 AND x=2) is True for every
    // non-null x but UNKNOWN for a null x, so it is NOT an unconditional tautology on a nullable
    // column - only AlwaysFalse(x=1 AND x=2), which requires x confirmed NOT NULL, licenses
    // NOT(...) being AlwaysTrue). All three false means "no conclusion" - the state for any leaf
    // this analyzer doesn't model (subqueries, LIKE, IN, function calls, ...).
    //
    // Pure - no marking here. A node's own NeverTrue/AlwaysTrue is only safe to act on from the
    // position that actually consumes it (an enclosing AND/OR dropping a conjunct/disjunct, a NOT
    // flipping it, or the top-level search condition itself) - marking inside Classify itself, as a
    // side effect of computing a child's verdict for an AND/OR/NOT composition, would mark a
    // subtree's leaves dead even when the composition ends up NOT actually treating it as dead (the
    // textbook case: the inner AND of NOT(x=1 AND x=2) is a real contradiction on its own, but NOT
    // of it is a near-tautology, not a dead branch - marking during the inner AND's own recursive
    // classification would have marked x=1/x=2 regardless of what NOT then did with that fact).
    // See <see cref="MarkDead"/> for the separate pass that actually mutates the dead set.
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
                    // False dominates Unknown in AND: if any conjunct - including the winning side
                    // of a cross-conjunct contradiction, when its own column is confirmed NOT NULL -
                    // is strictly False for every row, the whole AND is strictly False too,
                    // regardless of whether any OTHER conjunct could otherwise be Unknown.
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

                // NOT(AlwaysTrue) is AlwaysFalse (so also NeverTrue); NOT(AlwaysFalse) is AlwaysTrue.
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

    /// <summary>The actual mutating pass: marks <paramref name="node"/>'s own leaves dead outright
    /// when its OWN classification is fully resolved either way (NeverTrue or AlwaysTrue - both mean
    /// nothing inside it ever meaningfully gates row selection, regardless of what encloses it),
    /// otherwise recurses structurally to find a nested opportunity a coarser verdict on this whole
    /// node couldn't see (e.g. one dead disjunct inside an otherwise-live OR).</summary>
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
                foreach (var c in Flatten(node, BooleanBinaryExpressionType.And))
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
                // Deliberately does NOT recurse into the operand. A NeverTrue-but-not-AlwaysFalse
                // sub-expression can still change how Unknown propagates once wrapped in NOT (NOT of
                // "False for non-null x, Unknown for null x" is "True for non-null x, Unknown for
                // null x" - not equivalent to just discarding the sub-expression), so a nested
                // opportunity below a NOT this analyzer didn't already resolve at the NOT node's own
                // level (via Classify's And/Or/Not composition above) is not safe to act on without
                // a real negation-normal-form rewrite, which this analyzer does not attempt.
                break;

            default:
                break; // a leaf neither NeverTrue nor AlwaysTrue on its own: nothing to mark.
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

    /// <summary>A whole subtree proven dead never contributes a selected row, regardless of what's
    /// nested inside it (including past a NOT boundary) - so every comparison-shaped fragment
    /// reachable from here is marked, not just the ones this analyzer's own algebra understands.</summary>
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
            // x BETWEEN 10 AND 5 (lower>upper) is Unknown for a null x, False for every non-null x -
            // strictly False (AlwaysFalse) only once the column is confirmed never null.
            var alwaysFalse = between.FirstExpression is ColumnReferenceExpression colRef
                && resolveColumnFacts(colRef).IsNotNull == true;
            return (true, false, alwaysFalse);
        }

        return (false, false, false);
    }

    /// <summary>Both sides already constants - <c>1 = 2</c> style. A genuinely separate, cheaper
    /// case from the column-comparison algebra below: no column, no range, just direct evaluation -
    /// so a constant-false result is unconditionally <c>AlwaysFalse</c>, never merely
    /// <c>NeverTrue</c> (there is no column, so no row's value could ever make it Unknown).</summary>
    private static (bool, bool, bool) ClassifyConstantComparison(BooleanComparisonExpression cmp)
    {
        var op = ToCmpOp(cmp.ComparisonType);
        if (op is null)
        {
            return (false, false, false);
        }

        bool? result =
            TryGetNumericLiteral(cmp.FirstExpression) is { } a && TryGetNumericLiteral(cmp.SecondExpression) is { } b
                ? EvaluateNumeric(op.Value, a, b)
            : TryGetStringLiteral(cmp.FirstExpression) is { } sa && TryGetStringLiteral(cmp.SecondExpression) is { } sb
                ? EvaluateString(op.Value, sa, sb)
            : null;

        if (result == false)
        {
            return (true, false, true);
        }

        return (false, result == true, false);
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
        _ => null, // ordering between string literals is collation-dependent - not modeled
    };

    private static CmpOp? ToCmpOp(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => CmpOp.Eq,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => CmpOp.Ne,
        BooleanComparisonType.LessThan => CmpOp.Lt,
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => CmpOp.Le,
        BooleanComparisonType.GreaterThan => CmpOp.Gt,
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => CmpOp.Ge,
        // LeftOuterJoin/RightOuterJoin (legacy *=) and IsDistinctFrom/IsNotDistinctFrom have their
        // own NULL semantics this analyzer does not model - declined, not guessed.
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
        // RealLiteral (float/real, approximate binary representation) deliberately excluded - exact
        // decimal arithmetic over it would itself be a wrong model of the engine's own comparison.
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

        if (TryGetColumnKey(cmp.FirstExpression) is { } column && TryGetLiteralValue(cmp.SecondExpression) is var (num, str) && (num is not null || str is not null))
        {
            return new LiteralConstraint(column, op.Value, num, str);
        }

        if (TryGetColumnKey(cmp.SecondExpression) is { } column2 && TryGetLiteralValue(cmp.FirstExpression) is var (num2, str2) && (num2 is not null || str2 is not null))
        {
            return new LiteralConstraint(column2, Flip(op.Value), num2, str2);
        }

        return null;
    }

    private static (decimal? Numeric, string? Str) TryGetLiteralValue(ScalarExpression expr) =>
        // A NULL-literal comparison (x = NULL) has ANSI_NULLS-dependent meaning (UNKNOWN under the
        // standard setting, IS-NULL-equivalent under the legacy OFF setting) that isn't visible from
        // the expression alone - declined entirely, never folded either way.
        expr is NullLiteral ? (null, null) : (TryGetNumericLiteral(expr), TryGetStringLiteral(expr));

    /// <summary>True/<c>ColumnConfirmedNotNull</c>: whether the winning contradiction's own column
    /// is confirmed NOT NULL - see <see cref="Classify"/>'s doc comment on <c>AlwaysFalse</c> for
    /// why that distinction matters to a caller wrapping this in NOT. <c>IS NULL AND IS NOT NULL</c>
    /// needs no such confirmation (both are strictly two-valued, never Unknown, regardless of the
    /// column's real nullability) so it reports <see langword="true"/> unconditionally; every other
    /// shape genuinely depends on the column never being null to rule out the Unknown case.</summary>
    private static (bool Found, bool ColumnConfirmedNotNull) DetectContradiction(
        IReadOnlyList<BooleanExpression> conjuncts, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        foreach (var group in GroupByColumn(conjuncts))
        {
            var nullSeen = group.Value.Any(c => c.IsNull);
            var notNullSeen = group.Value.Any(c => c.IsNotNull);
            if (nullSeen && notNullSeen)
            {
                return (true, true);
            }

            var confirmedNotNull = IsNotNull(group.Key, group.Value, resolveColumnFacts) == true;

            var literalConstraints = group.Value.Select(c => c.Constraint).Where(c => c is not null).Select(c => c!.Value).ToList();
            var anyValueComparisonSeen = literalConstraints.Count > 0;
            if (nullSeen && anyValueComparisonSeen)
            {
                return (true, confirmedNotNull);
            }

            var numeric = NumericValueRangeSet.Universal;
            foreach (var c in literalConstraints.Where(c => c.Numeric is not null))
            {
                numeric = numeric.Intersect(ToRangeSet(c.Op, c.Numeric!.Value));
            }

            if (numeric.IsEmpty)
            {
                return (true, confirmedNotNull);
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in literalConstraints.Where(c => c.Str is not null))
            {
                if (c.Op == CmpOp.Eq)
                {
                    required.Add(c.Str!);
                }
                else if (c.Op == CmpOp.Ne)
                {
                    excluded.Add(c.Str!);
                }
            }

            if (required.Overlaps(excluded))
            {
                return (true, confirmedNotNull);
            }

            if (required.Count >= 2 && IsCaseSensitive(group.Key, group.Value, resolveColumnFacts) == true)
            {
                return (true, confirmedNotNull);
            }
        }

        return (false, false);
    }

    private static bool DetectTautology(
        IReadOnlyList<BooleanExpression> disjuncts, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts)
    {
        foreach (var group in GroupByColumn(disjuncts))
        {
            var nullSeen = group.Value.Any(c => c.IsNull);
            var notNullSeen = group.Value.Any(c => c.IsNotNull);
            if (nullSeen && notNullSeen)
            {
                // col IS NULL OR col IS NOT NULL - unconditionally true, neither side is ever UNKNOWN.
                return true;
            }

            if (IsNotNull(group.Key, group.Value, resolveColumnFacts) != true)
            {
                // Every other tautology shape below relies on covering every non-null value; without
                // a confirmed NOT NULL column, a row where the column is NULL makes every comparison
                // UNKNOWN, so the OR is never unconditionally true regardless of value coverage.
                continue;
            }

            var literalConstraints = group.Value.Select(c => c.Constraint).Where(c => c is not null).Select(c => c!.Value).ToList();

            var numericUnion = literalConstraints.Where(c => c.Numeric is not null)
                .Select(c => ToRangeSet(c.Op, c.Numeric!.Value))
                .Aggregate((NumericValueRangeSet?)null, (acc, next) => acc is null ? next : acc.Union(next));
            if (numericUnion?.HasFullCoverage == true)
            {
                return true;
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in literalConstraints.Where(c => c.Str is not null))
            {
                if (c.Op == CmpOp.Eq)
                {
                    required.Add(c.Str!);
                }
                else if (c.Op == CmpOp.Ne)
                {
                    excluded.Add(c.Str!);
                }
            }

            // x = V (one disjunct) OR x <> V (another disjunct), same literal V: together cover
            // every non-null value regardless of collation, since equality is reflexive no matter
            // how the collation orders anything else.
            if (required.Overlaps(excluded))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>Groups the direct (unnested) members of one AND/OR list by the exact column they
    /// constrain - keyed on the reference's own textual qualifier, never a resolved table identity,
    /// so two different aliases of the same self-joined table are never conflated (they read as two
    /// different keys, which only ever costs a missed conclusion, never a wrong one).</summary>
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

    private static bool? IsNotNull(
        ColumnKey key, IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts) =>
        leaves.Count > 0 ? resolveColumnFacts(leaves[0].ColumnRef).IsNotNull : null;

    private static bool? IsCaseSensitive(
        ColumnKey key, IReadOnlyList<GroupedLeaf> leaves, Func<ColumnReferenceExpression, ColumnFacts> resolveColumnFacts) =>
        leaves.Count > 0 ? resolveColumnFacts(leaves[0].ColumnRef).IsCaseSensitiveCollation : null;
}
