using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// Phase 1.1 of docs/audit-remediation-plan.md, end to end: real-world DDL almost never carries
/// an explicit per-column COLLATE, so without the manifest's declaredCollation hint reaching
/// the classifier, this exact fixture - the tool's marquee varchar-vs-nvarchar case - previously
/// resolved to UNKNOWN instead of a verdict at all.
/// </summary>
public sealed class ScanReportBuilderCollationTests
{
    private const string Sql = """
        CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);
        GO
        CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
        AS
        BEGIN
            SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
        END
        """;

    private static ScanReport Build(string? manifestDeclaredCollation)
    {
        var result = SqlScriptParser.ParseText("test.sql", Sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ScanReportBuilder.BuildFromParseResults([result], manifestDeclaredCollation);
    }

    [Fact]
    public void NoManifestCollation_NoDdlCollation_VerdictIsUnknown()
    {
        var report = Build(manifestDeclaredCollation: null);

        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }

    [Fact]
    public void ManifestCollation_SqlFamily_VerdictIsScanForcedWithManifestProvenance()
    {
        var report = Build(manifestDeclaredCollation: "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(CollationSource.DatabaseDefaultFromManifest, finding.Column.Type!.Collation!.Source);
    }

    [Fact]
    public void ManifestCollation_WindowsFamily_VerdictIsRangeSeekWithManifestProvenance()
    {
        var report = Build(manifestDeclaredCollation: "Latin1_General_CI_AS");

        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.Equal(CollationSource.DatabaseDefaultFromManifest, finding.Column.Type!.Collation!.Source);
    }
}
