using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class SecondSweepGLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_BareTopNoOrderBy_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.BareTopTarget (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(50) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_BareTopTarget_Find AS
            BEGIN
                SELECT TOP (5) Id, Name FROM dbo.BareTopTarget;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<BareTopNoOrderByFinding>("BareTopNoOrderByScanner"));
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_TopWithOrderBy_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.BareTopClean (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(50) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_BareTopClean_Find AS
            BEGIN
                SELECT TOP (5) Id, Name FROM dbo.BareTopClean ORDER BY Id;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BareTopNoOrderByFinding>("BareTopNoOrderByScanner"));
    }

    [Fact]
    public async Task LiveDeployment_BareTopHundredPointZeroPercent_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.BareTopDecimalHundred (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(50) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_BareTopDecimalHundred_Find AS
            BEGIN
                SELECT TOP (100.0) PERCENT Id, Name FROM dbo.BareTopDecimalHundred;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<BareTopNoOrderByFinding>("BareTopNoOrderByScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ViewTopHundredPointZeroPercentOrderBy_FiresAsNeverLimits()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.ViewOrderingDecimalHundred (Id INT NOT NULL PRIMARY KEY, Amt INT NOT NULL);
            GO
            CREATE VIEW dbo.v_ViewOrderingDecimalHundred AS
            SELECT TOP (100.0) PERCENT Id, Amt FROM dbo.ViewOrderingDecimalHundred ORDER BY Amt DESC;
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<ViewOrderingFinding>("ViewOrderingScanner"));
        Assert.Equal(ViewOrderingFindingKind.TopPercentOrderByNeverLimits, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_StringConcatNull_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.ConcatTarget (Id INT NOT NULL PRIMARY KEY, FirstName VARCHAR(50) NOT NULL, MiddleName VARCHAR(50) NULL);
            GO
            CREATE PROCEDURE dbo.usp_ConcatTarget_Find AS
            BEGIN
                SELECT FirstName + ' ' + MiddleName FROM dbo.ConcatTarget;
            END
            """);

        var finding = Assert.Single(report.Find<StringConcatNullFinding>("StringConcatNullScanner"));
        Assert.Equal("dbo.ConcatTarget", finding.TableQualifiedName);
        Assert.Equal("MiddleName", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_StringConcatGuardedByIsNull_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.ConcatClean (Id INT NOT NULL PRIMARY KEY, FirstName VARCHAR(50) NOT NULL, MiddleName VARCHAR(50) NULL);
            GO
            CREATE PROCEDURE dbo.usp_ConcatClean_Find AS
            BEGIN
                SELECT FirstName + ' ' + ISNULL(MiddleName, '') FROM dbo.ConcatClean;
            END
            """);

        Assert.Empty(report.Find<StringConcatNullFinding>("StringConcatNullScanner"));
    }

    [Fact]
    public async Task LiveDeployment_AggregateDivisionOnColumnstoreTable_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.RatioTarget (Id INT NOT NULL PRIMARY KEY, Num INT NOT NULL, Denom INT NOT NULL);
            GO
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_RatioTarget ON dbo.RatioTarget (Id, Num, Denom);
            GO
            CREATE PROCEDURE dbo.usp_RatioTarget_Find AS
            BEGIN
                SELECT SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.RatioTarget;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<AggregateDivisionColumnstoreFinding>("AggregateDivisionColumnstoreScanner"));
        Assert.Equal("dbo.RatioTarget", finding.TableQualifiedName);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_AggregateDivisionOnRowstoreTable_NoColumnstoreIndex_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.RatioClean (Id INT NOT NULL PRIMARY KEY, Num INT NOT NULL, Denom INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_RatioClean_Find AS
            BEGIN
                SELECT SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.RatioClean;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<AggregateDivisionColumnstoreFinding>("AggregateDivisionColumnstoreScanner"));
    }
}
