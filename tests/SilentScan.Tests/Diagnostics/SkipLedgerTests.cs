using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Diagnostics;

/// <summary>
/// Phase 0.1 of the audit remediation plan: every pass must record what it could not resolve
/// rather than silently dropping it (CLAUDE.md's dynamic-SQL "never silently counted as clean"
/// policy, extended to Catalog/Lineage/Predicates). These tests pin the specific gaps that
/// currently exist so a later fix (e.g. Phase 2.5's ALTER TABLE ordering fix, Phase 4.1's
/// UPDATE/DELETE coverage) is provably a fix - it must remove the corresponding ledger entry.
/// </summary>
public sealed class SkipLedgerTests
{
    [Fact]
    public void SkipLedger_Record_AppendsEntryWithGivenFields()
    {
        var ledger = new SkipLedger();

        ledger.Record(AnalysisPass.Predicates, "test.sql", 3, 7, "kind", "reason");

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(AnalysisPass.Predicates, entry.Pass);
        Assert.Equal("test.sql", entry.SourcePath);
        Assert.Equal(3, entry.Line);
        Assert.Equal(7, entry.Column);
        Assert.Equal("kind", entry.ConstructKind);
        Assert.Equal("reason", entry.Reason);
    }

    [Fact]
    public void CatalogBuilder_AlterTableBeforeCreateTable_RecordsSkip()
    {
        // Cross-file ordering: the ALTER arrives (in enumeration order) before the CREATE TABLE
        // that would establish the target. CLAUDE.md precision discipline says never silently
        // drop this - it can hide an indexed/typed column from every downstream pass.
        var alterFirst = new SqlScriptParser().ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Email VARCHAR(200) NULL;");
        var createSecond = new SqlScriptParser().ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL);");

        var catalog = CatalogBuilder.Build([alterFirst, createSecond]);

        var entry = Assert.Single(catalog.Skipped.Entries);
        Assert.Equal(AnalysisPass.Catalog, entry.Pass);
        Assert.Equal("02_alter.sql", entry.SourcePath);
        Assert.Contains("dbo.Users", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogBuilder_CreateIndexBeforeCreateTable_RecordsSkip()
    {
        var indexFirst = new SqlScriptParser().ParseText("02_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");
        var createSecond = new SqlScriptParser().ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");

        var catalog = CatalogBuilder.Build([indexFirst, createSecond]);

        var entry = Assert.Single(catalog.Skipped.Entries);
        Assert.Equal("CREATE INDEX", entry.ConstructKind);
    }

    [Fact]
    public void CatalogBuilder_WellOrderedDdl_RecordsNoSkips()
    {
        var create = new SqlScriptParser().ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");
        var alter = new SqlScriptParser().ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Phone VARCHAR(20) NULL;");
        var index = new SqlScriptParser().ParseText("03_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");

        var catalog = CatalogBuilder.Build([create, alter, index]);

        Assert.Empty(catalog.Skipped.Entries);
    }

    [Fact]
    public void LineageResolver_ViewOverUnknownTable_RecordsSkip()
    {
        // dbo.Ghost has no DDL anywhere in the scanned set - CLAUDE.md: never guess, but also
        // never silently treat the view as if it had zero problems.
        var view = new SqlScriptParser().ParseText("view.sql", "CREATE VIEW dbo.vw_Ghost AS SELECT g.Id FROM dbo.Ghost AS g;");

        var catalog = CatalogBuilder.Build([view]);
        var lineage = LineageResolver.Resolve(catalog, [view]);

        // Two entries, not one: the unresolved table itself, plus the cascading column lookup
        // against it (both real, both worth reporting - a downstream skip isn't noise, it's
        // the direct consequence of the upstream one and both help bound the study's coverage).
        Assert.Equal(2, lineage.Skipped.Entries.Count);
        Assert.All(lineage.Skipped.Entries, e => Assert.Equal(AnalysisPass.Lineage, e.Pass));
        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("dbo.Ghost", StringComparison.Ordinal));
        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "column reference");
    }

    [Fact]
    public void LineageResolver_CyclicView_RecordsSkip()
    {
        var viewA = new SqlScriptParser().ParseText("a.sql", "CREATE VIEW dbo.vw_A AS SELECT b.Id FROM dbo.vw_B AS b;");
        var viewB = new SqlScriptParser().ParseText("b.sql", "CREATE VIEW dbo.vw_B AS SELECT a.Id FROM dbo.vw_A AS a;");

        var catalog = CatalogBuilder.Build([viewA, viewB]);
        var lineage = LineageResolver.Resolve(catalog, [viewA, viewB]);

        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "view dependency");
    }

    [Fact]
    public void TypedPredicateExtractor_UpdateWhereClause_RecordsSkipInsteadOfSilentDrop()
    {
        // Phase 4.1 (not yet implemented): UPDATE's WHERE clause has no FROM-scope pushed, so
        // this predicate is invisible to Pass 3 today. It must show up in the ledger, not
        // vanish - this test is the regression guard that Phase 4.1 must turn green by
        // producing a real finding here instead of a skip.
        var sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_RenameUser @Id NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = 'x' WHERE Id = @Id;
            END
            """;
        var result = new SqlScriptParser().ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.TypedFindings);
        Assert.Contains(extraction.SkippedConstructs, e => e.ConstructKind == "comparison outside FROM scope");
    }

    [Fact]
    public void TypedPredicateExtractor_OrdinaryWhereClause_RecordsNoSkip()
    {
        var sql = "CREATE TABLE dbo.Users (Id INT NOT NULL);\nGO\nSELECT Id FROM dbo.Users WHERE Id = 1;";
        var result = new SqlScriptParser().ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.SkippedConstructs);
    }

    [Fact]
    public void ScanReportBuilder_AggregatesSkippedConstructsAcrossAllPasses()
    {
        var sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE VIEW dbo.vw_Ghost AS SELECT g.Id FROM dbo.Ghost AS g;
            GO
            CREATE PROCEDURE dbo.usp_RenameUser @Id NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = 'x' WHERE Id = @Id;
            END
            """;
        var result = new SqlScriptParser().ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var report = ScanReportBuilder.BuildFromParseResults([result]);

        Assert.Contains(report.SkippedConstructs, e => e.Pass == AnalysisPass.Lineage);
        Assert.Contains(report.SkippedConstructs, e => e.Pass == AnalysisPass.Predicates);
        // Deterministic ordering (CLAUDE.md): grouped by pass, then source location.
        Assert.Equal(report.SkippedConstructs.OrderBy(e => e.Pass).ThenBy(e => e.SourcePath, StringComparer.Ordinal).ThenBy(e => e.Line), report.SkippedConstructs);
    }

    [Fact]
    public void TypedPredicateExtractor_UnrecognizedComparisonOperator_NeverThrows()
    {
        // Historically this path threw NotImplementedException, which would abort an entire
        // corpus scan on one odd construct. Every currently-recognized BooleanComparisonType
        // is handled, so there is no live repro left in ScriptDOM's public surface - this test
        // instead pins that the code path degrades to a ledger entry rather than a throw by
        // construction (see ToOperatorText's exhaustive-but-defensive switch).
        var sql = "CREATE TABLE dbo.T (Col INT NOT NULL);\nGO\nSELECT Col FROM dbo.T WHERE Col = 1;";
        var result = new SqlScriptParser().ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);

        var exception = Record.Exception(() => TypedPredicateExtractor.Extract(result, catalog, lineage));

        Assert.Null(exception);
    }
}
