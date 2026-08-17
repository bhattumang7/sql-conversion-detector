using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": true cartesian join. Pure
/// relational algebra - no oracle needed, the same "structural/architectural fact" reasoning
/// <see cref="CompositeIndexLeadingColumnScannerTests"/> already documents for its own rule.
/// </summary>
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
        // Precision guard: an unqualified column can't be conservatively attributed to a side
        // without a catalog column lookup this pass doesn't perform - the whole statement
        // declines rather than risking a mis-attributed false positive.
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
        // Regression: an earlier version checked connectivity pairwise ("does any single leaf
        // predicate mention BOTH of these two specific aliases") instead of as a graph, so it
        // false-positived on this exact real shape from the local test database - b and c never
        // appear together in one predicate, but are transitively joined through a (b-a, a-c).
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

        // C is disconnected from both A and B - a real defect, but reported once (a witness
        // pair), not once per (A,C) and (B,C) combination.
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
        // Regression: COUNT(*) produces a ColumnReferenceExpression with ColumnType.Wildcard and
        // a null MultiPartIdentifier entirely (not merely unqualified, a null reference the naive
        // "Identifiers.Count < 2" check crashes on) - found only against the real corpus, not by
        // a synthetic fixture, the same class of bug the composite-index/hint-validity streams
        // also hit against real production code. The wildcard is treated the same as an
        // unqualified reference by the existing precision guard, so the whole statement declines
        // rather than firing - conservative, not a missed detection this test is asserting on.
        var findings = Scan("SELECT * FROM dbo.A, dbo.B WHERE (SELECT COUNT(*) FROM dbo.C) > 0;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedJoinOnOneSide_Declines()
    {
        // Known v1 scope limit: only a plain table-vs-table gap is analyzed.
        var findings = Scan("SELECT * FROM (dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId), dbo.C c;");

        Assert.Empty(findings);
    }
}
