using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class SetOptionScannerTests
{
    private const string ModuleName = "dbo.usp_Test";

    private static CatalogTable FilteredIndexTable(string schema, string name) =>
        new(schema, name, CatalogTableKind.Table,
            [new CatalogColumn("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [new CatalogIndex("IX_Filtered", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Id"], IncludedColumns: [], IsFiltered: true)],
            SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogTable PlainTable(string schema, string name) =>
        new(schema, name, CatalogTableKind.Table,
            [new CatalogColumn("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static IReadOnlyList<SetOptionFinding> Scan(string sql, DatabaseCatalog catalog, bool? usesQuotedIdentifier = true, bool? usesAnsiNulls = true)
    {
        var result = SqlScriptParser.ParseText(ModuleName, sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        if (usesQuotedIdentifier is { } uqi)
        {
            catalog.AddModuleUsesQuotedIdentifier(ModuleName, uqi);
        }

        if (usesAnsiNulls is { } uan)
        {
            catalog.AddModuleUsesAnsiNulls(ModuleName, uan);
        }

        var lineage = LineageResolver.Resolve(catalog, [result]);
        return SetOptionScanner.Scan(result, catalog, lineage);
    }

    [Fact]
    public void NumericRoundabortOn_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT ON; SELECT Id FROM dbo.Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature, finding.Kind);
        Assert.Equal("dbo.Orders", finding.TouchedObjectQualifiedName);
        Assert.Equal("IX_Filtered", finding.TouchedIndexName);
        Assert.False(finding.TouchedIsIndexedView);
    }

    [Fact]
    public void NumericRoundabortOn_ModuleTouchesNothingFilteredOrIndexed_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(PlainTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT ON; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void NumericRoundabortOff_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT OFF; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void NumericRoundabortOnInCommaSeparatedOptionList_StillFires()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT, ANSI_NULLS ON; SELECT Id FROM dbo.Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature, finding.Kind);
    }

    [Fact]
    public void NumericRoundabortOn_ModuleTouchesIndexedView_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(PlainTable("dbo", "Orders"));
        catalog.AddIndexedView("dbo.vw_Orders", [new CatalogIndex("IX_vw_Orders", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Id"], IncludedColumns: [])]);

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT ON; SELECT Id FROM dbo.vw_Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature, finding.Kind);
        Assert.Equal("dbo.vw_Orders", finding.TouchedObjectQualifiedName);
        Assert.True(finding.TouchedIsIndexedView);
    }

    [Fact]
    public void NumericRoundabortOn_TouchesFilteredIndexTableOnlyThroughAReferencedView_StillFires()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var result = SqlScriptParser.ParseText(ModuleName,
            """
            CREATE VIEW dbo.vw_Orders AS SELECT Id FROM dbo.Orders;
            GO
            CREATE PROCEDURE dbo.usp_Test AS BEGIN SET NUMERIC_ROUNDABORT ON; SELECT Id FROM dbo.vw_Orders; END
            """);
        Assert.False(result.HasErrors);

        catalog.AddModuleUsesQuotedIdentifier(ModuleName, true);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var findings = SetOptionScanner.Scan(result, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TouchedObjectQualifiedName);
        Assert.False(finding.TouchedIsIndexedView);
    }

    [Fact]
    public void QuotedIdentifierOff_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesQuotedIdentifier: false);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature, finding.Kind);
        Assert.Equal("dbo.Orders", finding.TouchedObjectQualifiedName);
    }

    [Fact]
    public void QuotedIdentifierOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesQuotedIdentifier: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void QuotedIdentifierFlagUnknown_NeverGuesses()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesQuotedIdentifier: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoSetStatementAndQuotedIdentifierOn_NeverFires()
    {

        var catalog = new DatabaseCatalog();

        var findings = Scan("CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT 1; END", catalog, usesQuotedIdentifier: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnsiNullsOff_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesAnsiNulls: false);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, finding.Kind);
        Assert.Equal("dbo.Orders", finding.TouchedObjectQualifiedName);
    }

    [Fact]
    public void AnsiNullsOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesAnsiNulls: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnsiNullsFlagUnknown_NeverGuesses()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesAnsiNulls: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnsiWarningsOff_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_WARNINGS OFF; SELECT Id FROM dbo.Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature, finding.Kind);
    }

    [Fact]
    public void AnsiWarningsOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_WARNINGS ON; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void ConcatNullYieldsNullOff_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET CONCAT_NULL_YIELDS_NULL OFF; SELECT Id FROM dbo.Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature, finding.Kind);
    }

    [Fact]
    public void ConcatNullYieldsNullOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET CONCAT_NULL_YIELDS_NULL ON; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnsiPaddingOff_ModuleTouchesFilteredIndexTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_PADDING OFF; SELECT Id FROM dbo.Orders; END", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature, finding.Kind);
    }

    [Fact]
    public void AnsiPaddingOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_PADDING ON; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void SingleStatementSettingMultipleOptionsOfTheSameState_FiresBothKinds()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL OFF; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature);
        Assert.Contains(findings, f => f.Kind == SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature);
    }
}
