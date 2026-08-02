using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Diagnostics;

/// <summary>
/// Executable ledger of KNOWN analysis gaps - the test-suite counterpart of
/// ConstructCoverage.json. Each test runs the full pipeline (ScanReportBuilder, the same entry
/// point production uses) on a scenario the engine cannot yet analyze completely, and asserts
/// the CURRENT limited behavior - an Unknown verdict, a missing Tier-1 finding, a stale
/// Indexed claim - never the desired one. The suite therefore stays green today, and the
/// moment an implementation closes one of these gaps its test here FAILS, forcing whoever
/// closed it to flip the assertion to the now-correct verdict and promote the scenario into
/// the appropriate real suite. A test in this class is a to-do item with teeth, not an
/// endorsement of the behavior it pins.
///
/// The SQL here is synthetic by design, like fixtures/mini_project/ - these are pipeline
/// characterization scenarios, distinct from the tier1/ rule fixtures whose real-world-sourced
/// requirement (CLAUDE.md) applies to rules' fire/clean evidence, not to gap pinning.
///
/// Two declared gaps are NOT pinned here: nested sp_executesql declared-parameter propagation
/// across two nesting levels (ConstructCoverage.json, verifiedBy: None) and the
/// Collation.IsWindowsFamily prefix heuristic misclassifying UTF-8/_BIN2 collations - both
/// need multi-level dynamic scaffolding or matrix regeneration to demonstrate end to end and
/// should gain characterization coverage when their areas are next touched.
/// </summary>
public sealed class KnownGapCharacterizationTests
{
    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("gap.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        // Every scenario must parse cleanly - a gap pinned against a half-parsed script would
        // characterize ScriptDom error recovery, not the analysis gap it claims to.
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    // ------------------------------------------------------------------
    // Typing and verdicts
    // ------------------------------------------------------------------

    // ScalarUdfReturnType was pinned here and is now CLOSED - CatalogBuilder registers every
    // scalar CREATE/ALTER FUNCTION's RETURNS type (DatabaseCatalog.AddScalarFunctionReturnType),
    // and TypedPredicateExtractor.ResolveFunctionCallOperand consults it when a call isn't a
    // built-in. Moved to Predicates/ScalarUdfPipelineTests.cs asserting the corrected
    // ScanForced outcome through the full pipeline.

    // ComputedColumn was pinned here and is now CLOSED - ComputedColumnTypeResolver infers a
    // computed column's type from its defining expression (sibling column references,
    // literals, CAST/CONVERT, binary expressions combined via T-SQL data type precedence), so
    // FirstName + ' ' + LastName now types as varchar instead of silently vanishing. Moved to
    // Catalog/CatalogBuilderTests.cs (unit coverage of the resolver via the public CatalogBuilder
    // surface) and Predicates/ComputedColumnPipelineTests.cs (full pipeline).
    // Function calls, CASE, and other expression kinds remain deliberately unresolved (Unknown)
    // - those are CLAUDE.md's own named hard cases or need catalog data not yet built at this
    // point in CatalogBuilder's pass ordering, not silently dropped: an unresolved computed
    // column now reaches the skip ledger (Diagnostics/AnalysisPass.Catalog, "computed column
    // type") rather than the comparison disappearing with no trace at all.

    [Fact]
    public void SameCategoryDifferentCollations_CoercibilityUnimplemented_FallsToUnknown()
    {
        // varchar-vs-varchar across SQL_* and Windows collations is a real production
        // seek-killer (collation coercion converts one side), but same-category collation
        // divergence has no coercibility rules yet - both directions classify Unknown.
        var report = Scan("""
            CREATE TABLE dbo.LocalCustomers (
                Email varchar(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                INDEX IX_Email (Email));
            GO
            CREATE TABLE dbo.VendorCustomers (
                Email varchar(100) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            SELECT 1
            FROM dbo.LocalCustomers l
            INNER JOIN dbo.VendorCustomers v ON l.Email = v.Email;
            """);

        Assert.NotEmpty(report.TypedFindings);
        Assert.All(report.TypedFindings, f => Assert.Equal(Verdict.Unknown, f.Verdict));
    }

    [Fact]
    public void SameCategoryFacetDifference_IsInvisible_ClassifiesSeekPreserved()
    {
        // decimal(10,2) column vs a decimal(9,8) literal: same category, so the classifier
        // returns SeekPreserved without ever looking at precision/scale. Facet-aware
        // classification (oracle-grounded, like TypePairMatrix) may well revise pairs like
        // this; today the comparison is simply not inspected beyond its category.
        var report = Scan("""
            CREATE TABLE dbo.Ledger (Amount decimal(10,2) NOT NULL, INDEX IX_Amount (Amount));
            GO
            SELECT 1 FROM dbo.Ledger WHERE Amount = 1.23456789;
            """);

        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.Empty(report.TypedFindings);
    }

    // The three Tier-1 structural holes pinned here (function-wrapped column inside an IN
    // predicate, as a BETWEEN bound, and CAST wrapping an expression that merely CONTAINS a
    // column rather than IS one) are now CLOSED - NonSargablePredicateScanner gained an
    // InPredicate visitor, BETWEEN inspection now covers all three positions, and CAST/
    // CONVERT/arithmetic search their operand subtree via the shared FindAnyColumn helper
    // instead of requiring a direct ColumnReferenceExpression. Moved to
    // Predicates/NonSargablePredicateScannerTests.cs asserting the corrected fires.

    // ------------------------------------------------------------------
    // Catalog precision bugs (places Indexed can be claimed falsely - under CLAUDE.md's
    // precision-first rule these outrank every completeness gap)
    // ------------------------------------------------------------------

    // DisabledIndex was pinned here and is now CLOSED - ALTER INDEX ... DISABLE flips
    // CatalogIndex.IsDisabled (CatalogBuilder.VisitAlterIndex), so this scenario moved to
    // Predicates/DisabledIndexPipelineTests.cs asserting the corrected ScanForced/Indexed=false
    // outcome full pipeline through ScanReportBuilder.

    // DroppedPrimaryKeyConstraint was pinned here and is now CLOSED - ALTER TABLE ... DROP
    // CONSTRAINT removes the matching named CatalogIndex (CatalogBuilder.VisitDropTableElements),
    // so this scenario moved to Predicates/DroppedConstraintPipelineTests.cs asserting the
    // corrected ScanForced/Indexed=false outcome through the full pipeline.

    // ------------------------------------------------------------------
    // Lineage: constructs that silently give up
    // ------------------------------------------------------------------

    [Fact]
    public void Synonym_IsNeverResolved_QueryThroughItYieldsNoTypedFinding()
    {
        // dbo.Stock is a synonym for a table whose DDL was scanned in the same run - a pure
        // name aliasing the catalog could resolve - yet no pass models CREATE SYNONYM, so
        // the mismatch behind it is invisible (only skip-ledger entries record the loss).
        var report = Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE SYNONYM dbo.Stock FOR dbo.Inventory;
            GO
            SELECT 1 FROM dbo.Stock WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.NotEmpty(report.SkippedConstructs);
    }

    [Fact]
    public void RecursiveCte_RecursiveBranchIsUnknown_MismatchThroughCteNeverConfirmed()
    {
        // The recursive member resolves as Unknown (anchor-only resolution), so a column
        // read through the CTE has Union[BaseColumn, Unknown] provenance and the nvarchar
        // mismatch on the indexed varchar CategoryCode can never reach ScanForced.
        var report = Scan("""
            CREATE TABLE dbo.Categories (
                CategoryCode varchar(20) NOT NULL,
                ParentCode varchar(20) NULL,
                INDEX IX_CategoryCode (CategoryCode));
            GO
            WITH Tree AS (
                SELECT CategoryCode, ParentCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode
                FROM dbo.Categories c
                INNER JOIN Tree t ON c.ParentCode = t.CategoryCode)
            SELECT 1 FROM Tree WHERE CategoryCode = N'X';
            """);

        Assert.DoesNotContain(report.TypedFindings, f => f.Verdict == Verdict.ScanForced);
    }

    [Fact]
    public void SelectIntoFromView_ColumnsStayUntyped_MismatchOnTempCopyIsSilentlyDropped()
    {
        // SELECT ... INTO resolution never consults views (pass-ordering constraint), so
        // #snap.Badge - really dbo.Employees.Badge one trivial layer away - stays untyped.
        // The nvarchar mismatch against it then vanishes ENTIRELY: no finding, no Unknown,
        // no skip-ledger entry - the same silent-drop honesty hole the computed-column test
        // pins (a null-typed column side never reaches the classifier OR the ledger).
        var report = Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL, INDEX IX_Badge (Badge));
            GO
            CREATE VIEW dbo.vEmployees AS SELECT Badge FROM dbo.Employees;
            GO
            CREATE PROCEDURE dbo.usp_Snapshot AS
            BEGIN
                SELECT Badge INTO #snap FROM dbo.vEmployees;
                SELECT 1 FROM #snap WHERE Badge = N'B1';
            END;
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.SkippedConstructs);
        Assert.Equal(0, report.TypedPredicateSummary.TotalClassified);
    }

    [Fact]
    public void CrossDatabaseReference_GetsAKeyNothingPopulates_NoTypedFinding()
    {
        // ArchiveDb.dbo.Shipments is keyed distinctly from the scanned dbo.Shipments and no
        // DDL ever populates a cross-database key - the reference is unresolvable by
        // construction, so the mismatch produces no typed finding. Unlike the computed-column
        // and SELECT INTO silent drops, this loss IS honestly ledgered.
        var report = Scan("""
            CREATE TABLE dbo.Shipments (TrackingNo varchar(30) NOT NULL, INDEX IX_TrackingNo (TrackingNo));
            GO
            SELECT 1 FROM ArchiveDb.dbo.Shipments WHERE TrackingNo = N'T1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("ArchiveDb.dbo.Shipments", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Dynamic SQL: declared gaps (ConstructCoverage.json, verifiedBy: None until now)
    // ------------------------------------------------------------------

    [Fact]
    public void DynamicSql_TempTableFromEnclosingProcScope_DoesNotResolveInsideReparsedText()
    {
        // The identical predicate appears twice: once statically (resolves through the
        // proc-scoped #w catalog entry, ScanForced + Indexed) and once inside EXEC('...')
        // (the reparsed fragment carries no enclosing proc scope, so #w never resolves and
        // no ScanForced finding is produced). When scope propagation lands, both must fire.
        var report = Scan("""
            CREATE TABLE dbo.Widgets (WidgetCode varchar(25) NOT NULL, INDEX IX_WidgetCode (WidgetCode));
            GO
            CREATE PROCEDURE dbo.usp_DynamicTemp AS
            BEGIN
                CREATE TABLE #w (WidgetCode varchar(25) NOT NULL, INDEX IX_W (WidgetCode));
                INSERT INTO #w SELECT WidgetCode FROM dbo.Widgets;
                EXEC('SELECT 1 FROM #w WHERE WidgetCode = N''W1''');
                SELECT 1 FROM #w WHERE WidgetCode = N'W2';
            END;
            """);

        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);

        // Exactly one ScanForced - the static twin. The dynamic occurrence contributes none.
        var scanForced = Assert.Single(report.TypedFindings, f => f.Verdict == Verdict.ScanForced);
        Assert.Null(scanForced.DynamicSqlCallSite);
        Assert.True(scanForced.Column.Indexed);
    }

    [Fact]
    public void DynamicSql_AliasTypedDeclaredParameter_ResolvesToNullType_Unknown()
    {
        // sp_executesql's @params declaration is parsed with NO DatabaseCatalog, so the
        // dbo.CodeType alias (nvarchar(50), declared in the same scanned file) resolves to
        // null and the flagship varchar-vs-nvarchar ScanForced degrades to Unknown.
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
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }
}
