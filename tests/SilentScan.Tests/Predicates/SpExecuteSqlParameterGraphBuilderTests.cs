using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class SpExecuteSqlParameterGraphBuilderTests
{
    private static (ProcCallGraph Graph, SkipLedger Ledger) BuildFrom(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var ledger = new SkipLedger();
        return (ProcCallGraphBuilder.Build([result], catalog, ledger), ledger);
    }

    [Fact]
    public void Build_LiteralParameterDefinitions_ResolvesDeclaredTypeAndCallerType()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @sql NVARCHAR(MAX) = N'UPDATE dbo.T SET Sku = @SkuCode';
                DECLARE @sku VARCHAR(20) = 'WIDGET-2024-CLEARANCE';
                EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @SkuCode = @sku;
            """);

        var callSite = Assert.Single(graph.SpExecuteSqlCallSites);
        Assert.Equal("dbo.Caller", callSite.CallerScopeQualifiedName);
        var binding = Assert.Single(callSite.Bindings);
        Assert.Equal("@SkuCode", binding.ParameterName);
        Assert.Equal(SqlTypeCategory.VarChar, binding.DeclaredType!.Category);
        Assert.Equal(10, binding.DeclaredType.Length);
        Assert.Equal(SqlTypeCategory.VarChar, binding.CallerArgumentType!.Category);
        Assert.Equal(20, binding.CallerArgumentType.Length);
        Assert.Equal("@sku", binding.CallerVariableName);
        Assert.True(binding.CallerVariableWasAssignedBeforeCall);
        Assert.False(binding.DeclaredIsOutput);
    }

    [Fact]
    public void Build_ParameterDefinitionsBuiltFromVariable_RecordsSkippedAndNoCallSite()
    {
        var (graph, ledger) = BuildFrom("""
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @sql NVARCHAR(MAX) = N'UPDATE dbo.T SET Sku = @SkuCode';
                DECLARE @paramList NVARCHAR(100) = N'@SkuCode VARCHAR(10)';
                DECLARE @sku VARCHAR(20) = 'WIDGET-2024-CLEARANCE';
                EXEC sp_executesql @sql, @paramList, @SkuCode = @sku;
            """);

        Assert.Empty(graph.SpExecuteSqlCallSites);
        Assert.Contains(ledger.Entries, e => e.Reason.Contains("not a literal string", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_OutputParameter_CarriesOutputFlagAndCallSiteKeyword()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
                DECLARE @tax DECIMAL(4,1);
                EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax OUTPUT;
            """);

        var callSite = Assert.Single(graph.SpExecuteSqlCallSites);
        var binding = Assert.Single(callSite.Bindings);
        Assert.True(binding.DeclaredIsOutput);
        Assert.True(binding.CallSiteHasOutputKeyword);
        Assert.Equal("@tax", binding.CallerVariableName);
        Assert.Equal(4, binding.DeclaredType!.Scale);
        Assert.Equal(1, binding.CallerArgumentType!.Scale);
    }

    [Fact]
    public void Build_ActualBindingNameNotInDeclaredParameterList_IsSkippedNotCrash()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1';
                DECLARE @sku VARCHAR(20) = 'WIDGET';
                EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @NotDeclared = @sku;
            """);

        Assert.Empty(graph.SpExecuteSqlCallSites);
    }

    [Fact]
    public void Build_MatchingDeclaredAndCallerTypes_StillRecordsBindingForScannerToClear()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1';
                DECLARE @sku VARCHAR(10) = 'WIDGET';
                EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @SkuCode = @sku;
            """);

        var callSite = Assert.Single(graph.SpExecuteSqlCallSites);
        var binding = Assert.Single(callSite.Bindings);
        Assert.Equal(10, binding.DeclaredType!.Length);
        Assert.Equal(10, binding.CallerArgumentType!.Length);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(graph);
        Assert.Empty(findings);
    }
}
