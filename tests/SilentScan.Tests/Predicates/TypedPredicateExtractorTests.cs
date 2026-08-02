using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

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
    public void Extract_VarcharColumnVsNVarcharParam_SqlCollation_ScanForced()
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
    }

    [Fact]
    public void Extract_IndexedColumn_IsFlaggedIndexed()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);",
            """
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderId FROM dbo.Orders WHERE OrderCode = @OrderCode;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_LiteralComparison_TypesTheLiteralSide()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "SELECT OrderId FROM dbo.Orders WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        var value = Assert.IsType<PredicateOperand.Value>(finding.OtherOperand);
        Assert.Equal(SqlTypeCategory.Int, value.Type!.Category);
    }

    [Fact]
    public void Extract_LiteralComparison_CarriesLiteralTextForProbeReconstruction()
    {
        // docs/audit-remediation-plan.md Phase 5.2: the finding must carry enough to
        // reconstruct the exact literal later during oracle verification, not just its type.
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);",
            "SELECT DisplayName FROM dbo.Users WHERE DisplayName = N'Alice';");

        var finding = Assert.Single(findings);
        var value = Assert.IsType<PredicateOperand.Value>(finding.OtherOperand);
        Assert.True(value.IsLiteral);
        Assert.Equal("N'Alice'", value.LiteralText);
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
    public void Extract_SysnameVariableVsVarcharColumn_ScanForced()
    {
        // docs/audit-remediation-plan.md Phase 6.2: sysname (nvarchar(128)) outranks varchar in
        // precedence exactly like an ordinary nvarchar parameter would - oracle-verified in
        // SysnameOracleTests.
        var findings = Extract(
            "CREATE TABLE dbo.T (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find
            AS
            BEGIN
                DECLARE @p sysname = N'x';
                SELECT Code FROM dbo.T WHERE Code = @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CatalogedTypeAliasColumn_ResolvesThroughToUnderlyingType()
    {
        var findings = Extract(
            "CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;",
            "CREATE TABLE dbo.Orders (OrderId dbo.MyIntAlias NOT NULL);",
            "SELECT OrderId FROM dbo.Orders WHERE OrderId = 5;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.Equal(SqlTypeCategory.Int, finding.Column.Type!.Category);
    }

    [Fact]
    public void Extract_PredicateThroughViewLayer_CarriesDepthFromLineage()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;",
            """
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.vw_Orders WHERE OrderCode = @OrderCode;
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
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal("dbo.vw_Orders", finding.Column.ImmediateRelationQualifiedName);
        Assert.Equal("OrderCode", finding.Column.ImmediateColumnName);
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
    public void Extract_LikeColumnVsNvarcharPattern_ColumnConverts_ScanForced()
    {
        // The classic ORM-generated pattern: `varcharCol LIKE @nvarcharPattern`. LIKE was
        // previously invisible to the typed pipeline entirely - only Tier-1's wildcard-shape
        // check ran against it, never the type-conversion question.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.Orders WHERE Code LIKE N'ABC%';");

        var finding = Assert.Single(findings);
        Assert.Equal("LIKE", finding.Operator);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_LikeColumnVsVarcharLiteralPattern_NoConversion_SeekPreserved()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);",
            "SELECT Code FROM dbo.Orders WHERE Code LIKE 'ABC%';");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
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
    public void Extract_SameComparisonMovedFromSelectListIntoWhere_NowProducesAFinding()
    {
        // The positive control for the two tests above: the identical comparison, in a genuine
        // filter position, must still fire - proving the gate is scoped to non-filter positions
        // specifically, not a blanket regression.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.Orders WHERE Code = N'X';");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
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
    public void Extract_JoinOnClausePredicate_IsResolved()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (CustomerCode VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Customers (CustomerCode NVARCHAR(10) NOT NULL);",
            """
            SELECT o.CustomerCode
            FROM dbo.Orders o
            JOIN dbo.Customers c ON o.CustomerCode = c.CustomerCode;
            """);

        // Both directions of the join predicate are now classified (the join-direction fix):
        // the varchar side genuinely converts (ScanForced), and the nvarchar side - reported
        // separately - never converts regardless of collation (its own outcome, correctly
        // SeekPreserved, not swallowed by only checking the other column).
        Assert.Equal(2, findings.Count);
        var varcharSide = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.Orders");
        Assert.Equal(Verdict.ScanForced, varcharSide.Verdict);
        var nvarcharSide = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.Customers");
        Assert.Equal(Verdict.SeekPreserved, nvarcharSide.Verdict);
    }

    [Fact]
    public void Extract_HavingClausePredicate_IsResolved()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (CustomerId INT NOT NULL);",
            """
            SELECT CustomerId, COUNT(*)
            FROM dbo.Orders
            GROUP BY CustomerId
            HAVING CustomerId = 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_BetweenPredicate_IsResolved()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderDate DATETIME NOT NULL);",
            "SELECT OrderDate FROM dbo.Orders WHERE OrderDate BETWEEN '20240101' AND '20240201';");

        // BETWEEN decomposes into two independent comparisons (col >= lower AND col <= upper) -
        // both bounds are reported.
        Assert.Equal(2, findings.Count);
        // datetime outranks varchar in T-SQL precedence, so the literal bounds convert.
        Assert.All(findings, f => Assert.Equal(Verdict.SeekPreserved, f.Verdict));
    }

    [Fact]
    public void Extract_BetweenPredicate_UpperBoundAloneForcesConversion_IsReported()
    {
        // Only the upper bound carries a higher-precedence literal (nvarchar) - a scanner that
        // only checked the lower bound would miss this entirely.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Code FROM dbo.Orders WHERE Code BETWEEN 'A' AND N'Z';");

        Assert.Equal(2, findings.Count);
        Assert.Equal(Verdict.SeekPreserved, findings[0].Verdict);
        Assert.Equal(Verdict.ScanForced, findings[1].Verdict);
    }

    [Fact]
    public void Extract_ColumnVsColumnSameType_NoConversionAnywhere_SeekPreserved()
    {
        // A column-vs-column comparison is classified in BOTH directions (the join-predicate
        // fix: `ON a.x = b.y` can convert either side depending on which one has lower
        // precedence, so only checking one side silently misses the other's verdict).
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, CustomerId INT NOT NULL);",
            "SELECT OrderId FROM dbo.Orders WHERE OrderId = CustomerId;");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(Verdict.SeekPreserved, f.Verdict));
        Assert.Equal("OrderId", findings[0].Column.ColumnName);
        Assert.Equal("CustomerId", findings[1].Column.ColumnName);
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
    public void Extract_CorrectQualifier_SameShapeAsAboveNearMiss_ProducesFinding()
    {
        // The near-miss sibling of the test above: same tables, same predicate, but the
        // qualifier ('s') is the real alias - this must still resolve and fire normally, proving
        // the fix rejects only genuinely-unresolvable qualifiers, not qualified references
        // generally.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.Shipments (Id INT NOT NULL, TrackingCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindShipment @p NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.Orders AS o
                JOIN dbo.Shipments AS s ON o.Id = s.Id
                WHERE s.TrackingCode = @p;
            END
            """);

        // Same join-condition SeekPreserved noise as the near-miss above; the TrackingCode
        // predicate is the one under test here.
        var finding = Assert.Single(findings, f => f.Column.ColumnName == "TrackingCode");
        Assert.Equal("dbo.Shipments", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CorrelatedExistsSubquery_OuterAliasResolvesThroughScopeChain()
    {
        // docs/audit-remediation-plan.md Phase 2.2: the EXISTS subquery's own FROM scope (d)
        // is innermost when its WHERE clause is visited; "o.CustomerId" refers to the *outer*
        // query's alias, one level up the scope chain, not anything in the subquery's own FROM
        // clause. Before this fix only the innermost scope was ever consulted, so the outer
        // reference could never resolve at all (Phase 2.1 made that failure produce no finding
        // instead of a wrong one - this test proves it now correctly produces the right one).
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.OrderDetails (OrderId INT NOT NULL, Sku VARCHAR(20) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT o.Id
                FROM dbo.Orders AS o
                WHERE o.CustomerId = @CustomerId
                    AND EXISTS (SELECT 1 FROM dbo.OrderDetails AS d WHERE d.OrderId = o.Id);
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
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
    public void Extract_AlterProcedureAfterCreateStub_UsesAlterProcsOwnParameterType()
    {
        // docs/audit-remediation-plan.md Phase 2.3: the idempotent-deploy pattern seen verbatim
        // in the First Responder Kit corpus repo - a body-less CREATE PROCEDURE stub, then the
        // real body via ALTER PROCEDURE. Before the fix, ALTER PROCEDURE's parameters were never
        // recorded at all (only CreateProcedureStatement/CreateFunctionStatement were handled),
        // so @DisplayName here would resolve to an untyped variable and produce no finding.
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE PROCEDURE dbo.usp_FindUser AS RETURN 0;",
            """
            ALTER PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CreateOrAlterProcedure_UsesOwnParameterType()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE OR ALTER PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_TwoProceduresInSequence_SecondProcDoesNotInheritFirstProcsVariableTypes()
    {
        // The core staleness bug: before the fix, only CreateProcedureStatement/
        // CreateFunctionStatement reset _variables, but every CREATE PROCEDURE already did that
        // correctly - the real gap was ALTER's total non-handling. This test guards the
        // more basic regression (two ordinary CREATE PROCEDUREs in a row must never leak
        // variable types between them) so it can't quietly break again while fixing the ALTER
        // gap above.
        var findings = Extract(
            "CREATE TABLE dbo.Ints (Col INT NOT NULL);",
            "CREATE TABLE dbo.Strings (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_First @Id INT
            AS
            BEGIN
                SELECT Col FROM dbo.Ints WHERE Col = @Id;
            END
            """,
            """
            CREATE PROCEDURE dbo.usp_Second @Id NVARCHAR(20)
            AS
            BEGIN
                SELECT Col FROM dbo.Strings WHERE Col = @Id;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.TableQualifiedName == "dbo.Strings");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_PredicateInsideCte_ResolvesToRealBaseColumn()
    {
        // docs/audit-remediation-plan.md Phase 2.4: the predicate lives inside the CTE body
        // itself, not the outer query - proves CteResolver's own resolution (not just the outer
        // SELECT referencing the finished CTE) goes through the normal typed-predicate pipeline.
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                WITH Matches AS (SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName)
                SELECT DisplayName FROM Matches;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CteNameShadowsRealTable_PredicateAgainstOuterQueryResolvesThroughCte()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Region VARCHAR(10) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                WITH Users AS (SELECT DisplayName FROM dbo.Users WHERE Region = 'US')
                SELECT DisplayName FROM Users WHERE DisplayName = @DisplayName;
            END
            """);

        // Region = 'US' inside the CTE body resolves against the real dbo.Users (VarChar vs a
        // literal - SeekPreserved, filtered out below); the outer DisplayName predicate is
        // against the CTE's own single-column shape, still tracing back to dbo.Users.DisplayName.
        var finding = Assert.Single(findings, f => f.Column.ColumnName == "DisplayName");
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CteVisibleInsideNestedSubquery_ResolvesCorrelatedReference()
    {
        // A CTE is visible for the whole containing statement, including a correlated subquery
        // nested inside the main query - not just the top-level FROM clause.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Flags (OrderId INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Check @CustomerId NVARCHAR(20)
            AS
            BEGIN
                WITH RecentOrders AS (SELECT Id, CustomerId FROM dbo.Orders)
                SELECT Id
                FROM RecentOrders AS ro
                WHERE ro.CustomerId = @CustomerId
                    AND EXISTS (SELECT 1 FROM dbo.Flags AS f WHERE f.OrderId = ro.Id);
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_SameNamedTempTableInTwoProcedures_EachProcedureResolvesItsOwnShape()
    {
        // docs/audit-remediation-plan.md Phase 2.5 "Done when": two procedures with same-named
        // temp tables of different shapes each resolve correctly - proves the scoped catalog
        // lookup Phase 2.5 added reaches all the way through predicate extraction, not just the
        // catalog's own storage (see CatalogBuilderTests for the catalog-level version of this).
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
    public void Extract_UpdateWhereClause_NoFromExtension_ResolvesAgainstTarget()
    {
        // docs/audit-remediation-plan.md Phase 4.1, audit finding B1 ("the single biggest
        // coverage gap in the tool"): UPDATE's WHERE clause previously had no FROM-scope pushed
        // at all, so this predicate was invisible to Pass 3 no matter what it contained.
        var findings = Extract(
            "CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_RenameUser @DisplayName NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = @DisplayName WHERE DisplayName = @DisplayName;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_UpdateWithFromExtension_ResolvesJoinedTableAliases()
    {
        // UPDATE ... FROM ... JOIN ... WHERE - the extended FROM syntax, where the WHERE clause
        // references aliases established only in the FROM clause, not the bare target name.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Flags (OrderId INT NOT NULL, IsStale BIT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_MarkStale @CustomerId NVARCHAR(20)
            AS
            BEGIN
                UPDATE f
                SET f.IsStale = 1
                FROM dbo.Flags AS f
                JOIN dbo.Orders AS o ON o.Id = f.OrderId
                WHERE o.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_DeleteWhereClause_NoFromExtension_ResolvesAgainstTarget()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Sessions (Token VARCHAR(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_ExpireSession @Token NVARCHAR(64)
            AS
            BEGIN
                DELETE FROM dbo.Sessions WHERE Token = @Token;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Sessions", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_DeleteWithFromExtension_ResolvesJoinedTableAliases()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.OrderLines (OrderId INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_PurgeOrderLines @CustomerId NVARCHAR(20)
            AS
            BEGIN
                DELETE ol
                FROM dbo.OrderLines AS ol
                JOIN dbo.Orders AS o ON o.Id = ol.OrderId
                WHERE o.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_MergeOnClause_ResolvesTargetAndSourceAliases()
    {
        // MergeSpecification's own TableReference property is the USING SOURCE, not the INTO
        // target (verified empirically against the real parser output while implementing this -
        // the target's alias lives in the separate TableAlias property). This test pins that
        // both sides resolve correctly regardless of that naming trap.
        var findings = Extract(
            "CREATE TABLE dbo.Target (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Source (Id INT NOT NULL, Code NVARCHAR(20) NOT NULL);",
            """
            MERGE INTO dbo.Target AS t
            USING dbo.Source AS s
            ON t.Code = s.Code
            WHEN MATCHED THEN UPDATE SET t.Id = s.Id
            WHEN NOT MATCHED THEN INSERT (Id, Code) VALUES (s.Id, s.Code);
            """);

        // Both directions of the ON clause's column-vs-column comparison are now reported.
        var targetSide = Assert.Single(findings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.Target");
        Assert.Equal(Verdict.ScanForced, targetSide.Verdict);
        var sourceSide = Assert.Single(findings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.Source");
        Assert.Equal(Verdict.SeekPreserved, sourceSide.Verdict);
    }

    [Fact]
    public void Extract_MergeActionClauseAdditionalCondition_Resolves()
    {
        // WHEN MATCHED AND <extra condition> - the additional predicate on the action clause
        // itself, not just the top-level ON clause, must resolve through the same scope.
        var findings = Extract(
            "CREATE TABLE dbo.Target (Id INT NOT NULL, Status VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Source (Id INT NOT NULL);",
            """
            MERGE INTO dbo.Target AS t
            USING dbo.Source AS s
            ON t.Id = s.Id
            WHEN MATCHED AND t.Status = N'Active' THEN UPDATE SET t.Id = s.Id;
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_CteThenUpdateFrom_CteVisibleInUpdatesFromClause()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, IsFlagged BIT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_FlagOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                WITH TargetOrders AS (SELECT Id, CustomerId FROM dbo.Orders)
                UPDATE o
                SET o.IsFlagged = 1
                FROM dbo.Orders AS o
                JOIN TargetOrders AS t ON t.Id = o.Id
                WHERE t.CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "CustomerId");
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InlineTvfInFromClause_PredicateResolvesToBaseColumnWithDepth()
    {
        // docs/audit-remediation-plan.md Phase 4.2, audit finding B2: FromScopeResolver only
        // handled NamedTableReference and QueryDerivedTable - a table-valued function call in a
        // FROM clause (SchemaObjectFunctionTableReference) fell to the unhandled default and
        // resolved to an empty relation, so a predicate over one of its columns could never
        // trace back to the real base column at all. "Done when": resolves to the base column
        // with depth >= 1, exactly like reading through a view.
        var findings = Extract(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_GetOrders(@Ignored INT) RETURNS TABLE AS RETURN (SELECT Id, CustomerId FROM dbo.Orders);",
            """
            CREATE PROCEDURE dbo.usp_FindOrders @CustomerId NVARCHAR(20)
            AS
            BEGIN
                SELECT Id FROM dbo.fn_GetOrders(1) WHERE CustomerId = @CustomerId;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.True(finding.Column.Depth >= 1);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
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
    public void Extract_MultiStatementTvfInFromClause_UsesDeclaredReturnColumnType()
    {
        // A multi-statement TVF's columns are Declared provenance (its RETURNS @t TABLE(...)
        // shape), not a chain back to a base column - this is the complementary case to the
        // inline-TVF test above, proving both TVF kinds resolve through the FROM clause now.
        var findings = Extract(
            """
            CREATE FUNCTION dbo.fn_GetCodes(@Ignored INT)
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
                SELECT Id FROM dbo.fn_GetCodes(1) WHERE Code = @Code;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(SqlTypeCategory.VarChar, finding.Column.Type!.Category);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_DeclaredTableVariableInFromClause_Resolves()
    {
        // FROM @t parses as VariableTableReference, a distinct ScriptDOM node kind
        // FromScopeResolver never matched at all - it fell through to the same default arm as
        // OPENROWSET/PIVOT (coverage-remediation-plan.md Phase 3.4/3.5's neighbor). This is the
        // ordinary DECLARE @t TABLE(...) case, not the MSTVF return-variable case below.
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
    public void Extract_InListHomogeneousVarchar_SqlCollation_SeekPreserved()
    {
        // Oracle-verified (docs/audit-remediation-plan.md Phase 4.3): a homogeneous varchar IN
        // list against a varchar column produces no conversion at all.
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN ('a', 'b', 'c');");

        var finding = Assert.Single(findings);
        Assert.Equal("IN", finding.Operator);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_InListOneNvarcharLiteralAmongVarchar_SqlCollation_ScanForced()
    {
        // Oracle-verified: a SINGLE higher-precedence literal anywhere in an otherwise-
        // homogeneous list is enough to force the column to convert for the whole comparison -
        // this is the case a naive "type the first element only" implementation would miss.
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN ('a', N'b', 'c');");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InListHomogeneousNvarchar_AgainstVarcharColumn_ScanForced()
    {
        // Oracle-verified: matches ordinary single-comparison precedence (nvarchar outranks
        // varchar), just applied across the whole list.
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN (N'a', N'b', N'c');");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InListWithParameter_ResolvesParameterType()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find @A VARCHAR(20), @B NVARCHAR(20)
            AS
            BEGIN
                SELECT Col FROM dbo.T WHERE Col IN (@A, @B);
            END
            """);

        // nvarchar outranks varchar, so Col converts.
        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InListWithNonLiteralElement_RecordsSkipInsteadOfGuessing()
    {
        var findings = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Other INT NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN (1, Other + 1);");

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "IN predicate");
    }

    [Fact]
    public void Extract_InSubquery_ResolvesSubqueryOutputColumnThroughLineage()
    {
        var findings = Extract(
            """
            CREATE TABLE dbo.Orders (CustomerId VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            CREATE TABLE dbo.Customers (Id NVARCHAR(20) NOT NULL);
            """,
            "SELECT CustomerId FROM dbo.Orders WHERE CustomerId IN (SELECT Id FROM dbo.Customers);");

        var finding = Assert.Single(findings);
        Assert.Equal("CustomerId", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
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

    [Theory]
    [InlineData("!<")]
    [InlineData("!>")]
    public void Extract_NotLessThanAndNotGreaterThan_ClassifyNormally(string sqlOperator)
    {
        // T-SQL folds !< to >= and !> to <= (oracle-verified: identical plan shape, a genuine
        // range seek) - these are NOT non-seekable like <>/NOT IN/NOT LIKE, so they route
        // through the type-conversion verdict machinery exactly like any other comparison.
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            $"SELECT Col FROM dbo.T WHERE Col {sqlOperator} N'a';");

        var finding = Assert.Single(findings);
        Assert.Equal(sqlOperator, finding.Operator);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_TriggerBody_InsertedPseudoTable_ResolvesToTargetTableColumn()
    {
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
        // the default switch arm.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col = dbo.fn_DisplayName(1);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("fn_DisplayName", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ColumnComparedToBuiltinFunctionCall_ResolvesFixedReturnType()
    {
        // BuiltinFunctionTypeResolver's curated, oracle-verified table: GETDATE() types as
        // DATETIME, so a DATETIME column compared against it classifies normally instead of
        // falling to Unknown - the single biggest driver of this tool's Unknown-verdict rate in
        // real corpora before this existed.
        var result = ExtractAll(
            "CREATE TABLE dbo.Orders (CreatedOn DATETIME NOT NULL);",
            "SELECT 1 FROM dbo.Orders WHERE CreatedOn > GETDATE();");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "predicate operand");
    }

    [Fact]
    public void Extract_ColumnComparedToLenOfNvarcharLiteral_MixedCategoryClassifiesNormally()
    {
        // LEN() types as INT (oracle-verified) - an INT column compared against it should
        // classify exactly like any other int-vs-int comparison, not fall to Unknown.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (NameLength INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE NameLength = LEN(N'hello');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnComparedToGlobalVariable_ResolvesFixedType()
    {
        // @@ROWCOUNT types as INT (oracle-verified) - a GlobalVariableExpression previously fell
        // through the same generic default arm as an unhandled function call.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Total INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Total = @@ROWCOUNT;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnComparedToUnknownGlobalVariable_ResolvesUnknownAndLedgers()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Total INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Total = @@CURSOR_ROWS;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("@@CURSOR_ROWS", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ColumnComparedToIsNullOfHigherPrecedenceLiteral_UsesFirstArgumentType()
    {
        // Oracle-verified: ISNULL(check_expression, replacement_value) returns check_expression's
        // own type, even when replacement_value would otherwise outrank it in precedence -
        // ISNULL(@intVar, N'x') still types as int, not nvarchar. Distinct from COALESCE, which
        // CLAUDE.md's hard-cases list calls out separately.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Find @Id INT
            AS
            BEGIN
                SELECT 1 FROM dbo.T WHERE Id = ISNULL(@Id, 0);
            END
            """);

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnComparedToCastToInt_ResolvesTargetType()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            "CREATE TABLE dbo.Raw (Value VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T, dbo.Raw WHERE Id = CAST(Value AS INT);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnComparedToConvertToNvarcharOfVarcharColumn_PropagatesInputCollation()
    {
        // Mirrors Pass 2's identical collation propagation (ScalarExpressionResolver): CAST/
        // CONVERT to a string type has no inline COLLATE syntax, and the real engine propagates
        // the input's own collation into the result. Code carries its OWN explicit (different)
        // collation so ClassifySameCategory's null-collation short-circuit can't fire on either
        // side - only then does a genuinely-different-collation Unknown verdict prove the
        // CONVERT result's collation actually came from Value, not from being left uncollated.
        var result = ExtractAll(
            "CREATE TABLE dbo.T (Code NVARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);",
            "CREATE TABLE dbo.Raw (Value VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T, dbo.Raw WHERE Code = CONVERT(NVARCHAR(20), Value);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }
}
