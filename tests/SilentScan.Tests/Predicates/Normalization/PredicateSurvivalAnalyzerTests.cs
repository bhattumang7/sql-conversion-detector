using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Tests.Predicates.Normalization;

/// <summary>
/// Pure algebra tests - no catalog, no oracle needed, since three-valued boolean logic and
/// interval arithmetic are deterministic math, exhaustively checkable by hand. Column facts are
/// driven by naming convention: <c>NotNullCol</c>/<c>NullableCol</c>/<c>UnknownNullCol</c> for
/// nullability, <c>CsCol</c>/<c>CiCol</c>/<c>UnknownCollationCol</c> for collation
/// case-sensitivity - every column referenced in a test's WHERE clause must use one of these
/// names so <see cref="ResolveFacts"/> can answer deterministically.
/// </summary>
public sealed class PredicateSurvivalAnalyzerTests
{
    private static PredicateSurvivalAnalyzer.ColumnFacts ResolveFacts(ColumnReferenceExpression colRef)
    {
        var name = colRef.MultiPartIdentifier.Identifiers[^1].Value;
        bool? isNotNull = name switch
        {
            "NotNullCol" => true,
            "NullableCol" => false,
            _ => null,
        };
        bool? isCaseSensitive = name switch
        {
            "CsCol" => true,
            "CiCol" => false,
            _ => null,
        };

        return new PredicateSurvivalAnalyzer.ColumnFacts(isNotNull, isCaseSensitive);
    }

    private sealed class LeafCollector : TSqlFragmentVisitor
    {
        public List<TSqlFragment> Leaves { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node) => Leaves.Add(node);

        public override void ExplicitVisit(BooleanIsNullExpression node) => Leaves.Add(node);

        public override void ExplicitVisit(BooleanTernaryExpression node) => Leaves.Add(node);
    }

    private static BooleanExpression ParseCondition(string whereExpr)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"SELECT 1 WHERE {whereExpr};");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var select = Assert.IsType<TSqlScript>(result.Fragment).Batches[0].Statements
            .OfType<SelectStatement>().Single();
        return select.QueryExpression is QuerySpecification { WhereClause.SearchCondition: { } sc }
            ? sc
            : throw new InvalidOperationException("no WHERE clause parsed");
    }

    /// <summary>Single parse per test case: the dead set is keyed by AST reference identity, so the
    /// dead set and the leaf list being compared must come from the exact same parse.</summary>
    private static (IReadOnlySet<TSqlFragment> Dead, IReadOnlyList<TSqlFragment> Leaves) Analyze(string whereExpr)
    {
        var condition = ParseCondition(whereExpr);

        var collector = new LeafCollector();
        condition.Accept(collector);

        var dead = PredicateSurvivalAnalyzer.FindDeadComparisons(condition, ResolveFacts);
        return (dead, collector.Leaves);
    }

    // ---- AND contradiction: numeric range algebra ----

    [Fact]
    public void SameColumnConflictingEquality_BothMarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1 AND NotNullCol = 2");

        Assert.Equal(2, leaves.Count);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameColumnDisjointRanges_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol > 5 AND NotNullCol < 3");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameColumnTouchingExclusiveBounds_MarkedDead()
    {
        // x < 5 AND x > 5: the single point 5 is excluded by both sides - genuinely empty.
        var (dead, leaves) = Analyze("NotNullCol < 5 AND NotNullCol > 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameColumnAdjacentInclusiveBounds_NotDead()
    {
        // x <= 5 AND x >= 5: exactly satisfiable at x=5 - not a contradiction.
        var (dead, _) = Analyze("NotNullCol <= 5 AND NotNullCol >= 5");
        Assert.Empty(dead);
    }

    [Fact]
    public void EqualityWithinRange_NotDead()
    {
        var (dead, _) = Analyze("NotNullCol = 4 AND NotNullCol > 1 AND NotNullCol < 10");
        Assert.Empty(dead);
    }

    [Fact]
    public void EqualityOutsideRange_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 20 AND NotNullCol < 10");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void EqualityMatchingExclusion_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 5 AND NotNullCol <> 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void DifferentColumns_NeverConflated()
    {
        var (dead, _) = Analyze("NotNullCol = 1 AND NullableCol = 2");
        Assert.Empty(dead);
    }

    [Fact]
    public void SelfJoinAliasesSameSchemaColumn_NeverConflated()
    {
        // t1.NotNullCol and t2.NotNullCol are syntactically distinct qualifiers - even though a
        // real self-join could resolve both to the same underlying table, this analyzer only sees
        // the reference text and must never guess they're the same value.
        var (dead, _) = Analyze("t1.NotNullCol = 1 AND t2.NotNullCol = 2");
        Assert.Empty(dead);
    }

    [Fact]
    public void UnrelatedSiblingConjunct_AlsoMarkedDeadWhenAndIsUnsatisfiable()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1 AND NotNullCol = 2 AND NullableCol = 9");

        Assert.Equal(3, leaves.Count);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    // ---- AND contradiction: IS NULL vs comparison ----

    [Fact]
    public void IsNullWithComparison_MarkedDead()
    {
        var (dead, leaves) = Analyze("NullableCol IS NULL AND NullableCol = 1");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void IsNullAndIsNotNull_MarkedDead()
    {
        var (dead, leaves) = Analyze("NullableCol IS NULL AND NullableCol IS NOT NULL");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void IsNotNullWithComparison_NotDead()
    {
        var (dead, _) = Analyze("NullableCol IS NOT NULL AND NullableCol = 1");
        Assert.Empty(dead);
    }

    // ---- AND contradiction: BETWEEN ----

    [Fact]
    public void SelfContradictoryBetween_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol BETWEEN 10 AND 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void OrdinaryBetween_NotDead()
    {
        var (dead, _) = Analyze("NotNullCol BETWEEN 5 AND 10");
        Assert.Empty(dead);
    }

    [Fact]
    public void BetweenConflictingWithSiblingEquality_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol BETWEEN 5 AND 10 AND NotNullCol = 20");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    // ---- AND contradiction: string equality (collation-gated) ----

    [Fact]
    public void SameLiteralRequiredAndExcluded_MarkedDeadRegardlessOfCollation()
    {
        var (dead, leaves) = Analyze("UnknownCollationCol = 'foo' AND UnknownCollationCol <> 'foo'");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void DifferentLiteralsRequiredEquals_CaseSensitiveConfirmed_MarkedDead()
    {
        var (dead, leaves) = Analyze("CsCol = 'foo' AND CsCol = 'bar'");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void DifferentLiteralsRequiredEquals_CaseInsensitiveOrUnknownCollation_NeverConcluded()
    {
        // 'Foo' and 'foo' could legally collate equal - never safe to fold without confirmation.
        Assert.Empty(Analyze("CiCol = 'Foo' AND CiCol = 'foo'").Dead);
        Assert.Empty(Analyze("UnknownCollationCol = 'Foo' AND UnknownCollationCol = 'foo'").Dead);
    }

    // ---- Pure literal-vs-literal constant folding ----

    [Fact]
    public void ConstantFalseComparison_MarkedDead()
    {
        var (dead, leaves) = Analyze("1 = 2");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void ConstantFalseComparison_SiblingConjunctAlsoMarkedDead()
    {
        var (dead, leaves) = Analyze("1 = 2 AND NotNullCol = 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    // ---- OR composition: all-disjuncts-dead propagates ----

    [Fact]
    public void OrOfTwoContradictions_AllMarkedDead()
    {
        var (dead, leaves) = Analyze("(NotNullCol = 1 AND NotNullCol = 2) OR (NullableCol = 3 AND NullableCol = 4)");
        Assert.Equal(4, leaves.Count);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void OrWithOneLiveDisjunct_NothingMarkedDead()
    {
        var (dead, leaves) = Analyze("(NotNullCol = 1 AND NotNullCol = 2) OR NullableCol = 3");

        // Only the dead disjunct's own two comparisons are marked - the live one survives.
        Assert.Equal(2, dead.Count);
        Assert.DoesNotContain(leaves[2], dead);
    }

    // ---- OR tautology: requires NOT NULL ----

    [Fact]
    public void EqualsOrNotEquals_SameLiteral_NotNullColumn_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1 OR NotNullCol <> 1");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void EqualsOrNotEquals_SameLiteral_NullableColumn_NeverConcluded()
    {
        // If the column is NULL, both sides are UNKNOWN - not a tautology when NULL is possible.
        Assert.Empty(Analyze("NullableCol = 1 OR NullableCol <> 1").Dead);
    }

    [Fact]
    public void EqualsOrNotEquals_SameLiteral_UnknownNullability_NeverConcluded()
    {
        Assert.Empty(Analyze("UnknownNullCol = 1 OR UnknownNullCol <> 1").Dead);
    }

    [Fact]
    public void NumericRangeUnionCoversEverything_NotNullColumn_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol < 5 OR NotNullCol >= 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NumericRangeUnionLeavesGap_NotDead()
    {
        Assert.Empty(Analyze("NotNullCol < 5 OR NotNullCol > 5").Dead);
    }

    [Fact]
    public void StringEqualsOrNotEquals_SameLiteral_NotNullColumn_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 'a' OR NotNullCol <> 'a'");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    // ---- OR tautology: IS NULL / IS NOT NULL needs no nullability confirmation at all ----

    [Fact]
    public void IsNullOrIsNotNull_UnconditionallyMarkedDead()
    {
        var (dead, leaves) = Analyze("UnknownNullCol IS NULL OR UnknownNullCol IS NOT NULL");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    // ---- NOT boundary ----

    [Fact]
    public void NotOfContradiction_NotNullColumn_MarkedDead()
    {
        // NOT(x=1 AND x=2) on a column confirmed NOT NULL: the inner AND is strictly False for
        // every row (never Unknown, since x can never be null), so the negation is an unconditional
        // tautology and the inner comparisons never reach a residual filter either.
        var (dead, leaves) = Analyze("NOT (NotNullCol = 1 AND NotNullCol = 2)");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NotOfContradiction_NullableOrUnconfirmedColumn_NeverConcluded()
    {
        // Same shape, but without a NOT NULL guarantee: for a null x, x=1 AND x=2 is Unknown (not
        // False), so NOT(...) is also Unknown for that row - not an unconditional tautology, and
        // the inner comparisons must not be marked dead just because the un-negated form would have
        // been (on a confirmed NOT NULL column) or was declined (here).
        Assert.Empty(Analyze("NOT (NullableCol = 1 AND NullableCol = 2)").Dead);
        Assert.Empty(Analyze("NOT (UnknownNullCol = 1 AND UnknownNullCol = 2)").Dead);
    }

    [Fact]
    public void NotOfTautology_MarkedDead()
    {
        var (dead, leaves) = Analyze("NOT (NotNullCol = 1 OR NotNullCol <> 1)");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NotOfOrContainingContradiction_InnerContradictionNeverConcluded()
    {
        // NOT((x=1 AND x=2) OR y=5): for a null x and a non-null y<>5, the inner OR is Unknown OR
        // False = Unknown, so the whole NOT is Unknown (row excluded) - NOT True as naively
        // discarding the never-true first disjunct would wrongly conclude. Marking must not cross
        // this NOT boundary at all.
        Assert.Empty(Analyze("NOT ((NotNullCol = 1 AND NotNullCol = 2) OR NullableCol = 5)").Dead);
    }

    // ---- Opaque leaves: never guessed at ----

    [Fact]
    public void NonLiteralComparison_NeverConcluded()
    {
        Assert.Empty(Analyze("NotNullCol = OtherCol AND NotNullCol = 2").Dead);
    }

    [Fact]
    public void NullLiteralComparison_NeverConcluded()
    {
        // x = NULL is ANSI_NULLS-dependent and declined entirely, not treated as an ordinary
        // comparison and not treated as IS NULL either.
        Assert.Empty(Analyze("NullableCol = NULL AND NullableCol IS NOT NULL").Dead);
    }

    [Fact]
    public void IsUnsatisfiable_ContradictoryWhereClause_True()
    {
        var condition = ParseCondition("NotNullCol = 1 AND NotNullCol = 2");
        Assert.True(PredicateSurvivalAnalyzer.IsUnsatisfiable(condition, ResolveFacts));
    }

    [Fact]
    public void IsUnsatisfiable_OrdinaryWhereClause_False()
    {
        var condition = ParseCondition("NotNullCol = 1");
        Assert.False(PredicateSurvivalAnalyzer.IsUnsatisfiable(condition, ResolveFacts));
    }

    [Fact]
    public void IsUnsatisfiable_ContradictionInsideALiveOr_False()
    {
        var condition = ParseCondition("(NotNullCol = 1 AND NotNullCol = 2) OR NullableCol = 5");
        Assert.False(PredicateSurvivalAnalyzer.IsUnsatisfiable(condition, ResolveFacts));
    }
}
