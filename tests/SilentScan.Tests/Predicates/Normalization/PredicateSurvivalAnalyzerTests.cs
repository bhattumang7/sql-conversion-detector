using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Tests.Predicates.Normalization;

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
        bool? guaranteesDistinctLiterals = name switch
        {
            "CsCol" => true,
            "CiCol" => false,
            "CsAiCol" => false,
            _ => null,
        };

        return new PredicateSurvivalAnalyzer.ColumnFacts(isNotNull, guaranteesDistinctLiterals);
    }

    private sealed class LeafCollector : TSqlFragmentVisitor
    {
        public List<TSqlFragment> Leaves { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node) => Leaves.Add(node);

        public override void ExplicitVisit(BooleanIsNullExpression node) => Leaves.Add(node);

        public override void ExplicitVisit(BooleanTernaryExpression node) => Leaves.Add(node);

        public override void ExplicitVisit(LikePredicate node) => Leaves.Add(node);
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

    private static (IReadOnlySet<TSqlFragment> Dead, IReadOnlyList<TSqlFragment> Leaves) Analyze(string whereExpr)
    {
        var condition = ParseCondition(whereExpr);

        var collector = new LeafCollector();
        condition.Accept(collector);

        var dead = PredicateSurvivalAnalyzer.FindDeadComparisons(condition, ResolveFacts);
        return (dead, collector.Leaves);
    }

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

        var (dead, leaves) = Analyze("NotNullCol < 5 AND NotNullCol > 5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameColumnAdjacentInclusiveBounds_NotDead()
    {

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

        Assert.Empty(Analyze("CiCol = 'Foo' AND CiCol = 'foo'").Dead);
        Assert.Empty(Analyze("UnknownCollationCol = 'Foo' AND UnknownCollationCol = 'foo'").Dead);
    }

    [Fact]
    public void DifferentLiteralsRequiredEquals_CaseSensitiveButAccentInsensitiveCollation_NeverConcluded()
    {
        Assert.Empty(Analyze("CsAiCol = 'cafe' AND CsAiCol = 'café'").Dead);
    }

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

        Assert.Equal(2, dead.Count);
        Assert.DoesNotContain(leaves[2], dead);
    }

    [Fact]
    public void EqualsOrNotEquals_SameLiteral_NotNullColumn_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1 OR NotNullCol <> 1");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void EqualsOrNotEquals_SameLiteral_NullableColumn_NeverConcluded()
    {

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

    [Fact]
    public void IsNullOrIsNotNull_UnconditionallyMarkedDead()
    {
        var (dead, leaves) = Analyze("UnknownNullCol IS NULL OR UnknownNullCol IS NOT NULL");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NotOfContradiction_NotNullColumn_MarkedDead()
    {

        var (dead, leaves) = Analyze("NOT (NotNullCol = 1 AND NotNullCol = 2)");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NotOfContradiction_NullableOrUnconfirmedColumn_NeverConcluded()
    {

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

        Assert.Empty(Analyze("NOT ((NotNullCol = 1 AND NotNullCol = 2) OR NullableCol = 5)").Dead);
    }

    [Fact]
    public void EquivalentOuterConjunct_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1 AND (NotNullCol = 1 OR NullableCol = 2)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void ReversedEquality_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("1 = NotNullCol AND (NotNullCol = 1 OR NullableCol = 2)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void DifferentOuterConjunct_DoesNotAbsorbInnerDisjunction()
    {
        Assert.Empty(Analyze("NotNullCol = 1 AND (NotNullCol = 2 OR NullableCol = 2)").Dead);
    }

    [Fact]
    public void NonLiteralComparison_NeverConcluded()
    {
        Assert.Empty(Analyze("NotNullCol = OtherCol AND NotNullCol = 2").Dead);
    }

    [Fact]
    public void NullLiteralComparison_NeverConcluded()
    {

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

    [Fact]
    public void EquivalentOuterIsNullConjunct_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("NullableCol IS NULL AND (NullableCol IS NULL OR NotNullCol = 1)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void EquivalentOuterNumericLiteralConjunct_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("NotNullCol = 1.5 AND (NotNullCol = 1.5 OR NullableCol = 2)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void EquivalentOuterMoneyLiteralConjunct_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("NotNullCol = $1.50 AND (NotNullCol = $1.50 OR NullableCol = 2)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void EquivalentOuterVariableComparisonConjunct_AbsorbsInnerDisjunction()
    {
        var (dead, leaves) = Analyze("@x = @y AND (@x = @y OR NullableCol = 2)");

        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
        Assert.Contains(leaves[2], dead);
    }

    [Fact]
    public void SingleParenthesizedLiveComparison_NeverConcluded()
    {
        Assert.Empty(Analyze("(NotNullCol = 1)").Dead);
    }

    [Fact]
    public void StringLiteralVsLiteralFalseEquality_MarkedDead()
    {
        var (dead, leaves) = Analyze("'a' = 'b' AND NotNullCol = 1");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Theory]
    [InlineData("2 <> 2 AND NotNullCol = 1")]
    [InlineData("2 < 1 AND NotNullCol = 1")]
    [InlineData("2 <= 1 AND NotNullCol = 1")]
    [InlineData("1 > 2 AND NotNullCol = 1")]
    [InlineData("1 >= 2 AND NotNullCol = 1")]
    public void NumericLiteralVsLiteralFalseComparison_VariousOperators_MarkedDead(string whereExpr)
    {
        var (dead, leaves) = Analyze(whereExpr);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void StringLiteralVsLiteralFalseInequality_MarkedDead()
    {
        var (dead, leaves) = Analyze("'a' <> 'a' AND NotNullCol = 1");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void StringLiteralVsLiteralUnsupportedOperator_NeverConcluded()
    {
        Assert.Empty(Analyze("'a' < 'b' AND NotNullCol = 1").Dead);
    }

    [Fact]
    public void MoneyLiteralBetween_NeverCrashes()
    {
        Analyze("NotNullCol BETWEEN $1 AND $10");
    }

    [Fact]
    public void UnaryPositiveAndNegativeLiteralEquality_Contradiction_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = +5 AND NotNullCol = -5");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void LiteralLikePatternWithoutWildcards_ContradictsDifferentEquality_MarkedDead()
    {
        var (dead, leaves) = Analyze("CsCol = 'a' AND CsCol LIKE 'b'");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void LiteralLikePatternWithoutWildcards_MatchingEquality_NotDead()
    {
        Assert.Empty(Analyze("NotNullCol = 'a' AND NotNullCol LIKE 'a'").Dead);
    }

    [Fact]
    public void NotLikeLiteralPattern_SameLiteralAsEquality_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol = 'a' AND NotNullCol NOT LIKE 'a'");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void NotLikeLiteralPattern_DifferentLiteralFromEquality_NotDead()
    {
        Assert.Empty(Analyze("NotNullCol = 'a' AND NotNullCol NOT LIKE 'b'").Dead);
    }

    [Fact]
    public void LikePatternWithWildcard_NeverFoldedIntoLiteralConstraint()
    {
        Assert.Empty(Analyze("NotNullCol = 'a' AND NotNullCol LIKE 'b%'").Dead);
    }

    [Fact]
    public void LikePatternWithEscapeClause_NeverFoldedIntoLiteralConstraint()
    {
        Assert.Empty(Analyze("NotNullCol = 'a' AND NotNullCol LIKE 'b' ESCAPE '\\'").Dead);
    }

    [Theory]
    [InlineData("NotNullCol > 5 AND 3 > NotNullCol")]
    [InlineData("NotNullCol > 5 AND 3 >= NotNullCol")]
    [InlineData("NotNullCol < 1 AND 5 < NotNullCol")]
    [InlineData("NotNullCol < 1 AND 5 <= NotNullCol")]
    public void LiteralFirstRangeComparison_ContradictsColumnFirstComparison_MarkedDead(string whereExpr)
    {
        var (dead, leaves) = Analyze(whereExpr);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameCastWrappedColumnConflictingEquality_BothMarkedDead()
    {
        var (dead, leaves) = Analyze("CAST(NotNullCol AS INT) = 1 AND CAST(NotNullCol AS INT) = 2");

        Assert.Equal(2, leaves.Count);
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameConvertWrappedColumnConflictingEquality_BothMarkedDead()
    {
        var (dead, leaves) = Analyze("CONVERT(INT, NotNullCol) = 1 AND CONVERT(INT, NotNullCol) = 2");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameFunctionWrappedColumnConflictingEquality_BothMarkedDead()
    {
        var (dead, leaves) = Analyze("YEAR(NotNullCol) = 2020 AND YEAR(NotNullCol) = 2021");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void SameArithmeticWrappedColumnConflictingEquality_BothMarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol + 1 = 5 AND NotNullCol + 1 = 6");
        Assert.All(leaves, l => Assert.Contains(l, dead));
    }

    [Fact]
    public void DifferentCastTargetTypes_NeverConflated()
    {
        var (dead, _) = Analyze("CAST(NotNullCol AS INT) = 1 AND CAST(NotNullCol AS BIGINT) = 2");
        Assert.Empty(dead);
    }

    [Fact]
    public void DifferentFunctionWraps_NeverConflated()
    {
        var (dead, _) = Analyze("YEAR(NotNullCol) = 2020 AND MONTH(NotNullCol) = 12");
        Assert.Empty(dead);
    }

    [Fact]
    public void BareColumnAndCastWrappedColumn_NeverConflated()
    {
        var (dead, _) = Analyze("NotNullCol = 1 AND CAST(NotNullCol AS INT) = 2");
        Assert.Empty(dead);
    }

    [Fact]
    public void ColumnToColumnArithmetic_NeverTreatedAsWrappedColumnOperand()
    {
        var (dead, _) = Analyze("NotNullCol + NullableCol = 5 AND NotNullCol + NullableCol = 6");
        Assert.Empty(dead);
    }

    [Fact]
    public void SameCastWrappedColumnAgreeingEquality_NotDead()
    {
        Assert.Empty(Analyze("CAST(NotNullCol AS INT) = 1 AND CAST(NotNullCol AS INT) = 1").Dead);
    }

    [Fact]
    public void WiderRangeDisjunctFirst_NarrowerLaterDisjunctSubsumed_MarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol >= 3 OR NotNullCol > 5");

        Assert.Equal(2, leaves.Count);
        Assert.Contains(leaves[1], dead);
        Assert.DoesNotContain(leaves[0], dead);
    }

    [Fact]
    public void NarrowerRangeDisjunctFirst_LaterWiderDisjunctNeverMarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol > 5 OR NotNullCol >= 3");

        Assert.Equal(2, leaves.Count);
        Assert.DoesNotContain(leaves[0], dead);
        Assert.DoesNotContain(leaves[1], dead);
    }

    [Fact]
    public void DisjointRangeDisjuncts_NeitherMarkedDead()
    {
        var (dead, _) = Analyze("NotNullCol > 5 OR NotNullCol < 3");
        Assert.Empty(dead);
    }

    [Fact]
    public void DifferentColumnsAcrossOrDisjuncts_NeverConflatedForSubsumption()
    {
        var (dead, _) = Analyze("NotNullCol >= 3 OR NullableCol > 5");
        Assert.Empty(dead);
    }

    [Fact]
    public void RepeatedIdenticalRangeDisjunct_OnlyLaterOccurrenceMarkedDead()
    {
        var (dead, leaves) = Analyze("NotNullCol > 5 OR NotNullCol > 5");

        Assert.Equal(2, leaves.Count);
        Assert.DoesNotContain(leaves[0], dead);
        Assert.Contains(leaves[1], dead);
    }
}
