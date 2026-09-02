using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ExecuteAtLargeObjectParameterScannerTests
{
    private static IReadOnlyList<ExecuteAtLargeObjectParameterFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ExecuteAtLargeObjectParameterScanner.Scan(result, catalog);
    }

    [Fact]
    public void NVarCharMaxParameter_AtLinkedServer_Fires()
    {
        var findings = Scan("""
            DECLARE @p NVARCHAR(MAX) = N'hello';
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ExecuteAtLargeObjectParameterFindingKind.CrashesSession, finding.Kind);
        Assert.Equal("p", finding.VariableName);
    }

    [Fact]
    public void VarCharMaxParameter_Fires()
    {
        var findings = Scan("""
            DECLARE @p VARCHAR(MAX) = 'hello';
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void VarBinaryMaxParameter_Fires()
    {
        var findings = Scan("""
            DECLARE @p VARBINARY(MAX) = 0x01;
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void XmlParameter_Fires()
    {
        var findings = Scan("""
            DECLARE @p XML = '<a/>';
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ExecuteAtLargeObjectParameterFindingKind.XmlRejected, finding.Kind);
    }

    [Fact]
    public void FixedLengthNVarCharParameter_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @p NVARCHAR(100) = N'hello';
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void IntParameter_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @p INT = 5;
            EXEC ('SELECT 1', @p) AT MyLinkedServer;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MaxTypedCommandTextOnly_NoOtherParameters_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT 1';
            EXEC (@sql) AT MyLinkedServer;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExecuteWithoutAtClause_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @p NVARCHAR(MAX) = N'hello';
            EXEC ('SELECT 1', @p);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MaxTypedProcedureParameter_AtLinkedServer_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo @p NVARCHAR(MAX) AS
            BEGIN
                EXEC ('SELECT 1', @p) AT MyLinkedServer;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("p", finding.VariableName);
    }
}
