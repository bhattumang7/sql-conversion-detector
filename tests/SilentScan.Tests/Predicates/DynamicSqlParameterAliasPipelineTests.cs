using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for sp_executesql @params type-alias resolution (formerly pinned in
/// KnownGapCharacterizationTests.DynamicSql_AliasTypedDeclaredParameter_ResolvesToNullType_Unknown):
/// DynamicSqlScanner runs before CatalogBuilder in ScanReportBuilder's pipeline, so parsing
/// sp_executesql's @params declaration at scan time - the original design - could never resolve
/// a user CREATE TYPE ... FROM alias, since no DatabaseCatalog existed yet. DynamicSqlScript now
/// carries the raw declaration text (ParameterDeclarationText) forward, and DynamicSqlPipeline
/// parses it with DynamicSqlParameterDeclarations.TryParse(text, catalog.TypeAliases) once the
/// real catalog exists. Runs through ScanReportBuilder, the same entry point production uses.
/// </summary>
public sealed class DynamicSqlParameterAliasPipelineTests
{
    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("dynsql_alias.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public void AliasTypedDeclaredParameter_ResolvesThroughCatalogTypeAliases_ScanForced()
    {
        var report = Scan("""
            CREATE TYPE dbo.CodeType FROM nvarchar(50);
            GO
            CREATE TABLE dbo.Vendors (VendorCode varchar(50) NOT NULL, INDEX IX_VendorCode (VendorCode));
            GO
            CREATE PROCEDURE dbo.usp_FindVendor @Code dbo.CodeType AS
            BEGIN
                EXEC sp_executesql N'SELECT 1 FROM dbo.Vendors WHERE VendorCode = @P', N'@P dbo.CodeType', @P = @Code;
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "VendorCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }
}
