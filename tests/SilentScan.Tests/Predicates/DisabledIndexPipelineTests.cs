using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the disabled-index precision bug (formerly pinned in
/// KnownGapCharacterizationTests.DisabledIndex_IsStillReportedAsIndexed): a column behind an
/// index that has been <c>ALTER INDEX ... DISABLE</c>d must never report Indexed = true, since
/// the engine cannot actually use that index to seek. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses, so it exercises the
/// full catalog -> lineage -> predicate pipeline, not CatalogBuilder in isolation.
/// </summary>
public sealed class DisabledIndexPipelineTests
{
    [Fact]
    public void DisabledIndex_NoLongerReportsIndexed()
    {
        var parseResult = SqlScriptParser.ParseText("disabled_index.sql", """
            CREATE TABLE dbo.Devices (SerialNo varchar(50) NOT NULL);
            GO
            CREATE INDEX IX_SerialNo ON dbo.Devices (SerialNo);
            GO
            ALTER INDEX IX_SerialNo ON dbo.Devices DISABLE;
            GO
            SELECT 1 FROM dbo.Devices WHERE SerialNo = N'ABC';
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "SerialNo");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void RebuiltIndex_ReportsIndexedAgain()
    {
        var parseResult = SqlScriptParser.ParseText("rebuilt_index.sql", """
            CREATE TABLE dbo.Devices (SerialNo varchar(50) NOT NULL);
            GO
            CREATE INDEX IX_SerialNo ON dbo.Devices (SerialNo);
            GO
            ALTER INDEX IX_SerialNo ON dbo.Devices DISABLE;
            GO
            ALTER INDEX IX_SerialNo ON dbo.Devices REBUILD;
            GO
            SELECT 1 FROM dbo.Devices WHERE SerialNo = N'ABC';
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "SerialNo");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }
}
