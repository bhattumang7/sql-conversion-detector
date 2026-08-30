using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlParameterAliasPipelineTests
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
    public async Task AliasTypedDeclaredParameter_ResolvesThroughCatalogTypeAliases_ScanForced()
    {
        var report = await Scan("""
            CREATE TYPE dbo.CodeType FROM nvarchar(50);
            GO
            CREATE TABLE dbo.Vendors (VendorCode varchar(50) NOT NULL, INDEX IX_VendorCode (VendorCode));
            GO
            CREATE PROCEDURE dbo.usp_FindVendor @Code dbo.CodeType AS
            BEGIN
                EXEC sp_executesql N'SELECT 1 FROM dbo.Vendors WHERE VendorCode = @P', N'@P dbo.CodeType', @P = @Code;
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "VendorCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }
}
