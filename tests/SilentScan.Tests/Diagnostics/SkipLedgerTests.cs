using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Diagnostics;

/// <summary>
/// Phase 0.1 of the audit remediation plan: every pass must record what it could not resolve
/// rather than silently dropping it (CLAUDE.md's dynamic-SQL "never silently counted as clean"
/// policy, extended to Catalog/Lineage/Predicates). Some of these tests originally pinned gaps
/// that a later phase then closed (e.g. Phase 2.5's two-phase build fixed ALTER TABLE cross-file
/// ordering) - those were updated in place to assert the fix instead of the gap, proving the
/// ledger entry is gone, not just that the code compiles.
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
    public void CatalogBuilder_AlterTableBeforeCreateTable_TwoPhaseBuildResolvesRegardlessOfOrder()
    {
        // Cross-file ordering (docs/audit-remediation-plan.md Phase 2.5): the ALTER arrives (in
        // enumeration order) before the CREATE TABLE that establishes the target. The two-phase
        // build (every CREATE TABLE across every file first, then everything else) resolves this
        // correctly regardless of file order - this used to be a recorded skip (see git history
        // for the Phase 0.1 version of this test); now it's a real fix; no skip, real data.
        var alterFirst = SqlScriptParser.ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Email VARCHAR(200) NULL;");
        var createSecond = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL);");

        var catalog = CatalogBuilder.Build([alterFirst, createSecond]);

        Assert.Empty(catalog.Skipped.Entries);
        Assert.NotNull(catalog.Find("dbo.Users")!.FindColumn("Email"));
    }

    [Fact]
    public void CatalogBuilder_CreateIndexBeforeCreateTable_TwoPhaseBuildResolvesRegardlessOfOrder()
    {
        var indexFirst = SqlScriptParser.ParseText("02_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");
        var createSecond = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");

        var catalog = CatalogBuilder.Build([indexFirst, createSecond]);

        Assert.Empty(catalog.Skipped.Entries);
        Assert.True(catalog.Find("dbo.Users")!.IsIndexedColumn("Email"));
    }

    [Fact]
    public void CatalogBuilder_AlterOrIndexWithNoMatchingTableAnywhere_StillRecordsSkip()
    {
        // The two-phase build fixes ordering, not a genuinely missing base table - this must
        // still be recorded, not silently dropped.
        var alterOnly = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.Ghost ADD Email VARCHAR(200) NULL;");

        var catalog = CatalogBuilder.Build([alterOnly]);

        var entry = Assert.Single(catalog.Skipped.Entries);
        Assert.Equal(AnalysisPass.Catalog, entry.Pass);
        Assert.Contains("dbo.Ghost", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogBuilder_WellOrderedDdl_RecordsNoSkips()
    {
        var create = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");
        var alter = SqlScriptParser.ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Phone VARCHAR(20) NULL;");
        var index = SqlScriptParser.ParseText("03_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");

        var catalog = CatalogBuilder.Build([create, alter, index]);

        Assert.Empty(catalog.Skipped.Entries);
    }

    [Fact]
    public void LineageResolver_ViewOverUnknownTable_RecordsSkip()
    {
        // dbo.Ghost has no DDL anywhere in the scanned set - CLAUDE.md: never guess, but also
        // never silently treat the view as if it had zero problems.
        var view = SqlScriptParser.ParseText("view.sql", "CREATE VIEW dbo.vw_Ghost AS SELECT g.Id FROM dbo.Ghost AS g;");

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
        var viewA = SqlScriptParser.ParseText("a.sql", "CREATE VIEW dbo.vw_A AS SELECT b.Id FROM dbo.vw_B AS b;");
        var viewB = SqlScriptParser.ParseText("b.sql", "CREATE VIEW dbo.vw_B AS SELECT a.Id FROM dbo.vw_A AS a;");

        var catalog = CatalogBuilder.Build([viewA, viewB]);
        var lineage = LineageResolver.Resolve(catalog, [viewA, viewB]);

        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "view dependency");
    }

    [Fact]
    public void TypedPredicateExtractor_UpdateWhereClause_ResolvesInsteadOfSkipping()
    {
        // docs/audit-remediation-plan.md Phase 4.1: UPDATE's WHERE clause previously had no
        // FROM-scope pushed at all, so this predicate was invisible to Pass 3 - it recorded a
        // "comparison outside FROM scope" skip instead of a real finding (see git history for
        // the Phase 0.1 version of this test, which pinned that exact gap). Id outranks
        // NVarChar in T-SQL's precedence list, so the parameter converts, not the column -
        // SeekPreserved is the correct resolved verdict, not evidence of a remaining gap.
        var sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_RenameUser @Id NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = 'x' WHERE Id = @Id;
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.SkippedConstructs);
        var finding = Assert.Single(extraction.TypedFindings);
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal("Id", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void TypedPredicateExtractor_BareIfComparison_StillRecordsSkip()
    {
        // The genuinely scope-less case (Phase 4.1 fixed UPDATE/DELETE/MERGE specifically, not
        // every possible scope-less comparison) - a bare IF still has no column side to resolve
        // and must still be recorded, not silently dropped.
        var sql = """
            CREATE PROCEDURE dbo.usp_Check @Flag INT
            AS
            BEGIN
                IF @Flag = 1
                BEGIN
                    RETURN;
                END
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
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
        var result = SqlScriptParser.ParseText("test.sql", sql);
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
                IF @Id = N'0'
                BEGIN
                    RETURN;
                END
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
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
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);

        var exception = Record.Exception(() => TypedPredicateExtractor.Extract(result, catalog, lineage));

        Assert.Null(exception);
    }
}
