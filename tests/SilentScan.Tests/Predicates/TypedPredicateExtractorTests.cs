using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class TypedPredicateExtractorTests
{
    private static IReadOnlyList<TypedPredicateFinding> Extract(params string[] batches) =>
        ExtractAll(batches).TypedFindings;

    private static IReadOnlyList<ExpressionDerivedFinding> ExtractExpressionDerived(params string[] batches) =>
        ExtractAll(batches).ExpressionDerivedFindings;

    private static PredicateExtractionResult ExtractAll(params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage);
    }

    [Fact]
    public void Extract_LiteralComparison_CarriesLiteralTextForProbeReconstruction()
    {
        // docs/audit-remediation-plan.md Phase 5.2: the finding must carry enough to
        // reconstruct the exact literal later during oracle verification, not just its type.
        // (This is itself infrastructure the oracle-confirmed tests below depend on, not a
        // verdict claim of its own - nothing to oracle-confirm here.)
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);",
            "SELECT DisplayName FROM dbo.Users WHERE DisplayName = N'Alice';");

        var finding = Assert.Single(findings);
        var value = Assert.IsType<PredicateOperand.Value>(finding.OtherOperand);
        Assert.True(value.IsLiteral);
        Assert.Equal("N'Alice'", value.LiteralText);
    }

    [Fact]
    public void Extract_VariableDeclaredInAnEarlierAdHocBatch_DoesNotLeakIntoALaterBatchWithNoDeclareOfItsOwn()
    {
        // DECLARE'd variable types are batch-scoped in real T-SQL - a variable declared in one
        // GO-separated ad-hoc batch (no CREATE PROCEDURE wrapper to reset scope at) must not
        // silently type a same-named, un-declared reference in a LATER batch from the first
        // batch's stale type. Before the fix, @x's INT type from batch 2 leaked into batch 3,
        // silently classifying Col = @x as SeekPreserved (int vs int) instead of Unknown.
        var findings = Extract(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "DECLARE @x INT = 1; SELECT 1;",
            "SELECT 1 FROM dbo.T WHERE Col = @x;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.Equal("operand-type-unresolved", finding.UnknownReason);
    }

    [Fact]
    public void Extract_UnknownVerdict_CarriesAStableReasonCode()
    {
        // sql_variant vs. an in-model value is no longer Unknown (it now participates in the
        // standard precedence rule - see the dedicated sql_variant tests) - two sql_variant
        // operands stays genuinely Unknown, since comparison semantics then depend on the boxed
        // base type at execution time.
        var findings = Extract(
            "CREATE TABLE dbo.Docs (Payload sql_variant NOT NULL, Other sql_variant NOT NULL);",
            "SELECT 1 FROM dbo.Docs WHERE Payload = Other;");

        // Column-vs-column reports once per side (each side gets its own "this column is the
        // indexed/classified one" finding) - both must agree.
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal(Verdict.Unknown, f.Verdict);
            Assert.Equal("out-of-model-category:SqlVariant", f.UnknownReason);
        });
    }

    [Fact]
    public void Extract_NonUnknownVerdict_UnknownReasonIsNull()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "SELECT 1 FROM dbo.Orders WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.NotEqual(Verdict.Unknown, finding.Verdict);
        Assert.Null(finding.UnknownReason);
    }

    [Fact]
    public void Extract_Finding_CarriesThePredicateFragmentTextAndAStableFingerprint()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "SELECT 1 FROM dbo.Orders WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal("OrderId = 5", finding.PredicateFragmentText);
        Assert.NotNull(finding.Fingerprint);
        Assert.Equal(finding.Fingerprint, TypedPredicateFindingIdentity.ComputeFingerprint(finding.Column, finding.OtherOperand, finding.Operator));
    }

    [Fact]
    public void Extract_IndexedColumn_CarriesTheRealIndexName()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Accounts_Code (Code));",
            "SELECT 1 FROM dbo.Accounts WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.True(finding.Column.Indexed);
        Assert.Equal("IX_Accounts_Code", finding.Column.IndexName);
    }

    [Fact]
    public void Extract_UnindexedColumn_IndexNameIsNull()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL);",
            "SELECT 1 FROM dbo.Accounts WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.False(finding.Column.Indexed);
        Assert.Null(finding.Column.IndexName);
    }

    [Fact]
    public void Extract_ParameterComparison_IsNotMarkedAsLiteral()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find @Id INT
            AS
            BEGIN
                SELECT OrderId FROM dbo.Orders WHERE OrderId = @Id;
            END
            """);

        var finding = Assert.Single(findings);
        var value = Assert.IsType<PredicateOperand.Value>(finding.OtherOperand);
        Assert.False(value.IsLiteral);
        Assert.Null(value.LiteralText);
    }

    [Fact]
    public void Extract_DirectBaseTablePredicate_LeavesImmediateRelationNull()
    {
        // Depth 0 - the predicate already queries the base table directly, so there's no
        // separate "immediate relation" to route a probe through differently.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);",
            "SELECT OrderCode FROM dbo.Orders WHERE OrderCode = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal(0, finding.Column.Depth);
        Assert.Null(finding.Column.ImmediateRelationQualifiedName);
        Assert.Null(finding.Column.ImmediateColumnName);
    }

    [Fact]
    public void Extract_PredicateThroughViewWithRenamedColumn_ImmediateColumnNameIsTheViewsOwnAlias()
    {
        // The view exposes the base column under a DIFFERENT name than the base table's own -
        // the probe must query the view using the view's own exposed name, not the base
        // column's name (which wouldn't exist as a selectable column on the view at all).
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT Code AS OrderCode FROM dbo.Orders;",
            "SELECT OrderCode FROM dbo.vw_Orders WHERE OrderCode = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal("dbo.vw_Orders", finding.Column.ImmediateRelationQualifiedName);
        Assert.Equal("OrderCode", finding.Column.ImmediateColumnName);
    }

    [Fact]
    public void Extract_ComparisonInSelectListCaseExpression_ProducesNoFinding()
    {
        // The false-positive this guards: a comparison that never filters rows (a SELECT-list
        // CASE branch) has no seek to lose, so it must not be reported as a verdict-bearing
        // finding at all - before filter-context tracking existed, TypedPredicateExtractor
        // reported EVERY comparison anywhere in the tree, filter or not.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT CASE WHEN Code = N'X' THEN 1 ELSE 0 END AS Flag FROM dbo.Orders;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_ComparisonInOrderByExpression_ProducesNoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.Orders ORDER BY CASE WHEN Code = N'X' THEN 1 ELSE 0 END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_ComparisonInSelectListButQueryAlsoHasWhereClause_SelectListStillExcluded()
    {
        // Filter-context must reset per query part, not leak from WHERE into the SELECT list of
        // the SAME query specification.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Status VARCHAR(10) NOT NULL);",
            "SELECT CASE WHEN Code = N'X' THEN 1 ELSE 0 END AS Flag FROM dbo.Orders WHERE Status = 'A';");

        var finding = Assert.Single(findings);
        Assert.Equal("Status", finding.Column.ColumnName);
    }

    [Fact]
    public void Extract_BareIfComparisonOutsideAnyQuery_StillLedgeredAsSkip()
    {
        // Regression guard: the filter-context gate must not swallow the pre-existing "no FROM
        // scope in effect" ledger path for a genuinely scope-less comparison (a bare IF/WHILE
        // condition in procedural code) - that's a distinct case from a SELECT-list comparison
        // and must still be honestly accounted for, not silently dropped.
        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Test @Id INT AS BEGIN IF @Id = 1 BEGIN RETURN; END END;");

        Assert.Contains(result.SkippedConstructs, c => c.Reason.Contains("no FROM scope in effect", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SameColumnContradiction_NoFindingAndLedgeredAsNormalizationEliminated()
    {
        // WHERE Id = 1 AND Id = 2 can never select a row - the engine folds this to a Constant
        // Scan before sargability is ever considered (oracle-confirmed directly), so reporting a
        // seek/scan verdict for either comparison would be a false positive, not merely an
        // unconfirmed one.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Id = 1 AND Id = 2;");

        Assert.Empty(result.TypedFindings);
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_SiblingConjunctOfAnUnsatisfiableAnd_AlsoEliminated()
    {
        // The whole AND can never select a row once Id=1 AND Id=2 contradict, so Other=5 never
        // meaningfully reaches a Filter/Seek decision either - not just the two contradicting
        // comparisons.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL, Other INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Id = 1 AND Id = 2 AND Other = 5;");

        Assert.Empty(result.TypedFindings);
        Assert.Equal(3, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_OrDisjunctOutsideTheContradiction_StillReportsNormally()
    {
        // Only the dead disjunct's own comparisons are eliminated - the live OR branch is
        // unaffected, since the OR as a whole is not provably dead.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL, Other INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (Id = 1 AND Id = 2) OR Other = 5;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("Other", finding.Column.ColumnName);
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_NestedSubqueryHasOwnScope_DoesNotLeakOuterAlias()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE TABLE dbo.Lines (OrderId INT NOT NULL, Qty INT NOT NULL);",
            """
            SELECT o.OrderId
            FROM dbo.Orders o
            WHERE o.OrderId IN (SELECT l.OrderId FROM dbo.Lines l WHERE l.Qty = 5);
            """);

        // Two independent, correctly-scoped predicates: the outer o.OrderId IN (...) resolves
        // against Orders (Phase 4.3 added IN-subquery coverage), and the inner l.Qty = 5 must
        // resolve against Lines, not bleed into Orders' scope.
        Assert.Equal(2, findings.Count);
        var outer = Assert.Single(findings, f => f.Operator == "IN");
        Assert.Equal("dbo.Orders", outer.Column.TableQualifiedName);
        Assert.Equal("OrderId", outer.Column.ColumnName);

        var inner = Assert.Single(findings, f => f.Operator == "=");
        Assert.Equal("dbo.Lines", inner.Column.TableQualifiedName);
        Assert.Equal("Qty", inner.Column.ColumnName);
    }

    [Fact]
    public void Extract_VariableWithNoDeclaration_ProducesUnknownVerdict()
    {
        // A parameter declared in a different, unrelated batch: our per-proc variable scope
        // deliberately resets, so this must resolve Unknown rather than leaking a stale type.
        // Unknown is a claim about our own uncertainty, not the engine's behavior - nothing to
        // oracle-confirm (CLAUDE.md: never guess).
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);",
            "SELECT OrderCode FROM dbo.Orders WHERE OrderCode = @UndeclaredParam;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }

    [Fact]
    public void Extract_IntRoundTrippedThroughTwoViewsAndAProc_ReportsExpressionDerivedNotTyped()
    {
        // The exact case a direct question surfaced: a table has CustomerId INT (indexed),
        // vw_OrdersStr casts it to VARCHAR, vw_OrdersRoundTrip casts it back to INT, and a
        // proc filters on the round-tripped column. Both sides of the final predicate are
        // INT, so there's no type-precedence mismatch to report - but the column is a
        // computed expression by then, so no index seek is possible regardless. Before this
        // rule existed, this produced zero findings of any kind (silently dropped).
        var findings = ExtractExpressionDerived(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);
            """,
            "CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);",
            """
            CREATE VIEW dbo.vw_OrdersStr AS
            SELECT OrderId, CAST(CustomerId AS VARCHAR(20)) AS CustomerIdStr
            FROM dbo.Orders;
            """,
            """
            CREATE VIEW dbo.vw_OrdersRoundTrip AS
            SELECT OrderId, CAST(CustomerIdStr AS INT) AS CustomerIdAgain
            FROM dbo.vw_OrdersStr;
            """,
            """
            CREATE PROCEDURE dbo.usp_GetOrdersByCustomer @CustomerId INT
            AS
            BEGIN
                SELECT OrderId FROM dbo.vw_OrdersRoundTrip WHERE CustomerIdAgain = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("CustomerIdAgain", finding.ColumnName);
        Assert.Equal(2, finding.TransformationChain.Count);
        var underlying = Assert.Single(finding.UnderlyingBaseColumns);
        Assert.Equal("dbo.Orders", underlying.TableQualifiedName);
        Assert.Equal("CustomerId", underlying.ColumnName);
        Assert.True(underlying.Indexed);

        // Roadmap Phase E3: enough to actually probe this finding, not just describe it - the
        // predicate was written unqualified against dbo.vw_OrdersRoundTrip directly (no alias),
        // so ImmediateRelationAlias is correctly null while ImmediateRelationQualifiedName still
        // resolves through the real, catalog-known view layer.
        Assert.Equal("CustomerIdAgain = @CustomerId", finding.PredicateFragmentText);
        Assert.Equal("dbo.vw_OrdersRoundTrip", finding.ImmediateRelationQualifiedName);
        Assert.Null(finding.ImmediateRelationAlias);

        // And no ordinary typed finding was produced for it either - it's reported exactly once.
        Assert.Empty(Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);",
            "CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);",
            "CREATE VIEW dbo.vw_OrdersStr AS SELECT OrderId, CAST(CustomerId AS VARCHAR(20)) AS CustomerIdStr FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_OrdersRoundTrip AS SELECT OrderId, CAST(CustomerIdStr AS INT) AS CustomerIdAgain FROM dbo.vw_OrdersStr;",
            """
            CREATE PROCEDURE dbo.usp_GetOrdersByCustomer @CustomerId INT
            AS
            BEGIN
                SELECT OrderId FROM dbo.vw_OrdersRoundTrip WHERE CustomerIdAgain = @CustomerId;
            END
            """));
    }

    [Fact]
    public void Extract_PlainPassthroughThroughTwoViews_NoExpressionDerivedFinding()
    {
        // Negative case: a column that passes through two views unchanged must not be
        // flagged - only an actual CAST/expression layer should trigger this rule.
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.Orders (CustomerId INT NOT NULL);",
            "CREATE VIEW dbo.vw_A AS SELECT CustomerId FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_B AS SELECT CustomerId FROM dbo.vw_A;",
            "SELECT CustomerId FROM dbo.vw_B WHERE CustomerId = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_CastHiddenInLocalDerivedTable_ReportsExpressionDerived()
    {
        // Same phenomenon, no named view at all - the CAST is hidden inside an inline
        // derived table within the very statement being scanned. Tier-1's syntactic scanner
        // can't see this (the predicate itself is just "sub.X = @p"); only lineage-aware
        // resolution catches it.
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            """
            SELECT sub.X
            FROM (SELECT CAST(Col AS VARCHAR(10)) AS X FROM dbo.T) sub
            WHERE sub.X = 'abc';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("X", finding.ColumnName);
        var underlying = Assert.Single(finding.UnderlyingBaseColumns);
        Assert.Equal("Col", underlying.ColumnName);
    }

    [Fact]
    public void Extract_ArithmeticExpressionCombiningTwoColumnsInView_ReportsBothUnderlyingColumns()
    {
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.LineItems (Price INT NOT NULL, Quantity INT NOT NULL);",
            "CREATE VIEW dbo.vw_LineTotals AS SELECT Price * Quantity AS Total FROM dbo.LineItems;",
            "SELECT Total FROM dbo.vw_LineTotals WHERE Total = 100;");

        var finding = Assert.Single(findings);
        Assert.Equal(2, finding.UnderlyingBaseColumns.Count);
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.ColumnName == "Price");
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.ColumnName == "Quantity");
    }

    [Fact]
    public void Extract_WildcardCountInViewFeedingAPredicate_DoesNotCrashAndIsNotReported()
    {
        // Regression: COUNT(*)'s `*` is a ColumnReferenceExpression with no
        // MultiPartIdentifier. The generic expression-column-collector used to resolve
        // expression-derived provenance must skip it rather than null-refing, the same class
        // of bug NonSargablePredicateScanner hit earlier for the same construct. And since
        // COUNT(*) has no traceable underlying base column, it's correctly filtered out as
        // non-actionable rather than reported with an empty UnderlyingBaseColumns list.
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.Orders (CustomerId INT NOT NULL);",
            "CREATE VIEW dbo.vw_OrderCounts AS SELECT CustomerId, COUNT(*) AS OrderCount FROM dbo.Orders GROUP BY CustomerId;",
            "SELECT CustomerId FROM dbo.vw_OrderCounts WHERE OrderCount = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_OpaqueExpressionWithNoTraceableColumn_NotReported()
    {
        // Real corpus case (SQL Server First Responder Kit): a derived-table column built
        // from a niladic function call with no column reference anywhere inside it -
        // technically expression-derived, but nothing actionable (no real column/index to
        // point at), so a downstream predicate against it should not be reported.
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            """
            SELECT * FROM (
                SELECT Id, NEWID() AS Token FROM dbo.T
            ) sub
            WHERE sub.Token = '00000000-0000-0000-0000-000000000000';
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_FunctionWrappedColumnInView_ReportsExpressionDerived()
    {
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.Users (Name VARCHAR(50) NOT NULL);",
            "CREATE VIEW dbo.vw_UpperNames AS SELECT UPPER(Name) AS UpperName FROM dbo.Users;",
            "SELECT UpperName FROM dbo.vw_UpperNames WHERE UpperName = 'ALICE';");

        var finding = Assert.Single(findings);
        var underlying = Assert.Single(finding.UnderlyingBaseColumns);
        Assert.Equal("Name", underlying.ColumnName);
    }

    [Fact]
    public void Extract_UnionViewWithOneCastBranch_ReportsExpressionDerivedForMixedBranchColumn()
    {
        // CLAUDE.md Pass 2: "record ALL branch types - the mixed-branch case is itself a
        // finding." vw_Combined's Id column passes through untouched from dbo.Recent but is
        // CAST from VARCHAR in the dbo.Archive branch; a predicate against the merged column
        // can't seek through the Archive branch regardless of the other branch being clean.
        var findings = ExtractExpressionDerived(
            """
            CREATE TABLE dbo.Recent (Id INT NOT NULL);
            """,
            """
            CREATE TABLE dbo.Archive (IdStr VARCHAR(20) NOT NULL);
            """,
            """
            CREATE VIEW dbo.vw_Combined AS
            SELECT Id FROM dbo.Recent
            UNION ALL
            SELECT CAST(IdStr AS INT) AS Id FROM dbo.Archive;
            """,
            "SELECT Id FROM dbo.vw_Combined WHERE Id = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal("Id", finding.ColumnName);
        // Both branches' real columns are reported - the clean dbo.Recent.Id passthrough for
        // context, and dbo.Archive.IdStr as the actual source of the mismatch.
        Assert.Equal(2, finding.UnderlyingBaseColumns.Count);
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.TableQualifiedName == "dbo.Recent" && c.ColumnName == "Id");
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.TableQualifiedName == "dbo.Archive" && c.ColumnName == "IdStr");
    }

    [Fact]
    public void Extract_UnionViewWithAllPassthroughBranches_NoExpressionDerivedFinding()
    {
        // Negative case: every branch is a clean passthrough, so the merged column must not
        // be flagged even though it's wrapped in a Union provenance node.
        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.Recent (Id INT NOT NULL);",
            "CREATE TABLE dbo.Archive (Id INT NOT NULL);",
            "CREATE VIEW dbo.vw_Combined AS SELECT Id FROM dbo.Recent UNION ALL SELECT Id FROM dbo.Archive;",
            "SELECT Id FROM dbo.vw_Combined WHERE Id = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_UnionViewWithAllPassthroughBranchesAgreeingOnType_ReachesRealVerdict()
    {
        // The gap the passthrough test above never actually closed: no ExpressionDerivedFinding
        // firing does NOT mean a TYPED verdict was reached - before this, EVERY UNION-view
        // column, even one where every branch independently agrees on type, was routed straight
        // to Unknown (PredicateOperand.Value(Type: null)), so no ScanForced/SeekPreserved
        // verdict was ever produced either. T-SQL doesn't narrow a UNION's output type per row -
        // when every branch agrees, the merged column's own runtime type is fully determined
        // regardless of which branch a given row came from, so this is a real (non-guessed)
        // verdict, not a "pick the first branch" shortcut.
        var findings = Extract(
            "CREATE TABLE dbo.Recent (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Recent_Code (Code));",
            "CREATE TABLE dbo.Archive (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Archive_Code (Code));",
            "CREATE VIEW dbo.vw_Combined AS SELECT Code FROM dbo.Recent UNION ALL SELECT Code FROM dbo.Archive;",
            "SELECT Code FROM dbo.vw_Combined WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        // Never claims a real index - no single branch's own index is "the" index for a merged,
        // multi-table column.
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_QualifierNotInScope_NoFinding_NeverFallsBackToNameOnlyMatch()
    {
        // docs/audit-remediation-plan.md Phase 2.1: 'x' is not a declared alias in this FROM
        // scope. The only column named TrackingCode in scope belongs to dbo.Shipments (aliased
        // 's') - before the fix, an unresolved qualifier fell back to searching every relation
        // by column name alone, and since exactly one match existed, it silently misattributed
        // this predicate to dbo.Shipments.TrackingCode. A predicate whose qualifier the query
        // never actually declares must produce no finding at all, not a wrong one.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.Shipments (Id INT NOT NULL, TrackingCode NVARCHAR(20) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindShipment @p NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.Orders AS o
                JOIN dbo.Shipments AS s ON o.Id = s.Id
                WHERE x.TrackingCode = @p;
            END
            """);

        // The o.Id = s.Id join condition legitimately resolves (both Int, SeekPreserved) - only
        // the TrackingCode predicate with the bad qualifier must be absent.
        Assert.DoesNotContain(findings, f => f.Column.ColumnName == "TrackingCode");
    }

    [Fact]
    public void Extract_InnerScopeAliasShadowsOuterOfSameName_ResolvesToInnerFirst()
    {
        // The scope chain must try the INNERMOST level first - a self-referencing correlated
        // subquery that reuses the outer alias name for a different table should resolve to the
        // inner one, matching real SQL name-resolution order, not silently prefer the outer scope.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.Archive (Id INT NOT NULL, CustomerId NVARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Check @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.Orders AS o
                WHERE EXISTS (SELECT 1 FROM dbo.Archive AS o WHERE o.CustomerId = @CustomerId);
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Archive", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void Extract_SameNamedTempTableInTwoProcedures_EachProcedureResolvesItsOwnShape()
    {
        // docs/audit-remediation-plan.md Phase 2.5 "Done when": two procedures with same-named
        // temp tables of different shapes each resolve correctly - proves the scoped catalog
        // lookup Phase 2.5 added reaches all the way through predicate extraction, not just the
        // catalog's own storage (see CatalogBuilderTests for the catalog-level version of this).
        //
        // Not oracle-round-tripped: #temp tables only exist for the lifetime of the session/
        // batch that created them (CREATE TABLE #t is not on DdlStatementWhitelist - it lives
        // inside a CREATE PROCEDURE body, which isn't whitelisted DDL either), so there is no
        // way to deploy this shape and query it back from a separate probe connection. The
        // verdict correctness itself (VarChar column vs NVarChar literal, SQL collation) is
        // already oracle-confirmed by the plain-table cases in
        // TypedPredicateExtractorOracleTests; this test's own job is proving the scoped-catalog
        // lookup, not re-proving the type rule.
        var findings = Extract(
            """
            CREATE PROCEDURE dbo.usp_First
            AS
            BEGIN
                CREATE TABLE #t (Col INT NOT NULL);
                SELECT Col FROM #t WHERE Col = 1;
            END
            """,
            """
            CREATE PROCEDURE dbo.usp_Second
            AS
            BEGIN
                CREATE TABLE #t (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                SELECT Col FROM #t WHERE Col = N'x';
            END
            """);

        // usp_First's predicate is Int = Int literal (SeekPreserved); usp_Second's is VarChar
        // column vs NVarChar literal (ScanForced) - if the two #t declarations had clobbered
        // each other, one of these would resolve to the wrong type or Unknown instead.
        Assert.Equal(2, findings.Count);
        var firstFinding = Assert.Single(findings, f => f.Verdict == Verdict.SeekPreserved);
        Assert.Equal(SqlTypeCategory.Int, firstFinding.Column.Type!.Category);

        var secondFinding = Assert.Single(findings, f => f.Verdict == Verdict.ScanForced);
        Assert.Equal("#t", secondFinding.Column.TableQualifiedName);
        Assert.Equal(SqlTypeCategory.VarChar, secondFinding.Column.Type!.Category);
    }

    [Fact]
    public void Extract_TempTableCreatedOnlyInsideDynamicSql_LaterStaticPredicateResolvesItsShape()
    {
        // End-to-end proof of DynamicSqlTempTableDiscovery (found auditing a real production
        // database: 6,516 skip occurrences, 98% concentrated in two modules building a #temp
        // table this exact way): #Runs has no literal CreateTableStatement ANYWHERE in this
        // proc's static AST - it exists only as string-literal pieces assembled into @ddl and
        // handed to EXEC. CatalogBuilder's own static pass alone would leave the later
        // `SELECT RunID FROM #Runs WHERE RunID = 1` predicate with no known table at all
        // (`no known DDL`, TableQualifiedName null, Verdict.Unknown) - merging in
        // DynamicSqlTempTableDiscovery's output (mirroring exactly what LiveScanRunner does)
        // must let that same predicate resolve to a real, typed column instead.
        var sql = """
            CREATE PROCEDURE dbo.usp_BuildRuns
            AS
            BEGIN
                DECLARE @ddl NVARCHAR(MAX) = ''
                SET @ddl = @ddl + 'CREATE TABLE #Runs ('
                SET @ddl = @ddl + 'RunID INT NOT NULL'
                SET @ddl = @ddl + ')'
                EXEC (@ddl)

                SELECT RunID FROM #Runs WHERE RunID = 1;
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.MergeFileModeExtras(DynamicSqlTempTableDiscovery.Discover([result]));
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var findings = TypedPredicateExtractor.Extract(result, catalog, lineage).TypedFindings;

        var finding = Assert.Single(findings);
        Assert.Equal("#Runs", finding.Column.TableQualifiedName);
        Assert.Equal(SqlTypeCategory.Int, finding.Column.Type!.Category);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_InlineTvfWithAlias_QualifiedColumnResolves()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_GetOrders(@Ignored INT) RETURNS TABLE AS RETURN (SELECT Id, CustomerId FROM dbo.Orders);",
            """
            CREATE PROCEDURE dbo.usp_FindOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT f.Id FROM dbo.fn_GetOrders(1) AS f WHERE f.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Depth >= 1);
    }

    [Fact]
    public void Extract_DeclaredTableVariableInFromClause_Resolves()
    {
        // FROM @t parses as VariableTableReference, a distinct ScriptDOM node kind
        // FromScopeResolver never matched at all - it fell through to the same default arm as
        // OPENROWSET/PIVOT (coverage-remediation-plan.md Phase 3.4/3.5's neighbor). This is the
        // ordinary DECLARE @t TABLE(...) case, not the MSTVF return-variable case below.
        //
        // Not oracle-round-tripped: a table variable is scoped to the batch/procedure that
        // declares it, and DECLARE ... TABLE is not on DdlStatementWhitelist - there is no way
        // to stand this shape up outside a procedure body and query it back from a separate
        // probe connection. The ScanForced verdict itself (VarChar column vs NVarChar literal,
        // SQL collation) is already oracle-confirmed elsewhere in this project (e.g.
        // TypedPredicateExtractorOracleTests); this test's own job is proving @t resolves as a
        // FROM-clause relation at all.
        var findings = Extract(
            """
            CREATE PROCEDURE dbo.usp_UseTableVar
            AS
            BEGIN
                DECLARE @t TABLE (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                SELECT Code FROM @t WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_IndexedTempTableInsideProcedure_ReportsIndexedTrue()
    {
        // A genuinely pre-existing bug found while wiring up TVPs (coverage-remediation-plan.md
        // Phase 3.2): ResolveColumnOperand's index lookup called the UNSCOPED catalog.Find, but
        // a #temp table/table variable is cataloged under a key scoped to its enclosing
        // procedure - so an indexed #temp table or table variable ALWAYS silently reported
        // Indexed=false, for every proc, not just TVPs. Fixed by passing _currentProcScope
        // through (safe for a real persistent table too - DatabaseCatalog falls back to the
        // unscoped lookup automatically).
        var findings = Extract(
            """
            CREATE PROCEDURE dbo.usp_UseIndexedTemp
            AS
            BEGIN
                CREATE TABLE #t (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                CREATE INDEX IX_t_Code ON #t(Code);
                SELECT Code FROM #t WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_MultiStatementTvfBody_PredicateAgainstOwnReturnVariable_Resolves()
    {
        // RETURNS @t TABLE(...) is a DeclareTableVariableBody hanging off the return type, not a
        // DeclareTableVariableStatement, so @t was never cataloged and a predicate inside the
        // body over FROM @t resolved to no known table (coverage-remediation-plan.md Phase 3.4).
        //
        // Not oracle-round-tripped: this predicate only exists inside a multi-statement TVF's
        // own body, evaluated against rows this same function body INSERTs into its return
        // variable - there is no way to compile-only probe it without actually invoking the
        // function (which requires the INSERT INTO @t that feeds it to run for real, i.e. DML
        // execution - CLAUDE.md's hard scope forbids that anywhere outside a self-authored
        // Docker probe). The ScanForced verdict itself is already oracle-confirmed by the
        // simpler table-vs-literal cases in TypedPredicateExtractorOracleTests; this test's own
        // job is proving the return-variable table gets cataloged and resolves at all.
        var findings = Extract(
            """
            CREATE FUNCTION dbo.fn_GetCodes()
            RETURNS @t TABLE (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL)
            AS
            BEGIN
                INSERT INTO @t (Code) SELECT Code FROM dbo.Orders;
                DELETE FROM @t WHERE Code = N'x';
                RETURN;
            END
            """,
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);");

        var finding = Assert.Single(findings);
        Assert.Equal("@t", finding.Column.TableQualifiedName);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_TableValuedParameterInFromClause_Resolves()
    {
        // CREATE TYPE ... AS TABLE (a table-valued parameter's declared shape) had no visitor
        // anywhere - WWI's manifest lists four such files, each consumed as a TVP by a real proc
        // (coverage-remediation-plan.md Phase 3.2). This mirrors that exact shape: a table type
        // with an inline INDEX, used as a READONLY procedure parameter.
        var findings = Extract(
            "CREATE TYPE Website.OrderLineList AS TABLE (StockItemID INT NOT NULL, INDEX IX_OrderLineList (StockItemID));",
            """
            CREATE PROCEDURE Website.InsertOrderLines
                @OrderLines Website.OrderLineList READONLY
            AS
            BEGIN
                SELECT StockItemID FROM @OrderLines WHERE StockItemID = 1;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@OrderLines", finding.Column.TableQualifiedName);
        Assert.Equal("StockItemID", finding.Column.ColumnName);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_ColumnWrappedInCoalesceComparedToLiteral_NoTypedFinding_ButLedgered()
    {
        // The bug this closes: a column wrapped in COALESCE (or CASE/NULLIF/IIF) resolves
        // through ResolveOperand's ExpressionTypeInferencer branch into a Value operand, not a
        // Column - so neither side of `WHERE COALESCE(Col, '') = N'x'` is a PredicateOperand.Column
        // and this used to hit a silent `return` with zero trace: no typed finding AND no ledger
        // entry, even though the enclosing context is a genuine WHERE clause and Tier-1 (a
        // completely separate pass) independently flags the exact same construct as a syntactic
        // FunctionWrappedColumn finding. The typed tier must leave its own trace too - "nothing
        // classified here" should always be a ledger entry somewhere, never silence.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE COALESCE(Col, '') = N'x';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "no column operand");
    }

    [Fact]
    public void Extract_InSubqueryWithMultipleOutputColumns_ResolvesUnknownAndLedgers_NotWrongColumn()
    {
        // ResolveInSubqueryType used to take columns[0] unconditionally regardless of how many
        // columns the subquery actually resolved to - a genuinely multi-column subquery would
        // silently type the IN comparison off whichever column happened to resolve first, with
        // no check or ledger trace. `IN (SELECT ...)` only has a well-defined single output
        // column when there IS exactly one; more than one is Unknown, not a guess.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Other (A INT NOT NULL, B INT NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN (SELECT A, B FROM dbo.Other);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "IN predicate");
    }

    [Fact]
    public void Extract_CteThenInsertSelect_PredicateThroughCteClassifiesLikeBareSelect()
    {
        // ExplicitVisit(InsertStatement) pushes CTE scope exactly like SelectStatement/
        // UpdateStatement/DeleteStatement/MergeStatement do - covering the shape that used to be
        // the tool's own named gap: `WITH cte AS (...) INSERT INTO t SELECT ... FROM cte WHERE
        // ...` losing the predicate because the CTE was invisible to the INSERT's own SELECT
        // source. Asserts parity with the equivalent bare SELECT, not just "a finding exists".
        var viaInsert = ExtractAll(
            "CREATE TABLE dbo.Source (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Target (Code VARCHAR(20) NOT NULL);",
            """
            WITH SourceCte AS (SELECT Code FROM dbo.Source)
            INSERT INTO dbo.Target (Code)
            SELECT Code FROM SourceCte WHERE Code = N'x';
            """).TypedFindings;

        var viaBareSelect = ExtractAll(
            "CREATE TABLE dbo.Source (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            WITH SourceCte AS (SELECT Code FROM dbo.Source)
            SELECT Code FROM SourceCte WHERE Code = N'x';
            """).TypedFindings;

        var insertFinding = Assert.Single(viaInsert);
        var selectFinding = Assert.Single(viaBareSelect);
        Assert.Equal("dbo.Source", insertFinding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, insertFinding.Verdict);
        Assert.Equal(selectFinding.Verdict, insertFinding.Verdict);
        Assert.Equal(selectFinding.Column.Depth, insertFinding.Column.Depth);
    }

    [Fact]
    public void Extract_PredicateAgainstUntraceableExpressionDerivedColumn_NoFindingButLedgered()
    {
        // The bug this closes: RecordExpressionDerivedFinding's own "no traceable base column
        // underneath" branch (ROW_NUMBER(), a derived-table alias over another opaque
        // expression) returned with zero trace - true that it's expression-derived, but nothing
        // actionable to report, so no ExpressionDerivedFinding fired; that decision itself was
        // never ledgered, unlike every other "nothing to classify here" branch in this pass.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            """
            SELECT rn FROM (
                SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn FROM dbo.T
            ) AS x
            WHERE x.rn = 1;
            """);

        Assert.Empty(result.ExpressionDerivedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "expression-derived predicate" && s.Reason.Contains("rn", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_InListWithNonLiteralElement_RecordsSkipInsteadOfGuessing()
    {
        // Roadmap Phase B: arithmetic (Other + 1) is now typeable through the shared
        // ExpressionTypeInferencer, so the genuinely still-unresolvable element here is a
        // scalar function call with no return-type registry entry (never declared) - the
        // remaining real "can't type this" case, not a guess.
        var findings = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Other INT NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN (1, dbo.fn_NeverDeclared());");

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "IN predicate");
    }

    [Fact]
    public void Extract_NotInList_IsNotAttributedToTypeConversionVerdict()
    {
        // Oracle-verified: NOT IN scans the index regardless of type match (a matching-type
        // NOT IN list still produces an Index Scan, where the equivalent IN seeks) - fixing the
        // type mismatch would not make this predicate seek, so it's not routed through the
        // type-conversion verdict machinery at all. No typed finding, but recorded in the skip
        // ledger rather than silently dropped.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT IN (N'a', N'b');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT IN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ComparisonBetweenTwoUnresolvedColumnReferences_LedgersDistinctlyFromNoColumnOperand()
    {
        // Both sides ARE bare column references syntactically (r.A, r.B) - unlike the genuinely
        // benign "no column operand" shape (both sides are expressions), this is a real analysis
        // gap: the alias resolved (it's in ByAlias), but its relation is empty because OPENQUERY
        // is an unsupported table reference kind, so every column lookup against it fails. Before
        // this fix, this landed in the exact same "no column operand" bucket as a harmless
        // `expr = expr` comparison, with no way to tell the two apart in the honesty numbers.
        var result = ExtractAll(
            "SELECT * FROM OPENQUERY(RemoteServer, 'SELECT A, B FROM Remote') AS r WHERE r.A = r.B;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "unresolved column comparison");
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "no column operand");
    }

    [Fact]
    public void Extract_ComparisonInsideCaseWhenBranchWithinWhere_NotAFinding_ButLedgered()
    {
        // The bug this closes: ScriptDom's default traversal still walks into a
        // SearchedCaseExpression's WhenClauses after ResolveOperand has already typed the CASE
        // as a whole, so `WHERE CASE WHEN Col = N'X' THEN 1 ELSE 0 END = 1` used to visit the
        // inner `Col = N'X'` with filter context still true (inherited from the enclosing
        // WHERE) and report it as an independent, verdict-bearing finding - a comparison the
        // optimizer never uses as a seek predicate. The outer `CASE(...) = 1` comparison is the
        // only real predicate here and must still classify normally.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE CASE WHEN Col = N'X' THEN 1 ELSE 0 END = 1;");

        // The outer `CASE(...) = 1` comparison has no column on either side (the CASE result is
        // a Value operand, and so is the literal 1), so it produces no typed finding of its own
        // either - that both-sides-non-column case is ledgered too now ("no column operand",
        // covered in its own test), not silent. The point under test here is narrower: the INNER
        // `Col = N'X'` must not produce a SECOND, spurious finding, and must leave a ledger
        // trace precisely because it looked like a real WHERE predicate.
        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(
            result.SkippedConstructs,
            s => s.ConstructKind == "comparison inside scalar expression" && s.Reason.Contains("CASE/IIF/COALESCE/NULLIF", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ComparisonInsideIifPredicateWithinWhere_NotAFinding_ButLedgered()
    {
        // Same leak, via IIF's own Predicate (a BooleanExpression, structurally identical to a
        // SearchedCaseExpression's WhenExpression).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE IIF(Col = N'X', 1, 0) = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_CaseInSelectList_StillSilentlyExcluded_NotLedgered()
    {
        // Near-miss for the fix above: a CASE branch's inner comparison that was NEVER inside an
        // active filter clause to begin with (a SELECT-list CASE) must stay silently excluded,
        // exactly as before - only a comparison that suspended a genuinely active filter context
        // gets the new ledger entry, or every SELECT-list CASE in the corpus would start
        // generating ledger noise.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT CASE WHEN Col = N'X' THEN 1 ELSE 0 END FROM dbo.T;");

        Assert.Empty(result.TypedFindings);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_MergeUpdateSetClauseWithCase_NoFindingFromSetClause_OnAndActionConditionStillFire()
    {
        // The MERGE half of the same bug class: the previous implementation held filter context
        // true across the ENTIRE MergeSpecification subtree (rationale: "no SELECT-list analog
        // inside a MergeSpecification") - wrong, since UpdateMergeAction.SetClauses IS exactly
        // that analog. `WHEN MATCHED THEN UPDATE SET t.Flag = CASE WHEN t.Code = N'x' THEN 1
        // ELSE 0 END` used to report a false finding for the inner `t.Code = N'x'`. The ON clause
        // and the action's own "AND <cond>" extra condition are genuine filter positions and
        // must keep firing.
        var result = ExtractAll(
            "CREATE TABLE dbo.TargetMergeCase (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Flag INT NOT NULL);",
            "CREATE TABLE dbo.SourceMergeCase (Id INT NOT NULL, Code NVARCHAR(20) NOT NULL);",
            """
            MERGE INTO dbo.TargetMergeCase AS t
            USING dbo.SourceMergeCase AS s
            ON t.Id = s.Id
            WHEN MATCHED AND t.Code = N'y' THEN UPDATE SET t.Flag = CASE WHEN t.Code = N'x' THEN 1 ELSE 0 END;
            """);

        // The action-clause condition (t.Code = N'y') fires; the SET clause's inner CASE
        // comparison (t.Code = N'x') must not produce a second, spurious finding for the same
        // column. Unlike the WHERE-clause case above, the SET clause was never itself a filter
        // position to begin with (an assignment target, structurally the same as a SELECT list),
        // so there is no active filter context to suspend - the inner comparison stays silently
        // excluded exactly like a SELECT-list CASE, not ledgered.
        var codeFindings = result.TypedFindings.Where(f => f.Column.ColumnName == "Code").ToList();
        var finding = Assert.Single(codeFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_NotExistsWithInnerComparison_ClassifiesNormally_NotNegated()
    {
        // _negated previously wasn't reset when descending into a nested QuerySpecification, so
        // `WHERE NOT EXISTS (SELECT ... WHERE a.x = b.y)` visited the inner `=` with _negated
        // still true from the outer NOT, wrongly negating it to `<>` and routing a genuinely
        // seekable predicate to the non-seekable-operator ledger skip instead of classifying it.
        // The NOT applies to the whole EXISTS(...), not to the subquery's own, independent
        // boolean structure.
        var result = ExtractAll(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Flags (CustomerId NVARCHAR(20) NOT NULL);",
            """
            SELECT 1 FROM dbo.Orders o
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Flags f WHERE f.CustomerId = o.CustomerId);
            """);

        var finding = Assert.Single(result.TypedFindings, f => f.Column.ColumnName == "CustomerId" && f.Column.TableQualifiedName == "dbo.Orders");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("<>", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotEqualsOperator_IsNotAttributedToTypeConversionVerdict()
    {
        // Oracle-verified: <> scans a string-family index regardless of type match. Covers both
        // the <> and != spellings (BooleanComparisonType.NotEqualToBrackets/
        // NotEqualToExclamation both map to the same operator text).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col <> N'a';",
            "SELECT Col FROM dbo.T WHERE Col != N'a';");

        Assert.Empty(result.TypedFindings);
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("<>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extract_NotLikePredicate_IsNotAttributedToTypeConversionVerdict()
    {
        // Oracle-verified: NOT LIKE scans a string-family index regardless of type match, even
        // for a non-leading-wildcard pattern that the equivalent LIKE would seek through.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT LIKE N'a%';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT LIKE", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedEqualsComparison_IsTreatedAsNotEqual_NotAsEquals()
    {
        // Roadmap Phase E2: WHERE NOT (Col = @p) is semantically <> (a materially different,
        // oracle-verified-non-sargable comparison), not = - before this fix, the enclosing NOT
        // was invisible to TypedPredicateExtractor entirely and this reported an ordinary =
        // finding, a wrong verdict rather than a missing one.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col = N'a');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("<>", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedNotEqualsComparison_IsTreatedAsEquals()
    {
        // The other direction: NOT (Col <> @p) is semantically =, and DOES classify normally -
        // proves this is a genuine polarity inversion, not just "NOT always suppresses".
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col <> N'a');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("=", finding.Operator);
    }

    [Fact]
    public void Extract_DoubleNotWrappedEqualsComparison_ResolvesBackToEquals()
    {
        // NOT NOT X == X - proves negation toggles by parity, not by "any enclosing NOT flips it".
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (NOT (Col = N'a'));");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("=", finding.Operator);
    }

    [Fact]
    public void Extract_NotWrappedLikePredicate_IsNotAttributedToTypeConversionVerdict()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col LIKE N'a%');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator");
    }

    [Fact]
    public void Extract_NotWrappedInPredicate_IsNotAttributedToTypeConversionVerdict()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col IN (N'a', N'b'));");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator");
    }

    [Fact]
    public void Extract_NotBetweenKeyword_IsNotAttributedToTypeConversionVerdict()
    {
        // Roadmap Phase E2: oracle-verified directly - `Col NOT BETWEEN 'a' AND N'z'` produces
        // an Index Scan (both comparisons OR'd together), even when both bounds already match
        // the column's own type - non-sargable regardless of type match, previously
        // misclassified as if it were a plain BETWEEN (`>=`/`<=` findings for a materially
        // different predicate - a wrong verdict, not a missing one).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT BETWEEN N'a' AND N'z';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT BETWEEN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedBetween_IsTreatedTheSameAsNotBetweenKeyword()
    {
        // NOT (Col BETWEEN x AND y) is the same predicate as Col NOT BETWEEN x AND y under
        // different syntax - deliberately deferred when the NOT-polarity fix landed, closed here
        // once NOT BETWEEN's own oracle-verified behavior was established.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col BETWEEN N'a' AND N'z');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT BETWEEN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DoubleNotWrappedBetween_ClassifiesAsOrdinaryBetween()
    {
        // NOT NOT X == X - proves this follows the same negation-parity model as every other
        // wrapped predicate, not a special case.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (NOT (Col BETWEEN N'a' AND N'z'));");

        Assert.Equal(2, result.TypedFindings.Count);
        Assert.All(result.TypedFindings, f => Assert.Equal(Verdict.ScanForced, f.Verdict));
    }

    [Fact]
    public void Extract_IsNullPredicate_ProducesNoFindingAndNoLedgerNoise()
    {
        // Roadmap Phase E2: IS NULL is its own distinct SQL operation, not a value comparison -
        // no CONVERT_IMPLICIT is possible, so this must produce neither a typed finding nor a
        // ledger entry (a construct that's genuinely handled, not one that was dropped).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NULL);",
            "SELECT Col FROM dbo.T WHERE Col IS NULL;",
            "SELECT Col FROM dbo.T WHERE Col IS NOT NULL;");

        Assert.Empty(result.TypedFindings);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.Reason.Contains("IS NULL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_EqualsAnySubquery_ClassifiesLikeInSubquery()
    {
        // Roadmap Phase E2: oracle-verified to produce the identical CONVERT_IMPLICIT signature
        // as the equivalent IN (subquery) form.
        var result = ExtractAll(
            """
            CREATE TABLE dbo.T (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.U (Code NVARCHAR(20) NOT NULL);
            """,
            "SELECT Code FROM dbo.T WHERE Code = ANY (SELECT Code FROM dbo.U);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("IN", finding.Operator);
    }

    [Fact]
    public void Extract_EqualsSomeSubquery_ClassifiesTheSameAsEqualsAny()
    {
        // SOME is a pure syntactic synonym for ANY - ScriptDOM itself normalizes it to the same
        // SubqueryComparisonPredicateType.Any enum value.
        var result = ExtractAll(
            """
            CREATE TABLE dbo.T (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.U (Code NVARCHAR(20) NOT NULL);
            """,
            "SELECT Code FROM dbo.T WHERE Code = SOME (SELECT Code FROM dbo.U);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_NotEqualsAllSubquery_IsNotAttributedToTypeConversionVerdict()
    {
        // Oracle-verified: <> ALL scans regardless of type match, same as NOT IN - not routed
        // through the type-conversion verdict machinery at all.
        var result = ExtractAll(
            """
            CREATE TABLE dbo.T (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.U (Code NVARCHAR(20) NOT NULL);
            """,
            "SELECT Code FROM dbo.T WHERE Code <> ALL (SELECT Code FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("<> ALL", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_GreaterThanAnySubquery_IsLedgeredNotModeled()
    {
        // A range comparison against a whole result set - deliberately not modeled (not the
        // same shape as = ANY/<> ALL), ledgered rather than guessed.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Amount INT NOT NULL); CREATE TABLE dbo.U (Amount INT NOT NULL);",
            "SELECT Amount FROM dbo.T WHERE Amount > ANY (SELECT Amount FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "subquery comparison predicate");
    }

    [Fact]
    public void Extract_ColumnComparedToScalarSubquery_ResolvesSubqueryOutputColumnType()
    {
        // `col = (SELECT x FROM ...)` used to fall to ResolveOperand's default arm - no case
        // at all for a bare ScalarSubquery, unlike the dedicated IN/= ANY/<> ALL machinery -
        // so the other side stayed permanently untyped (an operand-type-unresolved Unknown)
        // even when the subquery's own single output column resolved just fine through
        // lineage. Reuses that exact resolution (ResolveInSubqueryType).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Amount INT NOT NULL); CREATE TABLE dbo.Settings (SettingValue INT NOT NULL, SettingId INT NOT NULL);",
            "SELECT Amount FROM dbo.T WHERE Amount = (SELECT SettingValue FROM dbo.Settings WHERE SettingId = 1);");

        var finding = Assert.Single(result.TypedFindings, f => f.Column.TableQualifiedName == "dbo.T");
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.Equal(SqlTypeCategory.Int, ((PredicateOperand.Value)finding.OtherOperand).Type!.Category);
    }

    [Fact]
    public void Extract_ColumnComparedToMultiColumnScalarSubquery_StaysUnknownNotAGuess()
    {
        // A genuinely multi-column subquery has no single well-defined output type - stays
        // Unknown, same as the IN-subquery machinery's own multi-column decline.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Amount INT NOT NULL); CREATE TABLE dbo.Wide (A INT NOT NULL, B INT NOT NULL);",
            "SELECT Amount FROM dbo.T WHERE Amount = (SELECT A, B FROM dbo.Wide);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }

    [Fact]
    public void Extract_TriggerBody_InsertedPseudoTable_ResolvesToTargetTableColumn()
    {
        // INSERTED is a pseudo-table that only exists inside a real trigger firing on a real DML
        // statement - this project never executes DML (CLAUDE.md hard scope), so there is no
        // plan to capture here; CREATE TRIGGER is also not on DdlStatementWhitelist, so the
        // trigger itself can't even be deployed standalone. The ScanForced verdict correctness
        // is already covered by the oracle-confirmed VerdictClassifier/type-matrix tests
        // elsewhere in this project - this test's own job is lineage resolution (does
        // inserted.Code trace back to dbo.Orders.Code).
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_TriggerBody_InsertedPseudoTable_DoesNotInheritTargetTablesRealIndex()
    {
        // inserted/deleted are a version-store rowset, not the real table - even when Code IS
        // indexed on dbo.Orders, a predicate against inserted.Code must report Indexed=false
        // (coverage-remediation-plan.md Phase 1.1). Before this fix, inserted/deleted resolved
        // through the ordinary BaseColumn path and wrongly inherited the real table's index,
        // which would have ranked this finding first under CLAUDE.md's ranking rule despite not
        // being a real index-killing conversion.
        //
        // No oracle round-trip for the inserted-pseudo-table half of this test: same reasoning
        // as the test above (no DML execution, CREATE TRIGGER not whitelisted DDL).
        var findings = Extract(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE INDEX IX_Orders_Code ON dbo.Orders(Code);
            """,
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // The direct FROM-clause case must be unaffected by this fix - the real table still
        // reports its real index.
        var directFindings = Extract(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE INDEX IX_Orders_Code ON dbo.Orders(Code);
            """,
            "SELECT Code FROM dbo.Orders WHERE Code = N'x';");

        var directFinding = Assert.Single(directFindings);
        Assert.True(directFinding.Column.Indexed);
    }

    [Fact]
    public void Extract_TriggerBody_DeletedPseudoTableWithAlias_Resolves()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER DELETE
            AS
            BEGIN
                IF EXISTS (SELECT 1 FROM deleted d WHERE d.Code = N'x') RETURN;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void Extract_AlterTriggerBody_InsertedPseudoTable_Resolves()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS BEGIN RETURN; END",
            """
            ALTER TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void Extract_CreateOrAlterTriggerBody_InsertedPseudoTable_Resolves()
    {
        // CreateOrAlterTriggerStatement is a distinct ScriptDOM node type from
        // CreateTriggerStatement/AlterTriggerStatement - procedures and functions already got
        // all three variants; triggers didn't (coverage-remediation-plan.md Phase 2.1).
        //
        // No oracle round-trip: INSERTED only exists inside a real trigger firing on a real DML
        // statement, which this project never executes (CLAUDE.md hard scope), and CREATE OR
        // ALTER TRIGGER is not on DdlStatementWhitelist either. The ScanForced verdict itself is
        // already oracle-confirmed elsewhere; this test's own job is proving the CREATE OR ALTER
        // spelling gets the same trigger-body handling as CREATE/ALTER.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE OR ALTER TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InsteadOfTriggerBody_OnTable_InsertedPseudoTable_Resolves()
    {
        // INSTEAD OF triggers on a table target take the identical resolution path AFTER
        // triggers do - TriggerType is never read anywhere in this pass, so this only works by
        // omission rather than by design (docs/coverage-remediation-plan.md Phase 5). This test
        // is what turns that "works by omission" claim into something checked, so a future
        // change that starts branching on TriggerType cannot silently break it.
        //
        // No oracle round-trip: same reasoning as the other trigger-body tests (no DML
        // execution, CREATE TRIGGER not whitelisted DDL) - the ScanForced verdict itself is
        // already oracle-confirmed elsewhere.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders
            INSTEAD OF INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InsteadOfTriggerBody_OnView_InsertedPseudoTable_Resolves()
    {
        // DatabaseCatalog holds no views - BuildTriggerPseudoTableRelations used to call
        // catalog.Find only, so an INSTEAD OF trigger on a view dropped every predicate with the
        // misleading reason "has no known DDL" while the view sat fully resolved in resolvedViews
        // (coverage-remediation-plan.md Phase 3.3).
        //
        // No oracle round-trip: same reasoning as the other trigger-body tests.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT Code FROM dbo.Orders;",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.vw_Orders
            INSTEAD OF INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        // Attributed to the trigger's literal target (the view), not chased through to the
        // ultimate base table - matches the table case, where TableQualifiedName is already the
        // trigger's own target.
        Assert.Equal("dbo.vw_Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InsteadOfTriggerBody_OnView_InsertedPseudoTable_DoesNotClaimTheBaseColumnsIndex()
    {
        // Same wrong-answer risk as the table case (Phase 1.1): even though dbo.Orders.Code IS
        // indexed and the view passes it straight through (a real SELECT against the view could
        // seek), inserted on this INSTEAD OF trigger is not a query against real rows - it must
        // not inherit that index.
        var findings = Extract(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE INDEX IX_Orders_Code ON dbo.Orders(Code);
            """,
            "CREATE VIEW dbo.vw_Orders AS SELECT Code FROM dbo.Orders;",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.vw_Orders
            INSTEAD OF INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_TriggerBody_InsertedVisibleInsideNestedSubquery()
    {
        // inserted/deleted are visible throughout the whole trigger body, not just a single
        // top-level SELECT - the same CTE-style scope chain used elsewhere in this pass.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders
            AFTER INSERT
            AS
            BEGIN
                IF EXISTS (SELECT 1 FROM (SELECT Code FROM inserted) AS i WHERE i.Code = N'x') RETURN;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void Extract_TriggerBody_TargetTableNotInCatalog_RecordsSkipInsteadOfGuessing()
    {
        var findings = ExtractAll(
            """
            CREATE TRIGGER dbo.trg_Ghost ON dbo.Ghost
            AFTER INSERT
            AS
            BEGIN
                SELECT Code FROM inserted WHERE Code = N'x';
            END
            """);

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "trigger inserted/deleted");
    }

    [Fact]
    public void Extract_DdlTrigger_OnDatabase_DoesNotThrowAndLedgersInsteadOfGuessing()
    {
        // Reproduced before this fix (coverage-remediation-plan.md Phase 0.3): TriggerObject.Name
        // is null for a DDL trigger (no target table - it fires on database-level DDL events, not
        // rows), and the trigger visitor used to dereference it unconditionally, taking down the
        // whole scan on the first DDL trigger it encountered.
        var findings = ExtractAll(
            """
            CREATE TRIGGER trg_DdlAudit
            ON DATABASE
            FOR CREATE_TABLE
            AS
            BEGIN
                DECLARE @x INT;
            END
            """);

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "DDL/LOGON trigger");
    }

    [Fact]
    public void Extract_LogonTrigger_DoesNotThrowAndLedgersInsteadOfGuessing()
    {
        var findings = ExtractAll(
            """
            CREATE TRIGGER trg_LogonAudit
            ON ALL SERVER
            FOR LOGON
            AS
            BEGIN
                DECLARE @x INT = 1;
            END
            """);

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "DDL/LOGON trigger");
    }

    [Fact]
    public void Extract_DdlTriggerBody_RealPredicateAgainstRealTable_StillAnalyzed()
    {
        // A DDL trigger has no inserted/deleted, but its body can still contain ordinary
        // predicates against real tables - those must not be lost just because the trigger
        // itself has no target.
        //
        // No oracle round-trip: CREATE TRIGGER (DDL-trigger form included) is not on
        // DdlStatementWhitelist at all, so this predicate's containing statement can't be
        // deployed standalone even though the predicate itself is an ordinary column-vs-literal
        // comparison with nothing trigger-specific about it. The ScanForced verdict for exactly
        // this shape (VarChar column vs NVarChar literal, SQL collation) is already
        // oracle-confirmed by TypedPredicateExtractorOracleTests; this test's own job is proving
        // a DDL trigger body doesn't get skipped wholesale just because it has no pseudo-tables.
        var findings = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE TRIGGER trg_DdlAudit ON DATABASE FOR CREATE_TABLE
            AS
            BEGIN
                SELECT Col FROM dbo.T WHERE Col = N'x';
            END
            """);

        var finding = Assert.Single(findings.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnComparedToFunctionCall_ResolvesUnknownAndLedgersOperand()
    {
        // No return-type registry entry exists for a scalar UDF (BuiltinFunctionTypeResolver is
        // a curated allowlist of built-in functions only) - the right side resolves Unknown,
        // same as before this pass, but now it's counted instead of silently falling through
        // the default switch arm. Unknown makes no claim about engine behavior, so nothing to
        // oracle-confirm.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col = dbo.fn_DisplayName(1);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("fn_DisplayName", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ColumnComparedToUnknownGlobalVariable_ResolvesUnknownAndLedgers()
    {
        // @@REMSERVER is a genuinely obscure global variable this curated table doesn't cover -
        // @@CURSOR_ROWS used to be this test's own example until it turned out to just be a
        // missing table entry (oracle-verified and added to BuiltinFunctionTypeResolver).
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Total INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Total = @@REMSERVER;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("@@REMSERVER", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ColumnComparedToConvertToNvarcharOfVarcharColumn_PropagatesInputCollation()
    {
        // Mirrors Pass 2's identical collation propagation (ScalarExpressionResolver): CAST/
        // CONVERT to a string type has no inline COLLATE syntax, and the real engine propagates
        // the input's own collation into the result. Code carries its OWN explicit (different)
        // collation so ClassifySameCategory's null-collation short-circuit can't fire on either
        // side - only then does a genuinely-different-collation OperandClash verdict prove the
        // CONVERT result's collation actually came from Value, not from being left uncollated.
        // Oracle-verified directly (Docker SQL Server): this exact shape does not compile at all
        // (Msg 468, "Cannot resolve the collation conflict between SQL_Latin1_General_CP1_CI_AS
        // and Latin1_General_CI_AS") - a CAST/CONVERT result with no COLLATE of its own carries
        // its source column's "implicit" coercibility tier, and comparing two differing
        // "implicit" collations is a compile failure, not a silent convert. This used to assert
        // Unknown (an admitted, unverified guess); now a confirmed compile failure.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Code NVARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Raw (Value VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T, dbo.Raw WHERE Code = CONVERT(NVARCHAR(20), Value);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.OperandClash, finding.Verdict);
    }

    [Fact]
    public void Extract_PredicateAgainstLegacySysobjectsCompatibilityView_ColumnSideConverts()
    {
        // Bare "sysobjects" (no schema) qualifies to dbo.sysobjects, a DIFFERENT column shape
        // than sys.objects (id/xtype/type, not object_id/type_desc) - DNN Platform's incremental
        // upgrade scripts use exactly this legacy form throughout. xtype is CHAR(2)
        // (oracle-verified); its collation is deliberately left unresolved by the registry
        // (never guessed), so a cross-category comparison against NVARCHAR correctly reaches
        // Unknown rather than either being skipped (the old behavior) or a guessed verdict.
        // Unknown is a claim about our own uncertainty, not the engine's behavior - nothing to
        // oracle-confirm.
        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Find @T NVARCHAR(2) AS BEGIN SELECT name FROM sysobjects WHERE xtype = @T; END");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("dbo.sysobjects", finding.Column.TableQualifiedName);
        Assert.Equal("xtype", finding.Column.ColumnName);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference");
    }

    [Fact]
    public void Extract_PredicateAgainstUnregisteredSystemView_StillRecordsSkip()
    {
        // A system view genuinely not in the curated registry (e.g. a DMV) must still record
        // the honest "no known DDL" skip, not silently resolve to nothing.
        var result = ExtractAll(
            "SELECT session_id FROM sys.dm_exec_requests WHERE session_id = 1;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference" && s.Reason.Contains("sys.dm_exec_requests", StringComparison.Ordinal));
    }

    // docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #2. Deliberately
    // NOT verdict-bearing (see OversizedParameterFinding's own doc comment): oracle-probed
    // directly (Docker SQL Server, populated table) that a bare equality predicate against an
    // oversized parameter shows no memory-grant difference in its own plan - the risk is
    // structural (the value's declared size feeding a sort/hash operator elsewhere), not a
    // seek/scan claim about THIS predicate. So these are catalog/AST-structural tests, no oracle
    // probe attached, matching how the finding is actually reported. The pattern itself is the
    // one Paul White (sqlperformance.com, "Performance Myths: Oversizing String Columns") and
    // Brent Ozar ("Would You Just Look at the Execution Plan?" memory-grant series) both warn
    // against: declaring a parameter/variable wider than the column it's compared to costs
    // nothing on this predicate alone, but the same wide declaration elsewhere sizes memory
    // grants off the parameter's own length, not the column's.

    [Fact]
    public void Extract_ColumnComparedToLongerDeclaredVariable_FiresOversizedParameter()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(200) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        var finding = Assert.Single(result.OversizedParameterFindings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal(20, finding.ColumnLength);
        Assert.Equal(200, finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ProcedureParameterLongerThanColumn_FiresOversizedParameter()
    {
        // The realistic shape: a stored procedure's own formal parameter, declared wider than
        // the column it filters, exactly the "just make it NVARCHAR(MAX) to be safe" habit both
        // cited articles call out.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "CREATE PROCEDURE dbo.usp_FindCustomer @Code VARCHAR(4000) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @Code; END");

        var finding = Assert.Single(result.OversizedParameterFindings);
        Assert.Equal(20, finding.ColumnLength);
        Assert.Equal(4000, finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterOrEqualDeclaredVariable_NeverFires()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(20) = 'ABC'; DECLARE @q VARCHAR(5) = 'AB'; SELECT 1 FROM dbo.Customers WHERE Code = @p OR Code = @q;");

        Assert.Empty(result.OversizedParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToLongerLiteral_NeverFires()
    {
        // A literal's own length is its actual content length, not a "declared" one distinct
        // from the column - only a real variable/parameter/expression carries a size
        // independent of its current value, so literals are excluded outright.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(5) NOT NULL);",
            "SELECT 1 FROM dbo.Customers WHERE Code = 'a much longer literal than the column';");

        Assert.Empty(result.OversizedParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToLongerMaxTypedVariable_NeverFires()
    {
        // MAX-typed is item #1's own separate finding (MaxTypedColumnScanner) - a declared
        // length of -1 here would falsely read as "shorter than the column", so MAX-typed
        // operands are excluded from this check explicitly rather than by coincidence.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(MAX) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.OversizedParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToLongerVariableOfDifferentCategory_NeverFires()
    {
        // A category mismatch (VARCHAR column vs NVARCHAR variable) is the implicit-conversion
        // stream's own, already-covered concern - this check only fires within the SAME string
        // category, where length is the only thing that differs.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p NVARCHAR(200) = N'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.OversizedParameterFindings);
    }

    // docs/detection-checklist.md Tier 1 "Under-length and length-defaulted string declarations" -
    // the exact mirror of the oversized-parameter tests above. Deliberately NOT verdict-bearing,
    // same reasoning: this pass never traces the variable's actual assigned VALUE, so it cannot
    // claim truncation DID happen for a specific query, only that the declared-length pairing
    // risks it - the same honesty WriteLossFinding already applies to assignment-site truncation.
    // Real-world source for the "bare-length declaration defaults to 1" gotcha: Erland
    // Sommarskog's widely-cited parameter-sizing writing
    // (https://www.sommarskog.se/dynamic_sql.html and his general T-SQL error-handling series)
    // repeatedly calls out the length-1 default as a common, easy-to-miss accident distinct from
    // CAST/CONVERT's own length-30 default for the same bare spelling.

    [Fact]
    public void Extract_ColumnComparedToShorterDeclaredVariable_FiresUnderLengthParameter()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(5) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal(20, finding.ColumnLength);
        Assert.Equal(5, finding.OtherOperandLength);
        Assert.False(finding.IsImplicitDefault);
        Assert.Equal("=", finding.Operator);
        Assert.False(finding.ChangesRangeOrPatternShape);
    }

    [Fact]
    public void Extract_ProcedureParameterShorterThanColumn_FiresUnderLengthParameter()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "CREATE PROCEDURE dbo.usp_FindCustomer @Code VARCHAR(5) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @Code; END");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal(20, finding.ColumnLength);
        Assert.Equal(5, finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ColumnComparedToVariableWithNoExplicitLength_FiresImplicitDefault()
    {
        // T-SQL defaults a length-less DECLARE to 1 - a near-universal accident, not an
        // intentional choice, and distinct from the shorter-but-explicit case above (no declared
        // length to report, so OtherOperandLength stays null).
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR = 'A'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.True(finding.IsImplicitDefault);
        Assert.Null(finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterVariableInLikePredicate_ChangesPatternShape()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(3) = 'AB%'; SELECT 1 FROM dbo.Customers WHERE Code LIKE @p;");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal("LIKE", finding.Operator);
        Assert.True(finding.ChangesRangeOrPatternShape);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterVariableInRangeComparison_ChangesRangeShape()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(5) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code > @p;");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal(">", finding.Operator);
        Assert.True(finding.ChangesRangeOrPatternShape);
    }

    [Fact]
    public void Extract_ColumnComparedToEqualOrLongerDeclaredVariable_NeverFiresUnderLength()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(20) = 'ABC'; DECLARE @q VARCHAR(50) = 'AB'; SELECT 1 FROM dbo.Customers WHERE Code = @p OR Code = @q;");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterLiteral_NeverFiresUnderLength()
    {
        // A literal's own length is its actual content length, not a "declared" one distinct
        // from the column - only a real variable/parameter/expression carries a size independent
        // of its current value, so literals are excluded outright, same as the oversized case.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.Customers WHERE Code = 'x';");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterMaxTypedVariable_NeverFiresUnderLength()
    {
        // MAX-typed is never "shorter" - a length of -1 would falsely read that way, so MAX-typed
        // operands are excluded here too, symmetric with the oversized case's own guard.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(MAX) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterVariableOfDifferentCategory_NeverFiresUnderLength()
    {
        // A category mismatch (VARCHAR column vs NVARCHAR variable) is the implicit-conversion
        // stream's own, already-covered concern - this check only fires within the SAME string
        // category, where length is the only thing that differs.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p NVARCHAR(5) = N'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    // docs/detection-checklist.md "Small precise adds", "Explicit-length audit of CAST/CONVERT to
    // a string type" - the expression-side companion to the DECLARE case above, sharing the exact
    // same UnderLengthParameterFinding/OversizedParameterFinding comparison and reporting path
    // (no new finding type). Oracle-confirmed the underlying mechanism directly in
    // CastConvertUnsizedLengthOracleTests: an unsized CAST/CONVERT to a string/binary-family type
    // truncates to 30 characters, never length 1 - a materially different default than the bare
    // DECLARE case above, so IsImplicitDefault must read false here (30 is a real, resolved
    // length, not the DECLARE case's "no length at all" signal).

    [Fact]
    public void Extract_ColumnComparedToUnsizedConvert_FiresUnderLengthParameterAtLength30()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(40) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CONVERT(VARCHAR, @x);");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal(40, finding.ColumnLength);
        Assert.Equal(30, finding.OtherOperandLength);
        Assert.False(finding.IsImplicitDefault);
    }

    [Fact]
    public void Extract_ColumnComparedToUnsizedCast_FiresUnderLengthParameterAtLength30()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(40) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CAST(@x AS VARCHAR);");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal(30, finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ColumnComparedToUnsizedConvert_ColumnShorterThan30_NeverFiresUnderLength()
    {
        // The column is already narrower than CONVERT's own 30-character default - no
        // truncation risk in this direction (that's the oversized-parameter case's own concern).
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CONVERT(VARCHAR, @x);");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToExplicitlySizedConvert_UsesTheExplicitLengthNot30()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(40) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CONVERT(VARCHAR(10), @x);");

        var finding = Assert.Single(result.UnderLengthParameterFindings);
        Assert.Equal(10, finding.OtherOperandLength);
    }

    [Fact]
    public void Extract_ColumnComparedToUnsizedConvertOfNarrowerColumn_FiresOversizedParameter()
    {
        // Symmetric with the under-length cases above: CONVERT's own 30-character default is
        // WIDER than a narrower compared column - the oversized-parameter sibling's own concern.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(10) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CONVERT(VARCHAR, @x);");

        var finding = Assert.Single(result.OversizedParameterFindings);
        Assert.Equal(10, finding.ColumnLength);
        Assert.Equal(30, finding.OtherOperandLength);
    }

    // docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
    // "ANSI_PADDING OFF as a second, independent finding". Catalog fixtures here set
    // IsAnsiPadded directly (this is live-mode-only in real scans - sys.columns.is_ansi_padded
    // has no file-mode DDL equivalent this codebase parses, per CatalogColumn's own doc comment),
    // mirroring CrossTableTypeDriftScannerTests' own "build the catalog directly" pattern for a
    // live-only fact. Oracle-confirmed mechanism (real seeded rows, real query execution) lives
    // in AnsiPaddingMismatchOracleTests; these are structural/AST tests for the extraction logic.

    private static IReadOnlyList<AnsiPaddingMismatchFinding> ExtractAnsiPaddingMismatch(bool isAnsiPadded, string sql)
    {
        var ddl = "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Customers", CatalogTableKind.Table,
            [new CatalogColumn("Code", new SqlType(SqlTypeCategory.VarChar, Length: 20), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false, IsAnsiPadded: isAnsiPadded)],
            [], SourcePath: "test.sql", SourceLine: 1));

        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage).AnsiPaddingMismatchFindings;
    }

    [Fact]
    public void Extract_NonPaddedColumnLikeTrailingWhitespaceLiteral_Fires()
    {
        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code LIKE 'abc ';");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("'abc '", finding.PatternLiteralText);
    }

    [Fact]
    public void Extract_NonPaddedColumnLikePatternEndingInWildcardAfterSpace_NeverFires()
    {
        // 'abc %' ends in the wildcard '%', not whitespace - the detection deliberately checks
        // only the literal's OWN final character (matching its own doc comment's narrow, exactly-
        // provable scope), not "whitespace anywhere before a trailing wildcard", which would need
        // wildcard-aware pattern parsing this stream doesn't attempt. A real gap (this pattern's
        // significant internal space still can't match a non-padded column), but a deliberately
        // uncaught one rather than an overreaching heuristic.
        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code LIKE 'abc %';");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_PaddedColumn_NeverFires()
    {
        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: true, "SELECT 1 FROM dbo.Customers WHERE Code LIKE 'abc ';");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_NonPaddedColumnLikePatternWithNoTrailingWhitespace_NeverFires()
    {
        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code LIKE 'abc%';");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_NonPaddedColumnEqualityAgainstTrailingWhitespaceLiteral_NeverFires()
    {
        // Oracle-confirmed (this finding's own doc comment): plain equality is NOT affected by
        // ANSI_PADDING or trailing whitespace either way - T-SQL trims trailing spaces for '='
        // regardless. Scoped to LIKE only; must never fire on '='.
        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code = 'abc ';");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_NonPaddedColumnLikeAgainstVariable_NeverFires()
    {
        // Only a LITERAL pattern's own trailing whitespace is statically knowable - a variable's
        // actual runtime value is never traced (CLAUDE.md "soundness first"), so this must not
        // fire against a non-literal LIKE pattern.
        var findings = ExtractAnsiPaddingMismatch(
            isAnsiPadded: false, "DECLARE @p VARCHAR(20) = 'abc '; SELECT 1 FROM dbo.Customers WHERE Code LIKE @p;");

        Assert.Empty(findings);
    }

    // docs/detection-checklist.md Tier 2 "Local-variable predicates" - a predicate against a
    // DECLARE'd local variable's value is invisible to the cardinality estimator, unlike a
    // formal parameter's sniffed value. Purely structural/informational (no estimate magnitude
    // claimed); the general mechanism is oracle-confirmed once in a dedicated Verify-side test,
    // not per finding, matching this session's own precedent for this class of claim.

    [Fact]
    public void Extract_ColumnComparedToDeclaredLocalVariable_FiresLocalVariablePredicate()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @v VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @v;");

        var finding = Assert.Single(result.LocalVariablePredicateFindings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("@v", finding.VariableName);
        Assert.Equal("=", finding.Operator);
    }

    [Fact]
    public void Extract_ColumnComparedToFormalParameter_NeverFiresLocalVariablePredicate()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(result.LocalVariablePredicateFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToDeclaredLocalVariable_RangeOperator_StillFires()
    {
        // The density-vector-estimate exposure applies equally to range comparisons, not just
        // equality - covered from the start rather than artificially scoped to '=' alone.
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @v VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code > @v;");

        var finding = Assert.Single(result.LocalVariablePredicateFindings);
        Assert.Equal(">", finding.Operator);
    }

    [Fact]
    public void Extract_ColumnComparedToLiteral_NeverFiresLocalVariablePredicate()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.Customers WHERE Code = 'ABC';");

        Assert.Empty(result.LocalVariablePredicateFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToDeclaredLocalVariable_StatementOptionRecompile_NeverFires()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @v VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @v OPTION (RECOMPILE);");

        Assert.Empty(result.LocalVariablePredicateFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToDeclaredLocalVariable_ProcedureWithRecompile_NeverFires()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find WITH RECOMPILE AS
            BEGIN
                DECLARE @v VARCHAR(20) = 'ABC';
                SELECT 1 FROM dbo.Customers WHERE Code = @v;
            END
            """);

        Assert.Empty(result.LocalVariablePredicateFindings);
    }

    [Fact]
    public void Extract_SpExecutesqlSeededParameter_TreatedAsFormalParameter_NeverFiresLocalVariablePredicate()
    {
        // externalVariables (sp_executesql's own declared parameter types, seeded by the
        // dynamic-SQL pipeline) are genuinely caller-supplied per execution, exactly like a
        // formal CREATE PROCEDURE parameter - never the "invisible local" shape.
        var ddl = "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);";
        var sql = "SELECT 1 FROM dbo.Customers WHERE Code = @p;";
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        var sqlResult = SqlScriptParser.ParseText("sql.sql", sql);
        Assert.False(ddlResult.HasErrors);
        Assert.False(sqlResult.HasErrors);

        var catalog = CatalogBuilder.Build([ddlResult, sqlResult]);
        var lineage = LineageResolver.Resolve(catalog, [ddlResult, sqlResult]);
        var externalVariables = new Dictionary<string, SqlType?> { ["@p"] = new SqlType(SqlTypeCategory.VarChar, Length: 20) };

        var result = TypedPredicateExtractor.Extract(sqlResult, catalog, lineage, externalVariables: externalVariables);

        Assert.Empty(result.LocalVariablePredicateFindings);
    }

    // docs/detection-checklist.md full-archive practitioner sweep §E, "Filtered index whose
    // predicate compares against a variable/parameter, not a literal" - oracle-confirmed
    // (SET SHOWPLAN_XML, 2026-08-18): a query filtering the SAME column via a parameter/variable
    // can never use a filtered index whose own filter is a literal-equality restatement of that
    // comparison, even when the runtime value is identical. FilterDefinition is live-only
    // (CatalogIndex's own doc comment) - CatalogBuilder (file mode) never populates it from DDL
    // text alone, so these tests build the DDL catalog normally, then splice in a hand-built
    // filtered index the same way IndexDesignScannerTests builds its own live-only catalog shapes.

    private static PredicateExtractionResult ExtractWithFilteredIndex(string ddl, string filteredIndex, string query)
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        var sqlResult = SqlScriptParser.ParseText("test.sql", query);
        Assert.False(ddlResult.HasErrors, string.Join("; ", ddlResult.Errors.Select(e => e.Message)));
        Assert.False(sqlResult.HasErrors, string.Join("; ", sqlResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([ddlResult]);
        var table = catalog.Find("dbo.Customers")!;
        var index = new CatalogIndex(
            "IX_Active", CatalogIndexKind.Index, IsUnique: false, ["Status"], [],
            IsFiltered: true, FilterDefinition: filteredIndex);
        catalog.AddOrReplace(table with { Indexes = [.. table.Indexes, index] });

        var lineage = LineageResolver.Resolve(catalog, [ddlResult, sqlResult]);
        return TypedPredicateExtractor.Extract(sqlResult, catalog, lineage);
    }

    [Fact]
    public void Extract_ColumnComparedToLocalVariable_SameColumnHasLiteralFilteredIndex_FiresFilteredIndexParameterMismatch()
    {
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "DECLARE @p VARCHAR(20) = 'Active'; SELECT 1 FROM dbo.Customers WHERE Status = @p;");

        var finding = Assert.Single(result.FilteredIndexParameterMismatchFindings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Status", finding.ColumnName);
        Assert.Equal("IX_Active", finding.IndexName);
        Assert.Equal("'Active'", finding.FilterLiteralText);
        Assert.Equal("@p", finding.VariableName);
        Assert.False(finding.IsFormalParameter);
        Assert.Equal("=", finding.Operator);
    }

    [Fact]
    public void Extract_ColumnComparedToFormalParameter_SameColumnHasLiteralFilteredIndex_AlsoFires()
    {
        // Unlike LocalVariablePredicateFinding, this fires for a formal parameter too - the
        // optimizer's own filtered-index-match rule rejects every non-literal operand identically,
        // sniffed parameter or plain local alike.
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Status = @p; END");

        var finding = Assert.Single(result.FilteredIndexParameterMismatchFindings);
        Assert.True(finding.IsFormalParameter);
    }

    [Fact]
    public void Extract_ColumnComparedToLiteral_NeverFiresFilteredIndexParameterMismatch()
    {
        // The query itself restates the filter with a literal - the exact shape the filtered
        // index CAN match, so this must never fire.
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "SELECT 1 FROM dbo.Customers WHERE Status = 'Active';");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_DifferentColumnComparedToVariable_NeverFiresFilteredIndexParameterMismatch()
    {
        // The filtered index is on Status, not the column this predicate actually filters -
        // no relationship between them, must never fire.
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL, Code VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "DECLARE @p VARCHAR(20) = 'X'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToVariable_OptionRecompile_StillFires()
    {
        // Deliberately NOT suppressed by RECOMPILE, unlike LocalVariablePredicateFinding - oracle-
        // confirmed the filtered index still goes unused under a recompiled plan (this finding's
        // own doc comment): the limitation is evaluated against the predicate's compile-time
        // shape, not the value a recompile would re-sniff.
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "DECLARE @p VARCHAR(20) = 'Active'; SELECT 1 FROM dbo.Customers WHERE Status = @p OPTION (RECOMPILE);");

        Assert.Single(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToVariable_MultiPredicateFilter_NeverGuessesMatch()
    {
        // A multi-predicate filter reparses fine as a search condition but is NOT the simple
        // Column = Literal shape TryExtractSimpleLiteralEqualityFilter requires - never guessed
        // at, so this must not fire.
        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL, Region VARCHAR(20) NOT NULL);",
            "([Status]='Active' AND [Region]='West')",
            "DECLARE @p VARCHAR(20) = 'Active'; SELECT 1 FROM dbo.Customers WHERE Status = @p;");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }
}

/// <summary>
/// Oracle-confirmed companion to <see cref="TypedPredicateExtractorTests"/>: the subset of that
/// file's tests whose claim is a real <c>Verdict</c> (not lineage/scope mechanics, not
/// <see cref="ExpressionDerivedFinding"/>, not <c>Unknown</c>) AND whose fixture is deployable
/// with ordinary whitelisted DDL (CREATE TABLE/VIEW/INDEX/FUNCTION/TYPE - no triggers, temp
/// tables, table variables, or table-valued parameters). Split into its own
/// <see cref="OracleTestFixture"/>-derived class rather than mixed into
/// <see cref="TypedPredicateExtractorTests"/> because xUnit provisions a fresh instance (and
/// fresh database, per <see cref="OracleTestFixture.InitializeAsync"/>) per test method - paying
/// that cost for the large majority of this file's tests that assert lineage mechanics and never
/// touch a live database would slow every one of them down for no verification benefit.
/// CLAUDE.md: verify the real thing, not just that the static pipeline agrees with itself.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class TypedPredicateExtractorOracleTests : OracleTestFixture
{
    private static IReadOnlyList<TypedPredicateFinding> Extract(params string[] batches) =>
        ExtractAll(batches).TypedFindings;

    private static PredicateExtractionResult ExtractAll(params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage);
    }

    // xUnit gives every [Fact]/[Theory] case its own instance of this class, and a shared
    // literal database name let two instances' InitializeAsync/DisposeAsync race on the SAME
    // database name once this class grew to ~40 oracle-confirmed cases - "Cannot drop the
    // database ... because it does not exist" from one instance's CREATE racing another's DROP,
    // observed running this file's full suite. OracleTestFixture's own DatabaseName now applies
    // a GUID suffix to every subclass for exactly this reason - this override just supplies the
    // per-class seed, same as every other subclass.
    protected override string DatabaseNameSeed => nameof(TypedPredicateExtractorOracleTests);

    protected override string Ddl => string.Join(
        "\nGO\n",
        "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.OrdersIdx (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE INDEX IX_OrdersIdx_OrderCode ON dbo.OrdersIdx(OrderCode);
        """,
        "CREATE TABLE dbo.OrdersLit (OrderId INT NOT NULL);",
        "CREATE TABLE dbo.TSysname (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        // CREATE TYPE must be in its own batch - referencing it in the same batch that
        // creates it hits SQL Server's compile-time metadata cache and fails to resolve.
        "CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;",
        "CREATE TABLE dbo.OrdersAlias (OrderId dbo.MyIntAlias NOT NULL);",
        // CREATE VIEW must be the first (and only) statement in its batch.
        "CREATE TABLE dbo.OrdersView (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE VIEW dbo.vw_OrdersView AS SELECT OrderCode FROM dbo.OrdersView;",
        "CREATE TABLE dbo.OrdersLike (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.OrdersJoin (CustomerCode VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.CustomersJoin (CustomerCode NVARCHAR(10) NOT NULL);
        """,
        "CREATE TABLE dbo.OrdersHaving (CustomerId INT NOT NULL);",
        "CREATE TABLE dbo.OrdersBetween (OrderDate DATETIME NOT NULL);",
        "CREATE TABLE dbo.OrdersBetweenCode (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE TABLE dbo.OrdersColColCompare (OrderId INT NOT NULL, CustomerId INT NOT NULL);",
        """
        CREATE TABLE dbo.OrdersQualifier (Id INT NOT NULL, CustomerId VARCHAR(20) NOT NULL);
        CREATE TABLE dbo.ShipmentsQualifier (Id INT NOT NULL, TrackingCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        """,
        """
        CREATE TABLE dbo.OrdersExists (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.OrderDetailsExists (OrderId INT NOT NULL, Sku VARCHAR(20) NOT NULL);
        """,
        "CREATE TABLE dbo.UsersAlterStub (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE TABLE dbo.UsersCreateOrAlter (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.IntsSeq (Col INT NOT NULL);
        CREATE TABLE dbo.StringsSeq (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        """,
        "CREATE TABLE dbo.UsersCte (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE TABLE dbo.UsersCteShadow (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Region VARCHAR(10) NOT NULL);",
        """
        CREATE TABLE dbo.OrdersCteNested (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.FlagsCteNested (OrderId INT NOT NULL);
        """,
        "CREATE TABLE dbo.UsersUpdate (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.OrdersUpdateFrom (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.FlagsUpdateFrom (OrderId INT NOT NULL, IsStale BIT NOT NULL);
        """,
        "CREATE TABLE dbo.SessionsDelete (Token VARCHAR(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.OrdersDeleteFrom (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.OrderLinesDeleteFrom (OrderId INT NOT NULL);
        """,
        """
        CREATE TABLE dbo.TargetMerge (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.SourceMerge (Id INT NOT NULL, Code NVARCHAR(20) NOT NULL);
        """,
        """
        CREATE TABLE dbo.TargetMerge2 (Id INT NOT NULL, Status VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.SourceMerge2 (Id INT NOT NULL);
        """,
        "CREATE TABLE dbo.OrdersCteUpdate (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, IsFlagged BIT NOT NULL);",
        // CREATE FUNCTION must be the first (and only) statement in its batch.
        "CREATE TABLE dbo.OrdersTvf (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE FUNCTION dbo.fn_GetOrdersTvf(@Ignored INT) RETURNS TABLE AS RETURN (SELECT Id, CustomerId FROM dbo.OrdersTvf);",
        "CREATE FUNCTION dbo.fn_GetCodesMstvf(@Ignored INT) RETURNS @t TABLE (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL) AS BEGIN RETURN; END",
        "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.OrdersInSub (CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.CustomersInSub (Id NVARCHAR(20) NOT NULL);
        """,
        "CREATE TABLE dbo.OrdersGetDate (CreatedOn DATETIME NOT NULL);",
        "CREATE TABLE dbo.TLen (NameLength INT NOT NULL);",
        "CREATE TABLE dbo.TObjectId (SourceObjectId INT NOT NULL);",
        "CREATE TABLE dbo.TObjectProperty (IsShipped INT NOT NULL);",
        "CREATE TABLE dbo.TRowcount (Total INT NOT NULL);",
        "CREATE TABLE dbo.TIsNull (Id INT NOT NULL);",
        "CREATE TABLE dbo.TStringFn (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        "CREATE TABLE dbo.TDateAddTrunc (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
        """
        CREATE TABLE dbo.TScalarSubquery (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.SettingsScalarSubquery (SettingId INT NOT NULL);
        """,
        """
        CREATE TABLE dbo.TCastInt (Id INT NOT NULL);
        CREATE TABLE dbo.RawCastInt (Value VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        """,
        """
        CREATE TABLE dbo.RecentUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        CREATE TABLE dbo.ArchiveUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        """,
        "CREATE VIEW dbo.vw_CombinedUnion AS SELECT Code FROM dbo.RecentUnion UNION ALL SELECT Code FROM dbo.ArchiveUnion;",
        "CREATE TABLE dbo.OrdersIntCol (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL, INDEX IX_OrdersIntCol_Quantity (Quantity));",
        "CREATE TABLE dbo.OrdersVariantCol (OrderId INT NOT NULL PRIMARY KEY, Tag SQL_VARIANT NOT NULL, INDEX IX_OrdersVariantCol_Tag (Tag));",
        "CREATE TABLE dbo.OrdersMaxSql (OrderId INT NOT NULL PRIMARY KEY, Code VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_OrdersMaxSql_Code (Code));",
        "CREATE TABLE dbo.OrdersMaxWindows (OrderId INT NOT NULL PRIMARY KEY, Code VARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_OrdersMaxWindows_Code (Code));");

    /// <summary>
    /// <see cref="PipelineOracleVerification.VerifyAsync"/>/<c>AssertAllConfirmed</c> only
    /// confirms a verdict that CLAIMS a conversion (<see cref="CorpusFindingVerifier"/>
    /// unconditionally reports <c>NotConfirmed</c> when the probe's plan shows no column-side
    /// CONVERT_IMPLICIT at all - it exists to confirm ScanForced/RangeSeek, per its own class
    /// doc). A <see cref="Verdict.SeekPreserved"/> finding claims the OPPOSITE - that no
    /// conversion happens - so it needs the opposite check: build the same self-authored probe
    /// <see cref="CorpusFindingProbeBuilder"/> would, but assert the column is ABSENT from the
    /// plan's conversions instead of present. Mirrors the direct
    /// <c>ConvertImplicitDetector.FindColumnConversions</c>/<c>DoesNotContain</c> idiom
    /// <see cref="ExplicitCollatePipelineTests"/> already established for its own no-conversion
    /// cases.
    /// </summary>
    private async Task AssertNoColumnConversionAsync(TypedPredicateFinding finding)
    {
        var probe = CorpusFindingProbeBuilder.Build(finding);
        Assert.NotNull(probe);

        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe!);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        var table = finding.Column.TableQualifiedName.Split('.', 2)[^1];
        Assert.DoesNotContain(conversions, c =>
            string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Column, finding.Column.ColumnName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VarcharColumnVsNVarcharParam_SqlCollation_ScanForced_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task IndexedColumn_IsFlaggedIndexed_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersIdx (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE INDEX IX_OrdersIdx_OrderCode ON dbo.OrdersIdx(OrderCode);",
            """
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderId FROM dbo.OrdersIdx WHERE OrderCode = @OrderCode;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task LiteralComparison_TypesTheLiteralSide_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersLit (OrderId INT NOT NULL);",
            "SELECT OrderId FROM dbo.OrdersLit WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        var value = Assert.IsType<PredicateOperand.Value>(finding.OtherOperand);
        Assert.Equal(SqlTypeCategory.Int, value.Type!.Category);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task SysnameVariableVsVarcharColumn_ScanForced_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 6.2: sysname (nvarchar(128)) outranks varchar in
        // precedence exactly like an ordinary nvarchar parameter would - also directly
        // oracle-verified in SysnameOracleTests, this is the pipeline-level confirmation.
        var findings = Extract(
            "CREATE TABLE dbo.TSysname (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find
            AS
            BEGIN
                DECLARE @p sysname = N'x';
                SELECT Code FROM dbo.TSysname WHERE Code = @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CatalogedTypeAliasColumn_ResolvesThroughToUnderlyingType_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;",
            "CREATE TABLE dbo.OrdersAlias (OrderId dbo.MyIntAlias NOT NULL);",
            "SELECT OrderId FROM dbo.OrdersAlias WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.Equal(SqlTypeCategory.Int, finding.Column.Type!.Category);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task PredicateThroughViewLayer_CarriesDepthFromLineage_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersView (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_OrdersView AS SELECT OrderCode FROM dbo.OrdersView;",
            """
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.vw_OrdersView WHERE OrderCode = @OrderCode;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(1, finding.Column.Depth);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // TableQualifiedName/ColumnName always name the ultimate base column (needed for the
        // oracle's plan-matching signal), but ImmediateRelation* must name the VIEW the source
        // predicate actually queried - the Verify oracle probes this, not the base table
        // directly, or a depth>=1 finding is never actually tested through the view layer it
        // claims to be inherited through.
        Assert.Equal("dbo.OrdersView", finding.Column.TableQualifiedName);
        Assert.Equal("dbo.vw_OrdersView", finding.Column.ImmediateRelationQualifiedName);
        Assert.Equal("OrderCode", finding.Column.ImmediateColumnName);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task UnionViewWithAllPassthroughBranchesAgreeingOnType_ColumnConverts_ScanForced_OracleConfirmed()
    {
        // Oracle proof for the UNION-view type-agreement fix: every branch is a clean varchar
        // passthrough, so the merged column's own runtime type is fully determined regardless of
        // which branch a given row came from - a genuine, non-guessed column-side conversion.
        var findings = Extract(
            "CREATE TABLE dbo.RecentUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.ArchiveUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_CombinedUnion AS SELECT Code FROM dbo.RecentUnion UNION ALL SELECT Code FROM dbo.ArchiveUnion;",
            "SELECT Code FROM dbo.vw_CombinedUnion WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        // PipelineOracleVerification's generic prober queries finding.Column.TableQualifiedName
        // directly, but a UNION-merged column deliberately has no single real table ("?") - same
        // probe-fidelity limitation the TVF/CROSS APPLY tests hit. Hand-build the equivalent
        // probe against the VIEW itself instead.
        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.vw_CombinedUnion WHERE Code = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LikeColumnVsNvarcharPattern_ColumnConverts_ScanForced_OracleConfirmed()
    {
        // The classic ORM-generated pattern: `varcharCol LIKE @nvarcharPattern`. LIKE was
        // previously invisible to the typed pipeline entirely - only Tier-1's wildcard-shape
        // check ran against it, never the type-conversion question.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersLike (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.OrdersLike WHERE Code LIKE N'ABC%';");

        var finding = Assert.Single(findings);
        Assert.Equal("LIKE", finding.Operator);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task LikeColumnVsVarcharLiteralPattern_NoConversion_SeekPreserved_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersLike (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.OrdersLike WHERE Code LIKE 'ABC%';");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task SameComparisonMovedFromSelectListIntoWhere_NowProducesAFinding_OracleConfirmed()
    {
        // The positive control for the filter-context gate (see TypedPredicateExtractorTests'
        // SELECT-list/ORDER BY negative cases): the identical comparison, in a genuine filter
        // position, must still fire.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersLike (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.OrdersLike WHERE Code = N'X';");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task JoinOnClausePredicate_IsResolved_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersJoin (CustomerCode VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.CustomersJoin (CustomerCode NVARCHAR(10) NOT NULL);",
            """
            SELECT o.CustomerCode
            FROM dbo.OrdersJoin o
            JOIN dbo.CustomersJoin c ON o.CustomerCode = c.CustomerCode;
            """);

        // Both directions of the join predicate are now classified (the join-direction fix):
        // the varchar side genuinely converts (ScanForced), and the nvarchar side - reported
        // separately - never converts regardless of collation (its own outcome, correctly
        // SeekPreserved, not swallowed by only checking the other column).
        Assert.Equal(2, findings.Count);
        var varcharSide = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.OrdersJoin");
        Assert.Equal(Verdict.ScanForced, varcharSide.Verdict);
        var nvarcharSide = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.CustomersJoin");
        Assert.Equal(Verdict.SeekPreserved, nvarcharSide.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [varcharSide]);
        PipelineOracleVerification.AssertAllConfirmed(results);
        await AssertNoColumnConversionAsync(nvarcharSide);
    }

    [Fact]
    public async Task HavingClausePredicate_IsResolved_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersHaving (CustomerId INT NOT NULL);",
            """
            SELECT CustomerId, COUNT(*)
            FROM dbo.OrdersHaving
            GROUP BY CustomerId
            HAVING CustomerId = 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task BetweenPredicate_IsResolved_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersBetween (OrderDate DATETIME NOT NULL);",
            "SELECT OrderDate FROM dbo.OrdersBetween WHERE OrderDate BETWEEN '20240101' AND '20240201';");

        // BETWEEN decomposes into two independent comparisons (col >= lower AND col <= upper) -
        // both bounds are reported.
        Assert.Equal(2, findings.Count);
        // datetime outranks varchar in T-SQL precedence, so the literal bounds convert.
        Assert.All(findings, f => Assert.Equal(Verdict.SeekPreserved, f.Verdict));

        await AssertNoColumnConversionAsync(findings[0]);
        await AssertNoColumnConversionAsync(findings[1]);
    }

    [Fact]
    public async Task BetweenPredicate_UpperBoundAloneForcesConversion_IsReported_OracleConfirmed()
    {
        // Only the upper bound carries a higher-precedence literal (nvarchar) - a scanner that
        // only checked the lower bound would miss this entirely.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersBetweenCode (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.OrdersBetweenCode WHERE Code BETWEEN 'A' AND N'Z';");

        Assert.Equal(2, findings.Count);
        Assert.Equal(Verdict.SeekPreserved, findings[0].Verdict);
        Assert.Equal(Verdict.ScanForced, findings[1].Verdict);

        await AssertNoColumnConversionAsync(findings[0]);
        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [findings[1]]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task ColumnVsColumnSameType_NoConversionAnywhere_SeekPreserved_OracleConfirmed()
    {
        // A column-vs-column comparison is classified in BOTH directions (the join-predicate
        // fix: `ON a.x = b.y` can convert either side depending on which one has lower
        // precedence, so only checking one side silently misses the other's verdict).
        var findings = Extract(
            "CREATE TABLE dbo.OrdersColColCompare (OrderId INT NOT NULL, CustomerId INT NOT NULL);",
            "SELECT OrderId FROM dbo.OrdersColColCompare WHERE OrderId = CustomerId;");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(Verdict.SeekPreserved, f.Verdict));
        Assert.Equal("OrderId", findings[0].Column.ColumnName);
        Assert.Equal("CustomerId", findings[1].Column.ColumnName);

        await AssertNoColumnConversionAsync(findings[0]);
        await AssertNoColumnConversionAsync(findings[1]);
    }

    [Fact]
    public async Task CorrectQualifier_SameShapeAsAboveNearMiss_ProducesFinding_OracleConfirmed()
    {
        // The near-miss sibling (a bad qualifier producing no finding at all) is covered as pure
        // static mechanics in TypedPredicateExtractorTests.
        // Extract_QualifierNotInScope_NoFinding_NeverFallsBackToNameOnlyMatch - same tables, same
        // predicate, but the qualifier here ('s') is the real alias, so it resolves and fires.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersQualifier (Id INT NOT NULL, CustomerId VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.ShipmentsQualifier (Id INT NOT NULL, TrackingCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindShipment @p NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.OrdersQualifier AS o
                JOIN dbo.ShipmentsQualifier AS s ON o.Id = s.Id
                WHERE s.TrackingCode = @p;
            END
            """);

        // Same join-condition SeekPreserved noise as the near-miss above; the TrackingCode
        // predicate is the one under test here.
        var finding = Assert.Single(findings, f => f.Column.ColumnName == "TrackingCode");
        Assert.Equal("dbo.ShipmentsQualifier", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // Only the TrackingCode finding is verified here - the join predicate's own o.Id = s.Id
        // comparison also resolves (SeekPreserved noise, asserted nowhere in this test) but
        // PipelineOracleVerification's harness only confirms verdicts that CLAIM a conversion.
        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CorrelatedExistsSubquery_OuterAliasResolvesThroughScopeChain_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 2.2: the EXISTS subquery's own FROM scope (d)
        // is innermost when its WHERE clause is visited; "o.CustomerId" refers to the *outer*
        // query's alias, one level up the scope chain, not anything in the subquery's own FROM
        // clause.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersExists (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.OrderDetailsExists (OrderId INT NOT NULL, Sku VARCHAR(20) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.OrdersExists AS o
                WHERE o.CustomerId = @CustomerId
                    AND EXISTS (SELECT 1 FROM dbo.OrderDetailsExists AS d WHERE d.OrderId = o.Id);
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.OrdersExists", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings.Where(f => f.Column.ColumnName == "CustomerId").ToList());
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task AlterProcedureAfterCreateStub_UsesAlterProcsOwnParameterType_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 2.3: the idempotent-deploy pattern seen verbatim
        // in the First Responder Kit corpus repo - a body-less CREATE PROCEDURE stub, then the
        // real body via ALTER PROCEDURE.
        var findings = Extract(
            "CREATE TABLE dbo.UsersAlterStub (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE PROCEDURE dbo.usp_FindUser AS RETURN 0;",
            """
            ALTER PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.UsersAlterStub WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CreateOrAlterProcedure_UsesOwnParameterType_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.UsersCreateOrAlter (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE OR ALTER PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.UsersCreateOrAlter WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task TwoProceduresInSequence_SecondProcDoesNotInheritFirstProcsVariableTypes_OracleConfirmed()
    {
        // The core staleness bug: before the fix, only CreateProcedureStatement/
        // CreateFunctionStatement reset _variables, but every CREATE PROCEDURE already did that
        // correctly - the real gap was ALTER's total non-handling. This test guards the
        // more basic regression (two ordinary CREATE PROCEDUREs in a row must never leak
        // variable types between them).
        var findings = Extract(
            "CREATE TABLE dbo.IntsSeq (Col INT NOT NULL);",
            "CREATE TABLE dbo.StringsSeq (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_First @Id INT
            AS
            BEGIN
                SELECT Col FROM dbo.IntsSeq WHERE Col = @Id;
            END
            """,
            """
            CREATE PROCEDURE dbo.usp_Second @Id NVARCHAR(20)
            AS
            BEGIN
                SELECT Col FROM dbo.StringsSeq WHERE Col = @Id;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.StringsSeq");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var intFinding = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.IntsSeq");
        Assert.Equal(Verdict.SeekPreserved, intFinding.Verdict);
        await AssertNoColumnConversionAsync(intFinding);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task PredicateInsideCte_ResolvesToRealBaseColumn_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 2.4: the predicate lives inside the CTE body
        // itself, not the outer query - proves CteResolver's own resolution (not just the outer
        // SELECT referencing the finished CTE) goes through the normal typed-predicate pipeline.
        var findings = Extract(
            "CREATE TABLE dbo.UsersCte (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                WITH Matches AS (SELECT DisplayName FROM dbo.UsersCte WHERE DisplayName = @DisplayName)
                SELECT DisplayName FROM Matches;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.UsersCte", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CteNameShadowsRealTable_PredicateAgainstOuterQueryResolvesThroughCte_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.UsersCteShadow (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Region VARCHAR(10) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                WITH UsersCteShadow AS (SELECT DisplayName FROM dbo.UsersCteShadow WHERE Region = 'US')
                SELECT DisplayName FROM UsersCteShadow WHERE DisplayName = @DisplayName;
            END
            """);

        // Region = 'US' inside the CTE body resolves against the real dbo.UsersCteShadow
        // (VarChar vs a literal - SeekPreserved, filtered out below); the outer DisplayName
        // predicate is against the CTE's own single-column shape, still tracing back to
        // dbo.UsersCteShadow.DisplayName.
        var finding = Assert.Single(findings, f => f.Column.ColumnName == "DisplayName");
        Assert.Equal("dbo.UsersCteShadow", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CteVisibleInsideNestedSubquery_ResolvesCorrelatedReference_OracleConfirmed()
    {
        // A CTE is visible for the whole containing statement, including a correlated subquery
        // nested inside the main query - not just the top-level FROM clause.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersCteNested (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.FlagsCteNested (OrderId INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Check @CustomerId NVARCHAR(20)
            AS
            BEGIN
                WITH RecentOrders AS (SELECT Id, CustomerId FROM dbo.OrdersCteNested)
                SELECT Id
                FROM RecentOrders AS ro
                WHERE ro.CustomerId = @CustomerId
                    AND EXISTS (SELECT 1 FROM dbo.FlagsCteNested AS f WHERE f.OrderId = ro.Id);
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.OrdersCteNested", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task UpdateWhereClause_NoFromExtension_ResolvesAgainstTarget_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 4.1, audit finding B1 ("the single biggest
        // coverage gap in the tool"): UPDATE's WHERE clause previously had no FROM-scope pushed
        // at all, so this predicate was invisible to Pass 3 no matter what it contained.
        var findings = Extract(
            "CREATE TABLE dbo.UsersUpdate (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_RenameUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.UsersUpdate SET DisplayName = @DisplayName WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.UsersUpdate", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task UpdateWithFromExtension_ResolvesJoinedTableAliases_OracleConfirmed()
    {
        // UPDATE ... FROM ... JOIN ... WHERE - the extended FROM syntax, where the WHERE clause
        // references aliases established only in the FROM clause, not the bare target name.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersUpdateFrom (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.FlagsUpdateFrom (OrderId INT NOT NULL, IsStale BIT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_MarkStale @CustomerId NVARCHAR(20)
            AS
            BEGIN
                UPDATE f
                SET f.IsStale = 1
                FROM dbo.FlagsUpdateFrom AS f
                JOIN dbo.OrdersUpdateFrom AS o ON o.Id = f.OrderId
                WHERE o.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.OrdersUpdateFrom", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DeleteWhereClause_NoFromExtension_ResolvesAgainstTarget_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.SessionsDelete (Token VARCHAR(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_ExpireSession @Token NVARCHAR(64)
            AS
            BEGIN
                DELETE FROM dbo.SessionsDelete WHERE Token = @Token;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.SessionsDelete", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DeleteWithFromExtension_ResolvesJoinedTableAliases_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersDeleteFrom (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.OrderLinesDeleteFrom (OrderId INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_PurgeOrderLines @CustomerId NVARCHAR(20)
            AS
            BEGIN
                DELETE ol
                FROM dbo.OrderLinesDeleteFrom AS ol
                JOIN dbo.OrdersDeleteFrom AS o ON o.Id = ol.OrderId
                WHERE o.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.OrdersDeleteFrom", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task MergeOnClause_ResolvesTargetAndSourceAliases_OracleConfirmed()
    {
        // MergeSpecification's own TableReference property is the USING SOURCE, not the INTO
        // target (verified empirically against the real parser output while implementing this -
        // the target's alias lives in the separate TableAlias property). This test pins that
        // both sides resolve correctly regardless of that naming trap.
        var findings = Extract(
            "CREATE TABLE dbo.TargetMerge (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.SourceMerge (Id INT NOT NULL, Code NVARCHAR(20) NOT NULL);",
            """
            MERGE INTO dbo.TargetMerge AS t
            USING dbo.SourceMerge AS s
            ON t.Code = s.Code
            WHEN MATCHED THEN UPDATE SET t.Id = s.Id
            WHEN NOT MATCHED THEN INSERT (Id, Code) VALUES (s.Id, s.Code);
            """);

        // Both directions of the ON clause's column-vs-column comparison are now reported.
        var targetSide = Assert.Single(findings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.TargetMerge");
        Assert.Equal(Verdict.ScanForced, targetSide.Verdict);
        var sourceSide = Assert.Single(findings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.SourceMerge");
        Assert.Equal(Verdict.SeekPreserved, sourceSide.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [targetSide]);
        PipelineOracleVerification.AssertAllConfirmed(results);
        await AssertNoColumnConversionAsync(sourceSide);
    }

    [Fact]
    public async Task MergeActionClauseAdditionalCondition_Resolves_OracleConfirmed()
    {
        // WHEN MATCHED AND <extra condition> - the additional predicate on the action clause
        // itself, not just the top-level ON clause, must resolve through the same scope.
        var findings = Extract(
            "CREATE TABLE dbo.TargetMerge2 (Id INT NOT NULL, Status VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.SourceMerge2 (Id INT NOT NULL);",
            """
            MERGE INTO dbo.TargetMerge2 AS t
            USING dbo.SourceMerge2 AS s
            ON t.Id = s.Id
            WHEN MATCHED AND t.Status = N'Active' THEN UPDATE SET t.Id = s.Id;
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CteThenUpdateFrom_CteVisibleInUpdatesFromClause_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.OrdersCteUpdate (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, IsFlagged BIT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FlagOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                WITH TargetOrders AS (SELECT Id, CustomerId FROM dbo.OrdersCteUpdate)
                UPDATE o
                SET o.IsFlagged = 1
                FROM dbo.OrdersCteUpdate AS o
                JOIN TargetOrders AS t ON t.Id = o.Id
                WHERE t.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.OrdersCteUpdate", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task InlineTvfInFromClause_PredicateResolvesToBaseColumnWithDepth_OracleConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 4.2, audit finding B2: FromScopeResolver only
        // handled NamedTableReference and QueryDerivedTable - a table-valued function call in a
        // FROM clause (SchemaObjectFunctionTableReference) fell to the unhandled default and
        // resolved to an empty relation, so a predicate over one of its columns could never
        // trace back to the real base column at all. "Done when": resolves to the base column
        // with depth >= 1, exactly like reading through a view.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersTvf (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_GetOrdersTvf(@Ignored INT) RETURNS TABLE AS RETURN (SELECT Id, CustomerId FROM dbo.OrdersTvf);",
            """
            CREATE PROCEDURE dbo.usp_FindOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT Id FROM dbo.fn_GetOrdersTvf(1) WHERE CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.OrdersTvf", finding.Column.TableQualifiedName);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.True(finding.Column.Depth >= 1);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // The generic PipelineOracleVerification prober can't be used unmodified here: it
        // queries finding.Column.ImmediateRelationQualifiedName bare (`FROM [dbo].[fn_GetOrdersTvf]`),
        // but a table-valued function is not queryable without its call arguments - that's a
        // probe-fidelity limitation of the generic prober (same class of issue
        // ComputedColumnPipelineTests hit for non-persisted computed columns), not a claim about
        // the tool's own verdict. Hand-build the equivalent probe WITH the call argument instead.
        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.fn_GetOrdersTvf(1) WHERE CustomerId = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c =>
            string.Equals(c.Table, "OrdersTvf", StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Column, "CustomerId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InlineTvfViaCrossApply_PredicateResolvesToBaseColumnWithDepth_OracleConfirmed()
    {
        // The realistic real-world shape a hand-rolled string-splitting/per-row utility TVF is
        // actually called in - CROSS APPLY against a correlated argument, not a bare FROM with a
        // constant - has no test coverage anywhere in this suite even though the bare-FROM case
        // right above does. CROSS APPLY parses as ScriptDom's UnqualifiedJoin (a JoinTableReference
        // subtype), which FromScopeResolver.FlattenJoins already recurses through generically, so
        // this is expected to resolve identically to the bare-FROM case - this test is the proof.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersTvf (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_GetOrdersTvf(@Ignored INT) RETURNS TABLE AS RETURN (SELECT Id, CustomerId FROM dbo.OrdersTvf);",
            """
            CREATE PROCEDURE dbo.usp_FindOrdersViaApply @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT f.Id FROM dbo.OrdersTvf o CROSS APPLY dbo.fn_GetOrdersTvf(o.Id) f WHERE f.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.OrdersTvf", finding.Column.TableQualifiedName);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.True(finding.Column.Depth >= 1);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // Same probe-fidelity limitation as the bare-FROM test above - hand-build the equivalent
        // CROSS APPLY probe with a real correlated left side instead of the generic prober.
        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.OrdersTvf o CROSS APPLY dbo.fn_GetOrdersTvf(o.Id) f WHERE f.CustomerId = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c =>
            string.Equals(c.Table, "OrdersTvf", StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Column, "CustomerId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultiStatementTvfInFromClause_UsesDeclaredReturnColumnType_OracleConfirmed()
    {
        // A multi-statement TVF's columns are Declared provenance (its RETURNS @t TABLE(...)
        // shape), not a chain back to a base column - this is the complementary case to the
        // inline-TVF test above, proving both TVF kinds resolve through the FROM clause now.
        var findings = Extract(
            """
            CREATE FUNCTION dbo.fn_GetCodesMstvf(@Ignored INT)
            RETURNS @t TABLE (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL)
            AS
            BEGIN
                RETURN;
            END
            """,
            """
            CREATE PROCEDURE dbo.usp_FindCodes @Code NVARCHAR(20)
            AS
            BEGIN
                SELECT Id FROM dbo.fn_GetCodesMstvf(1) WHERE Code = @Code;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(SqlTypeCategory.VarChar, finding.Column.Type!.Category);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // Same TVF call-argument limitation as the inline-TVF test above: a multi-statement TVF
        // is not queryable bare either, so the generic prober's immediate-relation probe can't
        // be used unmodified - hand-build the equivalent probe with the call argument instead.
        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.fn_GetCodesMstvf(1) WHERE Code = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InListHomogeneousVarchar_SqlCollation_SeekPreserved_OracleConfirmed()
    {
        // Oracle-verified (docs/audit-remediation-plan.md Phase 4.3): a homogeneous varchar IN
        // list against a varchar column produces no conversion at all.
        var findings = Extract(
            "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.TInList WHERE Col IN ('a', 'b', 'c');");

        var finding = Assert.Single(findings);
        Assert.Equal("IN", finding.Operator);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task InListOneNvarcharLiteralAmongVarchar_SqlCollation_ScanForced_OracleConfirmed()
    {
        // Oracle-verified: a SINGLE higher-precedence literal anywhere in an otherwise-
        // homogeneous list is enough to force the column to convert for the whole comparison -
        // this is the case a naive "type the first element only" implementation would miss.
        var findings = Extract(
            "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.TInList WHERE Col IN ('a', N'b', 'c');");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task InListHomogeneousNvarchar_AgainstVarcharColumn_ScanForced_OracleConfirmed()
    {
        // Oracle-verified: matches ordinary single-comparison precedence (nvarchar outranks
        // varchar), just applied across the whole list.
        var findings = Extract(
            "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.TInList WHERE Col IN (N'a', N'b', N'c');");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task InListWithParameter_ResolvesParameterType_OracleConfirmed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find @A VARCHAR(20), @B NVARCHAR(20)
            AS
            BEGIN
                SELECT Col FROM dbo.TInList WHERE Col IN (@A, @B);
            END
            """);

        // nvarchar outranks varchar, so Col converts.
        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Theory]
    [InlineData("!<")]
    [InlineData("!>")]
    public async Task NotLessThanAndNotGreaterThan_ClassifyNormally_OracleConfirmed(string sqlOperator)
    {
        // T-SQL folds !< to >= and !> to <= (oracle-verified: identical plan shape, a genuine
        // range seek) - these are NOT non-seekable like <>/NOT IN/NOT LIKE, so they route
        // through the type-conversion verdict machinery exactly like any other comparison.
        var findings = Extract(
            "CREATE TABLE dbo.TInList (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            $"SELECT Col FROM dbo.TInList WHERE Col {sqlOperator} N'a';");

        var finding = Assert.Single(findings);
        Assert.Equal(sqlOperator, finding.Operator);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task InSubquery_ResolvesSubqueryOutputColumnThroughLineage_OracleConfirmed()
    {
        var findings = Extract(
            """
            CREATE TABLE dbo.OrdersInSub (CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.CustomersInSub (Id NVARCHAR(20) NOT NULL);
            """,
            "SELECT CustomerId FROM dbo.OrdersInSub WHERE CustomerId IN (SELECT Id FROM dbo.CustomersInSub);");

        var finding = Assert.Single(findings);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task EqualsAnySubquery_ClassifiesLikeInSubquery_OracleConfirmed()
    {
        // Roadmap Phase E2: reuses the IN-subquery fixture tables - = ANY (subquery) is oracle-
        // verified to produce the identical CONVERT_IMPLICIT signature as IN (subquery).
        var findings = Extract(
            """
            CREATE TABLE dbo.OrdersInSub (CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.CustomersInSub (Id NVARCHAR(20) NOT NULL);
            """,
            "SELECT CustomerId FROM dbo.OrdersInSub WHERE CustomerId = ANY (SELECT Id FROM dbo.CustomersInSub);");

        var finding = Assert.Single(findings);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task ColumnComparedToBuiltinFunctionCall_ResolvesFixedReturnType_OracleConfirmed()
    {
        // BuiltinFunctionTypeResolver's curated, oracle-verified table: GETDATE() types as
        // DATETIME, so a DATETIME column compared against it classifies normally instead of
        // falling to Unknown - the single biggest driver of this tool's Unknown-verdict rate in
        // real corpora before this existed.
        var result = ExtractAll(
            "CREATE TABLE dbo.OrdersGetDate (CreatedOn DATETIME NOT NULL);",
            "SELECT 1 FROM dbo.OrdersGetDate WHERE CreatedOn > GETDATE();");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "predicate operand");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToLenOfNvarcharLiteral_MixedCategoryClassifiesNormally_OracleConfirmed()
    {
        // LEN() types as INT (oracle-verified) - an INT column compared against it should
        // classify exactly like any other int-vs-int comparison, not fall to Unknown.
        var result = ExtractAll(
            "CREATE TABLE dbo.TLen (NameLength INT NOT NULL);",
            "SELECT 1 FROM dbo.TLen WHERE NameLength = LEN(N'hello');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToObjectId_ResolvesFixedReturnType_OracleConfirmed()
    {
        // OBJECT_ID() types as INT (oracle-verified) - an INT column compared against it should
        // classify normally instead of falling to Unknown, the same gap GETDATE()/LEN() close
        // above.
        var result = ExtractAll(
            "CREATE TABLE dbo.TObjectId (SourceObjectId INT NOT NULL);",
            "SELECT 1 FROM dbo.TObjectId WHERE SourceObjectId = OBJECT_ID(N'dbo.TObjectId');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "predicate operand");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToObjectPropertyIsMSShipped_ResolvesFixedReturnType_OracleConfirmed()
    {
        // OBJECTPROPERTY() also types as INT (oracle-verified).
        var result = ExtractAll(
            "CREATE TABLE dbo.TObjectProperty (IsShipped INT NOT NULL);",
            "SELECT 1 FROM dbo.TObjectProperty WHERE IsShipped = OBJECTPROPERTY(OBJECT_ID(N'dbo.TObjectProperty'), N'IsMSShipped');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToGlobalVariable_ResolvesFixedType_OracleConfirmed()
    {
        // @@ROWCOUNT types as INT (oracle-verified) - a GlobalVariableExpression previously fell
        // through the same generic default arm as an unhandled function call.
        var result = ExtractAll(
            "CREATE TABLE dbo.TRowcount (Total INT NOT NULL);",
            "SELECT 1 FROM dbo.TRowcount WHERE Total = @@ROWCOUNT;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToCursorRowsGlobalVariable_ResolvesFixedType_OracleConfirmed()
    {
        // @@CURSOR_ROWS types as INT (oracle-verified via sys.dm_exec_describe_first_result_set,
        // same method as every GlobalVariableTypes entry) - previously missing from the curated
        // table despite sibling globals like @@ROWCOUNT already being covered. Reuses
        // dbo.TRowcount (already deployed for the @@ROWCOUNT test above) rather than declaring a
        // new table this class's shared Ddl doesn't create.
        var result = ExtractAll(
            "CREATE TABLE dbo.TRowcount (Total INT NOT NULL);",
            "SELECT 1 FROM dbo.TRowcount WHERE Total = @@CURSOR_ROWS;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToIsNullOfHigherPrecedenceLiteral_UsesFirstArgumentType_OracleConfirmed()
    {
        // Oracle-verified: ISNULL(check_expression, replacement_value) returns check_expression's
        // own type, even when replacement_value would otherwise outrank it in precedence -
        // ISNULL(@intVar, N'x') still types as int, not nvarchar. Distinct from COALESCE, which
        // CLAUDE.md's hard-cases list calls out separately.
        var result = ExtractAll(
            "CREATE TABLE dbo.TIsNull (Id INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find @Id INT
            AS
            BEGIN
                SELECT 1 FROM dbo.TIsNull WHERE Id = ISNULL(@Id, 0);
            END
            """);

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task ColumnComparedToCastToInt_ResolvesTargetType_OracleConfirmed()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.TCastInt (Id INT NOT NULL);",
            "CREATE TABLE dbo.RawCastInt (Value VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.TCastInt, dbo.RawCastInt WHERE Id = CAST(Value AS INT);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task PredicateAgainstSysObjectsIntColumnVsNvarcharValue_ResolvesAndClassifies_OracleConfirmed()
    {
        // sys.objects has no CREATE DDL anywhere (it's a built-in system catalog view), and
        // before SystemCatalogViewRegistry existed a predicate against it fell through as an
        // unresolved FROM table reference - the single dominant cause of skipped predicates
        // across this project's own pinned corpus, since DBA/admin scripts (a large share of it)
        // query sys.objects/sysobjects constantly. type_desc is NVARCHAR(60) (oracle-verified);
        // comparing it to a lower-precedence VARCHAR value converts the VALUE side, not the
        // column - SeekPreserved is the correct, harmless verdict here (proves direction is
        // still respected even for a system view), and the point of this test is that a real
        // verdict was reached at all, not that it happened to be ScanForced. No fixture DDL is
        // needed to deploy - sys.objects always exists.
        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Find @T VARCHAR(20) AS BEGIN SELECT name FROM sys.objects WHERE type_desc = @T; END");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("sys.objects", finding.Column.TableQualifiedName);
        Assert.Equal("type_desc", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        // sys.objects has no CREATE DDL anywhere in this scan, so catalog.Find can never resolve
        // it - Indexed is honestly Unknown (null), not a guessed false (CLAUDE.md: never guess).
        Assert.Null(finding.Column.Indexed);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task PredicateAgainstInformationSchemaColumnsIntColumn_ResolvesAndClassifies_OracleConfirmed()
    {
        // INFORMATION_SCHEMA.COLUMNS has no CREATE DDL anywhere either (a built-in compatibility
        // view, same story as sys.objects above) - before it was modeled, a predicate against it
        // fell through as an unresolved FROM table reference exactly like sys.objects did.
        // ORDINAL_POSITION is INT (oracle-verified); comparing it to an INT parameter is a
        // same-category comparison, so the point of this test is that a real verdict was
        // reached at all (not falling to Unknown/unresolved), independent of any collation
        // question. No fixture DDL needed to deploy - INFORMATION_SCHEMA.COLUMNS always exists.
        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_FindColumn @Pos INT AS BEGIN SELECT column_name FROM INFORMATION_SCHEMA.COLUMNS WHERE ORDINAL_POSITION = @Pos; END");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("INFORMATION_SCHEMA.COLUMNS", finding.Column.TableQualifiedName);
        Assert.Equal("ORDINAL_POSITION", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        // INFORMATION_SCHEMA.COLUMNS has no CREATE DDL anywhere either, so catalog.Find can never
        // resolve it - Indexed is honestly Unknown (null), not a guessed false.
        Assert.Null(finding.Column.Indexed);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task DoubleNotWrappedComparison_ClassifiesTheSameAsTheBareComparison_OracleConfirmed()
    {
        // Roadmap Phase E2: proves the NOT-polarity fix produces a REAL, oracle-confirmed
        // verdict, not just the right operator string statically - NOT (NOT (X)) must convert
        // the column exactly like the bare `DisplayName = @DisplayName` flagship case does.
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUserDoubleNot @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE NOT (NOT (DisplayName = @DisplayName));
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("=", finding.Operator);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task ColumnComparedToUpperOfNvarcharParam_UsesFirstArgumentType_ScanForced_OracleConfirmed()
    {
        // Roadmap Phase B: UPPER (and the rest of BuiltinFunctionTypeResolver's string-transform
        // set) preserves its first argument's own type exactly, oracle-verified. UPPER(@p) where
        // @p is nvarchar types as nvarchar, so a varchar column under a SQL_* collation converts -
        // the flagship direction, reached through a function call rather than a bare value.
        var result = ExtractAll(
            "CREATE TABLE dbo.TStringFn (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindStringFn @P NVARCHAR(20)
            AS
            BEGIN
                SELECT 1 FROM dbo.TStringFn WHERE Code = UPPER(@P);
            END
            """);

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task ColumnComparedToDateAddDayTruncationIdiom_ResolvesDateTimeNotInt_ScanForced_OracleConfirmed()
    {
        // Oracle-verified: DATEADD(day, DATEDIFF(day, 0, @p), 0) - the common date-truncation
        // idiom, where the third argument is the INT literal 0, not a date/time expression -
        // resolves to datetime, not Int. A naive argument-passthrough rule mistyped this shape
        // (real production database, this exact idiom, found through a live scan-db run) as Int,
        // which would have missed the varchar-column-vs-datetime conversion entirely.
        var result = ExtractAll(
            "CREATE TABLE dbo.TDateAddTrunc (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindDateAddTrunc @P INT
            AS
            BEGIN
                SELECT 1 FROM dbo.TDateAddTrunc WHERE Code = DATEADD(day, DATEDIFF(day, 0, @P), 0);
            END
            """);

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task ColumnComparedToScalarSubquery_ResolvesSubqueryOutputColumnType_ScanForced_OracleConfirmed()
    {
        // Oracle-verified: `varchar Code = (SELECT int-typed SettingId FROM ...)` converts the
        // column exactly like the equivalent bare-int-value comparison would - the scalar
        // subquery case ResolveOperand had no type resolution for at all before this fix,
        // permanently Unknown regardless of how well-typed its own output column was.
        var result = ExtractAll(
            "CREATE TABLE dbo.TScalarSubquery (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.SettingsScalarSubquery (SettingId INT NOT NULL);",
            "SELECT 1 FROM dbo.TScalarSubquery WHERE Code = (SELECT SettingId FROM dbo.SettingsScalarSubquery);");

        var finding = Assert.Single(result.TypedFindings, f => f.Column.TableQualifiedName == "dbo.TScalarSubquery");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task IntColumnVsSqlVariantValue_HighestPrecedence_ScanForced_OracleConfirmed()
    {
        // sql_variant is T-SQL's highest-precedence type - the real, indexed int column always
        // converts, never the sql_variant side, oracle-verified: Index Scan, CONVERT_IMPLICIT on
        // the column, no RangeColumns/GetRangeThroughConvert anywhere in the plan.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersIntCol (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL, INDEX IX_OrdersIntCol_Quantity (Quantity));",
            "SELECT 1 FROM dbo.OrdersIntCol WHERE Quantity = CAST(5 AS SQL_VARIANT);");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.OrdersIntCol", finding.Column.TableQualifiedName);
        Assert.Equal("Quantity", finding.Column.ColumnName);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task SqlVariantColumnVsIntValue_HighestPrecedence_SeekPreserved_OracleConfirmed()
    {
        // Reverse direction: the sql_variant COLUMN is the highest-precedence side, so the int
        // value converts instead - the column keeps its seek, oracle-verified: Index Seek, a
        // real SeekPredicates/RangeColumns entry on the column, CONVERT_IMPLICIT lands on the
        // value's own ConstExpr, never on the column's ColumnReference.
        var findings = Extract(
            "CREATE TABLE dbo.OrdersVariantCol (OrderId INT NOT NULL PRIMARY KEY, Tag SQL_VARIANT NOT NULL, INDEX IX_OrdersVariantCol_Tag (Tag));",
            "SELECT 1 FROM dbo.OrdersVariantCol WHERE Tag = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.Equal("dbo.OrdersVariantCol", finding.Column.TableQualifiedName);
        Assert.Equal("Tag", finding.Column.ColumnName);
        Assert.True(finding.Column.Indexed);

        await AssertNoColumnConversionAsync(finding);
    }

    [Theory]
    [InlineData("OrdersMaxSql", "SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("OrdersMaxWindows", "Latin1_General_CI_AS")]
    public async Task BoundedColumnVsMaxTypedParameter_RangeSeek_OracleConfirmed(string table, string collation)
    {
        // docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #1 - oracle-
        // verified collation-independent (both the SQL_* and Windows collation representative
        // tables get the identical treatment): the column itself never converts at all here (the
        // Convert node wraps the PARAMETER, not the column - confirmed directly in the plan XML,
        // unlike every other RangeSeek case in this codebase) - a real Index Seek via the
        // GetRangeWithMismatchedTypes intrinsic instead of GetRangeThroughConvert. This needs its
        // own bespoke plan-XML assertion rather than the standard PipelineOracleVerification/
        // CorpusFindingVerifier path, since that machinery is built around confirming a claimed
        // COLUMN-side CONVERT_IMPLICIT, which genuinely does not exist for this shape - requires
        // real row data to reproduce (an empty/tiny table never triggers this seek strategy,
        // the same trap documented elsewhere in this codebase for GetRangeThroughConvert).
        var findings = Extract(
            $"CREATE TABLE dbo.{table} (OrderId INT NOT NULL PRIMARY KEY, Code VARCHAR(50) COLLATE {collation} NOT NULL, INDEX IX_{table}_Code (Code));",
            $"SELECT 1 FROM dbo.{table} WHERE Code = CAST('V1' AS VARCHAR(MAX));");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.Equal($"dbo.{table}", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);

        var seedRows = $"""
            INSERT INTO dbo.{table} (OrderId, Code)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'V' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.{table} WITH FULLSCAN;
            """;
        await new ScriptDeployer(Options).DeployAsync(seedRows, DatabaseName);

        var probe = $"DECLARE @p VARCHAR(MAX) = 'V1'; SELECT OrderId FROM dbo.{table} WHERE Code = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("GetRangeWithMismatchedTypes", planXml);
    }
}
