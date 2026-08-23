using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DroppedConstraintPipelineTests : OracleTestFixture
{
    private const string DroppedPkSql = """
        CREATE TABLE dbo.Parts (
            PartCode varchar(30) NOT NULL,
            CONSTRAINT PK_Parts PRIMARY KEY (PartCode));
        GO
        ALTER TABLE dbo.Parts DROP CONSTRAINT PK_Parts;
        GO
        SELECT 1 FROM dbo.Parts WHERE PartCode = N'P1';
        """;

    private const string DroppedUniqueSql = """
        CREATE TABLE dbo.Users (
            Email varchar(100) NOT NULL,
            CONSTRAINT UQ_Users_Email UNIQUE (Email));
        GO
        ALTER TABLE dbo.Users DROP CONSTRAINT UQ_Users_Email;
        GO
        SELECT 1 FROM dbo.Users WHERE Email = N'a@b.com';
        """;

    private const string DroppedOneOfTwoSql = """
        CREATE TABLE dbo.Orders (
            OrderId int NOT NULL,
            OrderCode varchar(30) NOT NULL,
            CONSTRAINT PK_Orders PRIMARY KEY (OrderId));
        GO
        CREATE INDEX IX_Orders_OrderCode ON dbo.Orders (OrderCode);
        GO
        ALTER TABLE dbo.Orders DROP CONSTRAINT PK_Orders;
        GO
        SELECT 1 FROM dbo.Orders WHERE OrderId = 1;
        SELECT 1 FROM dbo.Orders WHERE OrderCode = N'A1';
        """;

    protected override string DatabaseNameSeed => nameof(DroppedConstraintPipelineTests);

    protected override string Ddl => string.Join("\nGO\n", DroppedPkSql, DroppedUniqueSql, DroppedOneOfTwoSql);

    [Fact]
    public async Task DroppedPrimaryKeyConstraint_NoLongerReportsIndexed_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(DroppedPkSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "PartCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DroppedUniqueConstraint_NoLongerReportsIndexed_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(DroppedUniqueSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Email");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DroppingOneConstraint_LeavesUnrelatedIndexesIntact()
    {

        var report = await EngineAuthoritativeScan.ScanAsync(DroppedOneOfTwoSql, "SQL_Latin1_General_CP1_CI_AS");

        var orderCodeFinding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "OrderCode");
        Assert.True(orderCodeFinding.Column.Indexed);
    }
}
