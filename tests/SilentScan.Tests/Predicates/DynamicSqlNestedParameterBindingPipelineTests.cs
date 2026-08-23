using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlNestedParameterBindingPipelineTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task NestedCallBindsFormalParameterToEnclosingDeclaredParameter_TypesThroughTheBinding()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Vendors (VendorCode varchar(50) NOT NULL, INDEX IX_VendorCode (VendorCode));
            GO
            CREATE PROCEDURE dbo.usp_FindVendor @Code nvarchar(50) AS
            BEGIN
                EXEC sp_executesql N'EXEC sp_executesql N''SELECT 1 FROM dbo.Vendors WHERE VendorCode = @P'', @paramsDecl, @P = @Code', N'@Code nvarchar(50)', @Code = @Code;
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "VendorCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }

    [Fact]
    public async Task NestedCallBindsFormalParameterToNameOnlyMatch_NoEnclosingParameterOfThatName_StaysUnknown()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Vendors (VendorCode varchar(50) NOT NULL, INDEX IX_VendorCode (VendorCode));
            GO
            CREATE PROCEDURE dbo.usp_FindVendor AS
            BEGIN
                EXEC sp_executesql N'EXEC sp_executesql N''SELECT 1 FROM dbo.Vendors WHERE VendorCode = @P'', @paramsDecl, @P = @Code';
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "VendorCode");
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }
}
