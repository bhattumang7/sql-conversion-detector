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
        var result = new SqlScriptParser().ParseText("test.sql", sql);
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

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
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

        var finding = Assert.Single(findings);
        // datetime outranks varchar in T-SQL precedence, so the literal bounds convert.
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void Extract_ColumnVsColumnSameType_NoConversionAnywhere_SeekPreserved()
    {
        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, CustomerId INT NOT NULL);",
            "SELECT OrderId FROM dbo.Orders WHERE OrderId = CustomerId;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
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

        // Two independent, correctly-scoped predicates: outer o.OrderId isn't touched here,
        // but the inner l.Qty = 5 must resolve against Lines, not bleed into Orders' scope.
        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Lines", finding.Column.TableQualifiedName);
        Assert.Equal("Qty", finding.Column.ColumnName);
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
}
