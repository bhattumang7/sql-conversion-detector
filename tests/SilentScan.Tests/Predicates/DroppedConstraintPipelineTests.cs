using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the dropped-constraint precision bug (formerly pinned in
/// KnownGapCharacterizationTests.DroppedPrimaryKeyConstraint_BackingIndexIsStillReported):
/// dropping a named PK/UNIQUE constraint must remove its backing index from the catalog, since
/// the index the constraint created no longer exists on the real table. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses.
/// </summary>
public sealed class DroppedConstraintPipelineTests
{
    [Fact]
    public void DroppedPrimaryKeyConstraint_NoLongerReportsIndexed()
    {
        var parseResult = SqlScriptParser.ParseText("dropped_pk.sql", """
            CREATE TABLE dbo.Parts (
                PartCode varchar(30) NOT NULL,
                CONSTRAINT PK_Parts PRIMARY KEY (PartCode));
            GO
            ALTER TABLE dbo.Parts DROP CONSTRAINT PK_Parts;
            GO
            SELECT 1 FROM dbo.Parts WHERE PartCode = N'P1';
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "PartCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void DroppedUniqueConstraint_NoLongerReportsIndexed()
    {
        var parseResult = SqlScriptParser.ParseText("dropped_unique.sql", """
            CREATE TABLE dbo.Users (
                Email varchar(100) NOT NULL,
                CONSTRAINT UQ_Users_Email UNIQUE (Email));
            GO
            ALTER TABLE dbo.Users DROP CONSTRAINT UQ_Users_Email;
            GO
            SELECT 1 FROM dbo.Users WHERE Email = N'a@b.com';
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Email");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void DroppingOneConstraint_LeavesUnrelatedIndexesIntact()
    {
        // A named constraint drop must remove ONLY its own backing index, not every index on
        // the table - the fix matches by name, not by clearing the whole index list.
        var parseResult = SqlScriptParser.ParseText("dropped_one_of_two.sql", """
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
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var orderCodeFinding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "OrderCode");
        Assert.True(orderCodeFinding.Column.Indexed);
    }
}
