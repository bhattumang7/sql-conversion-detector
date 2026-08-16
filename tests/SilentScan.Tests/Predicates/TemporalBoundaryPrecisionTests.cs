using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream": BETWEEN
/// end-of-period boundary - a CORRECTNESS finding (silently dropped rows), not a sargability one
/// (BETWEEN itself seeks fine here). Needs a real catalog (the column's own declared
/// TIME/DATETIME2/DATETIMEOFFSET scale is a DDL fact) - unlike most of this file's other tests,
/// these use the full CatalogBuilder/LineageResolver pipeline directly rather than the
/// catalog-less ScanFixture helper.
/// </summary>
public sealed class TemporalBoundaryPrecisionTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static IReadOnlyList<TemporalBoundaryPrecisionFinding> ScanFixture(string fileName)
    {
        var sql = File.ReadAllText(Path.Combine(FixturesDir, fileName));
        var parseResult = SqlScriptParser.ParseText(fileName, sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        return NonSargablePredicateScanner.ScanFull(parseResult, catalog, lineage).TemporalBoundaryFindings;
    }

    [Fact]
    public void ThreeDigitBoundaryAgainstDateTime2Scale7_Fires()
    {
        var findings = ScanFixture("TEMPORAL_BOUNDARY_PRECISION_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Events", finding.TableQualifiedName);
        Assert.Equal("OccurredAt", finding.ColumnName);
        Assert.Equal(7, finding.ColumnScale);
        Assert.Equal(3, finding.BoundaryLiteralFractionalDigits);
        Assert.Equal("2024-12-31 23:59:59.997", finding.BoundaryLiteralText);
    }

    [Fact]
    public void RangeComparisonInsteadOfBetween_NeverFires()
    {
        var findings = ScanFixture("TEMPORAL_BOUNDARY_PRECISION_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void BoundaryMatchesColumnScaleExactly_NeverFires()
    {
        var findings = ScanFixture("TEMPORAL_BOUNDARY_PRECISION_matching_scale_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void BareDateBoundaryWithNoTimePortion_FiresWithZeroFractionalDigits()
    {
        // The classic, even more common version of this bug: a bare date with NO time
        // component implies midnight, which is even less precise than any explicit fractional
        // literal - zero fractional digits is correctly "less than" any positive column scale.
        const string sql = """
            CREATE TABLE dbo.Sessions (Id INT NOT NULL PRIMARY KEY, StartedAt DATETIME2(3) NOT NULL);
            SELECT Id FROM dbo.Sessions WHERE StartedAt BETWEEN '2024-01-01' AND '2024-06-30';
            """;
        var parseResult = SqlScriptParser.ParseText("bare_date.sql", sql);
        Assert.False(parseResult.HasErrors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.ScanFull(parseResult, catalog, lineage).TemporalBoundaryFindings;

        var finding = Assert.Single(findings);
        Assert.Equal(0, finding.BoundaryLiteralFractionalDigits);
        Assert.Equal(3, finding.ColumnScale);
    }

    [Fact]
    public void OrdinaryDateTimeColumn_NoDeclaredScale_NeverGuesses()
    {
        // Plain DATETIME has no fractional-seconds precision concept the way DATETIME2 does
        // (its own rounding behavior is a different, unrelated legacy quirk) - never guesses a
        // scale for a category this rule doesn't model.
        const string sql = """
            CREATE TABLE dbo.Legacy (Id INT NOT NULL PRIMARY KEY, OccurredAt DATETIME NOT NULL);
            SELECT Id FROM dbo.Legacy WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997';
            """;
        var parseResult = SqlScriptParser.ParseText("legacy.sql", sql);
        Assert.False(parseResult.HasErrors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.ScanFull(parseResult, catalog, lineage).TemporalBoundaryFindings;

        Assert.Empty(findings);
    }
}
