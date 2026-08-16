using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
/// live-mode only (<see cref="DatabaseCatalog.IsIndexedView"/>/<see
/// cref="DatabaseCatalog.TryGetModuleUsesQuotedIdentifier"/> are only ever populated by
/// <c>LiveCatalogReader</c>/<c>LiveScanRunner</c>), so these tests build the catalog directly and
/// parse each fixture with its SOURCE PATH set to the module's own qualified name - exactly what
/// <c>LiveScanRunner</c>'s own <c>parseResultSource()</c> does (<c>SqlScriptParser.ParseText(m.QualifiedName, ...)</c>) -
/// so <c>SetOptionScanner.Scan</c>'s <c>moduleQualifiedName = parseResult.SourcePath</c>
/// convention lines up the same way it would against a real live scan.
///
/// QUOTED_IDENTIFIER OFF, ANSI_NULLS OFF, NUMERIC_ROUNDABORT ON, ANSI_WARNINGS OFF, and
/// CONCAT_NULL_YIELDS_NULL OFF are covered - ARITHABORT OFF was investigated and dropped,
/// oracle-falsified: see <see cref="SetOptionFinding"/>'s own doc comment.
/// </summary>
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
        // SET NUMERIC_ROUNDABORT, ANSI_NULLS ON is legal T-SQL - a comma-separated option list
        // sharing one IsOn - so this must fire on the NumericRoundAbort bit alone, not require it
        // to be the only option named.
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
        // Transitive containment through a view layer, resolved for free from the already-
        // resolved LineageCatalog (ModuleReachableObjectWalker's own documented mechanism) -
        // no re-parsing of the view's own body needed.
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
        // File-mode / a module this scan never read sys.sql_modules for - no flag registered at
        // all. Must never be treated as "therefore OFF".
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT Id FROM dbo.Orders; END", catalog, usesQuotedIdentifier: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoSetStatementAndQuotedIdentifierOn_NeverFires()
    {
        // Nothing this module's own text/catalog flag could ever trigger a finding for - the
        // (relatively expensive) reachable-object walk is skipped entirely rather than run and
        // discarded (SetOptionScanner's own short-circuit).
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
    public void SingleStatementSettingMultipleOptionsOfTheSameState_FiresBothKinds()
    {
        // SET ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL OFF - a comma-separated option list sharing
        // one IsOn (T-SQL cannot mix ON and OFF within a single SET statement) legitimately
        // triggers two distinct kinds from the same PredicateSetStatement node.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(FilteredIndexTable("dbo", "Orders"));

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN SET ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL OFF; SELECT Id FROM dbo.Orders; END", catalog);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature);
        Assert.Contains(findings, f => f.Kind == SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature);
    }
}
