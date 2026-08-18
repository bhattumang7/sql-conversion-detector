using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G - end-to-end live-oracle
/// tests (standing Docker instance, disposable database, dropped unconditionally afterward) proving
/// the full wiring (catalog read -> ScanReportBuilder -> ScanReport) for the three newest streams:
/// <see cref="BareTopNoOrderByFinding"/>, <see cref="StringConcatNullFinding"/>, and
/// <see cref="AggregateDivisionColumnstoreFinding"/>. Each stream's own scanner-level fire/near-miss
/// coverage lives in its own dedicated *ScannerTests class; these tests exist to prove the report
/// plumbing (schema version bump, confidence filtering, live catalog agreement) rather than to
/// re-cover the AST logic already covered there.
/// </summary>
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

        var finding = Assert.Single(report.BareTopNoOrderByFindings);
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

        Assert.Empty(report.BareTopNoOrderByFindings);
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

        var finding = Assert.Single(report.StringConcatNullFindings);
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

        Assert.Empty(report.StringConcatNullFindings);
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

        var finding = Assert.Single(report.AggregateDivisionColumnstoreFindings);
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

        Assert.Empty(report.AggregateDivisionColumnstoreFindings);
    }
}
