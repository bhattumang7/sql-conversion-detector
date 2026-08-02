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

    // SameCategoryDifferentCollations was pinned here and is now CLOSED - oracle-verified
    // directly (Msg 468, "Cannot resolve the collation conflict"): two real columns, same
    // string category, differing native collations, and no explicit COLLATE anywhere does not
    // compile at all - not a seek-loss verdict to leave Unknown. Reported as a dedicated
    // CollationConflictFinding (TypedPredicateExtractor.TryRecordCollationConflict) instead of a
    // routine TypedPredicateFinding. Moved to
    // Predicates/ExplicitCollatePipelineTests.cs#ColumnVsColumnDifferingCollations_NoExplicitCollateAnywhere_ReportsCollationConflict.
    // A column vs. a literal with an explicit, differing COLLATE is a DIFFERENT, real
    // ScanForced (that literal is always "coercible default" and never conflicts) - see the
    // same test file's LiteralWithDifferingExplicitCollate_ForcesColumnScanForced.

    // SameCategoryFacetDifference was pinned here and turned out to be a NON-GAP, not a fix -
    // direct oracle probing (Docker SQL Server, compile-only SHOWPLAN_XML) across
    // varchar(10)/varchar(100)/varchar(max), nvarchar(10)/nvarchar(max), decimal(10,2) vs
    // decimal(9,8)/decimal(38,10)/a high-precision literal, and char(10)/char(50) all showed a
    // clean Index Seek with no CONVERT_IMPLICIT anywhere - length/precision/scale differences
    // within the same category never defeat sargability. VerdictClassifier's unconditional
    // SeekPreserved for a same-category pair is therefore already correct; no facet-aware
    // classification workstream is needed. See Rules/VerdictClassifierTests.cs's
    // Classify_SameCategoryFacetDifference_* tests for the positive assertions this evidence
    // backs.

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

    // Synonym was pinned here and is now CLOSED - DatabaseCatalog.ResolveSynonymName walks a
    // synonym chain to the real base object it means, and FromScopeResolver canonicalizes every
    // FROM-clause reference through it before either the catalog/view lookup, so a query
    // through a synonym resolves exactly like the base table itself would - the finding names
    // the REAL base table, not the synonym, matching what a SARIF consumer or the Verify oracle
    // needs to act on it. Moved to Catalog/SynonymResolutionTests.cs.

    // RecursiveCte was pinned here and is now CLOSED - T-SQL enforces (Msg 240) that a
    // recursive member's column types match the anchor's exactly, so CteResolver now uses the
    // anchor's type directly instead of wrapping it in Union[BaseColumn, Unknown] (which made
    // every predicate through any recursive CTE unclassifiable). The anchor's own index claim
    // is downgraded to Declared (type kept, index dropped) since a recursive CTE materializes
    // through a spool. Moved to Lineage/RecursiveCteAnchorTypeTests.cs.

    // SelectIntoFromView was pinned here and is now CLOSED - SelectIntoLineagePass re-resolves
    // every SELECT INTO target's columns after LineageResolver has run, through the same
    // QueryExpressionResolver machinery a view's own SELECT list uses, so a target column
    // sourced from a view (or a UNION) now types correctly instead of vanishing with zero
    // trace. Moved to Lineage/SelectIntoLineagePassTests.cs.

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
