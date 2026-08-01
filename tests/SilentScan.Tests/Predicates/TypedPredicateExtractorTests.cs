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

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "Code");
        Assert.Equal("dbo.Target", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
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
}
