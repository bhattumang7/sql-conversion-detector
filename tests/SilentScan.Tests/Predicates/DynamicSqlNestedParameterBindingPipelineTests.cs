using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for nested sp_executesql declared-parameter propagation via explicit
/// argument binding (ConstructCoverage.json's "ExecuteStatement / sp_executesql (nested
/// declared-parameter propagation)" gap, formerly verifiedBy: null): when a nested
/// sp_executesql call's own @params argument can't be resolved (here, itself a bare variable
/// reference undeclared in the reparsed fragment's fresh scope), but that same call binds one
/// of its formal parameters to a bare variable reference matching one of the ENCLOSING script's
/// own declared parameters (<c>@P = @Code</c>), DynamicSqlPipeline lets the enclosing script's
/// type for @Code stand in for @P - CLAUDE.md's "never guess" bar is met because this is an
/// explicit value hand-off at the call site, not a name match across unrelated scopes. Runs
/// through ScanReportBuilder, the same entry point production uses.
/// </summary>
public sealed class DynamicSqlNestedParameterBindingPipelineTests
{
    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("dynsql_nested_binding.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public void NestedCallBindsFormalParameterToEnclosingDeclaredParameter_TypesThroughTheBinding()
    {
        var report = Scan("""
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
    public void NestedCallBindsFormalParameterToNameOnlyMatch_NoEnclosingParameterOfThatName_StaysUnknown()
    {
        // @Code here isn't declared anywhere the outer script can see - it isn't the outer's
        // own sp_executesql parameter, so there is nothing to bind through. Guessing from the
        // name alone (rather than a genuine argument-binding hand-off) is exactly what CLAUDE.md
        // and the design note for this gap forbid.
        var report = Scan("""
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
