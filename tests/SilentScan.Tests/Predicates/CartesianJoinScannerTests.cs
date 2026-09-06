using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class CartesianJoinScannerTests
{
    private static IReadOnlyList<CartesianJoinFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CartesianJoinScanner.Scan(result);
    }

    [Fact]
    public void CommaJoin_NoConnectingPredicate_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.A, dbo.B;");

        var finding = Assert.Single(findings);
        Assert.Equal(CartesianJoinKind.CommaJoin, finding.Kind);
    }

    [Fact]
    public void CommaJoin_WithConnectingWherePredicate_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A a, dbo.B b WHERE a.Id = b.AId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExplicitCrossJoin_NoConnectingPredicate_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.A CROSS JOIN dbo.B;");

        var finding = Assert.Single(findings);
        Assert.Equal(CartesianJoinKind.ExplicitCrossJoin, finding.Kind);
    }

    [Fact]
    public void InnerJoinOnClause_ProvidesConnectingPredicate_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SingleTableFrom_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnqualifiedColumnReferenceInWhere_Declines()
    {

        var findings = Scan("SELECT * FROM dbo.A, dbo.B WHERE SomeFlag = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ConnectingPredicateInArithmeticExpression_StillRecognizedAsConnecting()
    {
        var findings = Scan("SELECT * FROM dbo.A a, dbo.B b WHERE a.X + b.Y = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void FiveTableCommaJoin_AllTransitivelyConnectedThroughAThirdTable_NeverFires()
    {

        var findings = Scan("""
            SELECT *
            FROM dbo.A a, dbo.B b, dbo.C c, dbo.D d, dbo.E e
            WHERE a.TypeId = b.Id
                AND b.Type = 'X'
                AND c.Id = @TripId
                AND c.OriginId = d.AddressId
                AND c.DestinationId = e.AddressId
                AND a.AgencyId = c.AgencyId;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CommaJoin_OneGenuinelyDisconnectedTableAmongConnectedOthers_FiresOnceNotPerPair()
    {
        var findings = Scan("""
            SELECT *
            FROM dbo.A a, dbo.B b, dbo.C c
            WHERE a.Id = b.AId;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(CartesianJoinKind.CommaJoin, finding.Kind);
    }

    [Fact]
    public void OnClauseOfAThirdJoinConnectsTwoOtherwiseUnrelatedTables_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A a, dbo.B b INNER JOIN dbo.C c ON b.Id = c.BId AND a.Id = c.AId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CountStarWildcardInsideNestedSubquery_NeverCrashes()
    {

        var findings = Scan("SELECT * FROM dbo.A, dbo.B WHERE (SELECT COUNT(*) FROM dbo.C) > 0;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedJoinOnOneSide_Declines()
    {

        var findings = Scan("SELECT * FROM (dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId), dbo.C c;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CrossApply_IsNotTreatedAsExplicitCrossJoin()
    {
        var findings = Scan("SELECT * FROM dbo.A a CROSS APPLY dbo.B b;");

        Assert.DoesNotContain(findings, f => f.Kind == CartesianJoinKind.ExplicitCrossJoin);
    }

    [Fact]
    public void ParenthesizedConnectingPredicate_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A a, dbo.B b WHERE (a.Id = b.AId);");

        Assert.Empty(findings);
    }

    [Fact]
    public void NegatedConnectingPredicate_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.A a, dbo.B b WHERE NOT (a.Id <> b.AId);");

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeWayExplicitInnerJoinChain_AllConnected_NeverFires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a JOIN dbo.B b ON a.Id = b.AId JOIN dbo.C c ON b.AId = c.Id;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedParenthesizedCrossJoinsOnBothSides_FiresForEachDisconnectedPair()
    {
        var findings = Scan(
            "SELECT * FROM (dbo.A CROSS JOIN dbo.B) CROSS JOIN (dbo.C CROSS JOIN dbo.D);");

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(CartesianJoinKind.ExplicitCrossJoin, f.Kind));
    }

    [Fact]
    public void QualifiedJoinWithNestedCrossJoinOnOneSide_FiresForTheNestedCrossJoin()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a JOIN (dbo.B b CROSS JOIN dbo.C c) ON a.Id = b.AId;");

        Assert.Contains(findings, f => f.Kind == CartesianJoinKind.ExplicitCrossJoin);
    }

    [Fact]
    public void TopLevelParenthesizedQualifiedJoin_NeverCrashes()
    {
        var findings = Scan(
            "SELECT * FROM (dbo.A a JOIN dbo.B b ON a.Id = b.AId);");

        Assert.Empty(findings);
    }

    [Fact]
    public void QualifiedJoinWithNestedCrossJoinOnFirstSide_FiresForTheNestedCrossJoin()
    {
        var findings = Scan(
            "SELECT * FROM (dbo.A a CROSS JOIN dbo.B b) JOIN dbo.C c ON a.Id = c.Id;");

        Assert.Contains(findings, f => f.Kind == CartesianJoinKind.ExplicitCrossJoin);
    }

    [Fact]
    public void EquiJoin_WhereClauseConstrainsBothSidesToDisjointConstants_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.X = b.Y WHERE a.X = 5 AND b.Y = 10;");

        var finding = Assert.Single(findings);
        Assert.Equal(CartesianJoinKind.JoinPredicateEmptyWithWhereClause, finding.Kind);
    }

    [Fact]
    public void EquiJoin_WhereClauseConstrainsBothSidesToDisjointRanges_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.X = b.Y WHERE a.X > 100 AND b.Y < 10;");

        Assert.Contains(findings, f => f.Kind == CartesianJoinKind.JoinPredicateEmptyWithWhereClause);
    }

    [Fact]
    public void EquiJoin_WhereClauseConstrainsBothSidesToSameConstant_NegativeControl_DoesNotFire()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.X = b.Y WHERE a.X = 5 AND b.Y = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EquiJoin_WhereClauseConstrainsOnlyOneSide_NegativeControl_DoesNotFire()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.X = b.Y WHERE a.X = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EquiJoin_NoWhereClause_NegativeControl_DoesNotFire()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.X = b.Y;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NonEquiJoin_NoColumnToColumnEquality_NegativeControl_DoesNotFire()
    {
        var findings = Scan(
            "SELECT * FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId WHERE a.X = 5 AND b.X = 10;");

        Assert.Empty(findings);
    }
}
