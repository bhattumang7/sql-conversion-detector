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

        var findings = Extract(
            "CREATE TABLE dbo.Docs (Payload sql_variant NOT NULL, Other sql_variant NOT NULL);",
            "SELECT 1 FROM dbo.Docs WHERE Payload = Other;");

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

        var findings = Extract(
            "CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Status VARCHAR(10) NOT NULL);",
            "SELECT CASE WHEN Code = N'X' THEN 1 ELSE 0 END AS Flag FROM dbo.Orders WHERE Status = 'A';");

        var finding = Assert.Single(findings);
        Assert.Equal("Status", finding.Column.ColumnName);
    }

    [Fact]
    public void Extract_BareIfComparisonOutsideAnyQuery_StillLedgeredAsSkip()
    {

        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Test @Id INT AS BEGIN IF @Id = 1 BEGIN RETURN; END END;");

        Assert.Contains(result.SkippedConstructs, c => c.Reason.Contains("no FROM scope in effect", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SameColumnContradiction_NoFindingAndLedgeredAsNormalizationEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Id = 1 AND Id = 2;");

        Assert.Empty(result.TypedFindings);
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_SiblingConjunctOfAnUnsatisfiableAnd_AlsoEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL, Other INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Id = 1 AND Id = 2 AND Other = 5;");

        Assert.Empty(result.TypedFindings);
        Assert.Equal(3, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_OrDisjunctOutsideTheContradiction_StillReportsNormally()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Id INT NOT NULL, Other INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (Id = 1 AND Id = 2) OR Other = 5;");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("Other", finding.Column.ColumnName);
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_NumericRangeTautologyOnNotNullColumn_EliminatedAsNormalizationDead()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL);",
            "CREATE TABLE dbo.Lines (OrderId INT NOT NULL, Qty INT NOT NULL);",
            "SELECT 1 FROM dbo.Orders o JOIN dbo.Lines l ON o.Id = l.OrderId WHERE l.Qty < 5 OR l.Qty >= 5;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Qty");
        Assert.Equal(2, result.SkippedConstructs.Count(s => s.ConstructKind == "predicate eliminated by normalization"));
    }

    [Fact]
    public void Extract_NumericRangeTautologyOnNullableSideOfLeftOuterJoin_NeverEliminated()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL);",
            "CREATE TABLE dbo.Lines (OrderId INT NOT NULL, Qty INT NOT NULL);",
            "SELECT 1 FROM dbo.Orders o LEFT JOIN dbo.Lines l ON o.Id = l.OrderId WHERE l.Qty < 5 OR l.Qty >= 5;");

        Assert.Equal(2, result.TypedFindings.Count(f => f.Column.ColumnName == "Qty"));
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization");
    }

    [Fact]
    public void Extract_NumericRangeTautologyThroughDerivedTableWrappingLeftOuterJoin_NeverEliminated()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL);",
            "CREATE TABLE dbo.Lines (OrderId INT NOT NULL, Qty INT NOT NULL);",
            """
            SELECT 1
            FROM (SELECT o.Id, l.Qty FROM dbo.Orders o LEFT JOIN dbo.Lines l ON o.Id = l.OrderId) d
            WHERE d.Qty < 5 OR d.Qty >= 5;
            """);

        Assert.Equal(2, result.TypedFindings.Count(f => f.Column.ColumnName == "Qty"));
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization");
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

        var findings = Extract(
            "CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);",
            "SELECT OrderCode FROM dbo.Orders WHERE OrderCode = @UndeclaredParam;");

        var finding = Assert.Single(findings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }

    [Fact]
    public void Extract_IntRoundTrippedThroughTwoViewsAndAProc_ReportsExpressionDerivedNotTyped()
    {

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

        Assert.Equal("CustomerIdAgain = @CustomerId", finding.PredicateFragmentText);
        Assert.Equal("dbo.vw_OrdersRoundTrip", finding.ImmediateRelationQualifiedName);
        Assert.Null(finding.ImmediateRelationAlias);

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

        var findings = ExtractExpressionDerived(
            "CREATE TABLE dbo.Orders (CustomerId INT NOT NULL);",
            "CREATE VIEW dbo.vw_OrderCounts AS SELECT CustomerId, COUNT(*) AS OrderCount FROM dbo.Orders GROUP BY CustomerId;",
            "SELECT CustomerId FROM dbo.vw_OrderCounts WHERE OrderCount = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_OpaqueExpressionWithNoTraceableColumn_NotReported()
    {

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

        Assert.Equal(2, finding.UnderlyingBaseColumns.Count);
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.TableQualifiedName == "dbo.Recent" && c.ColumnName == "Id");
        Assert.Contains(finding.UnderlyingBaseColumns, c => c.TableQualifiedName == "dbo.Archive" && c.ColumnName == "IdStr");
    }

    [Fact]
    public void Extract_UnionViewWithAllPassthroughBranches_NoExpressionDerivedFinding()
    {

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

        var findings = Extract(
            "CREATE TABLE dbo.Recent (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Recent_Code (Code));",
            "CREATE TABLE dbo.Archive (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Archive_Code (Code));",
            "CREATE VIEW dbo.vw_Combined AS SELECT Code FROM dbo.Recent UNION ALL SELECT Code FROM dbo.Archive;",
            "SELECT Code FROM dbo.vw_Combined WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void Extract_QualifierNotInScope_NoFinding_NeverFallsBackToNameOnlyMatch()
    {

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

        Assert.DoesNotContain(findings, f => f.Column.ColumnName == "TrackingCode");
    }

    [Fact]
    public void Extract_InnerScopeAliasShadowsOuterOfSameName_ResolvesToInnerFirst()
    {

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

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE COALESCE(Col, '') = N'x';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "no column operand");
    }

    [Fact]
    public void Extract_InSubqueryWithMultipleOutputColumns_ResolvesUnknownAndLedgers_NotWrongColumn()
    {

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

        var findings = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Other INT NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col IN (1, dbo.fn_NeverDeclared());");

        Assert.Empty(findings.TypedFindings);
        Assert.Contains(findings.SkippedConstructs, s => s.ConstructKind == "IN predicate");
    }

    [Fact]
    public void Extract_NotInList_IsNotAttributedToTypeConversionVerdict()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT IN (N'a', N'b');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT IN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ComparisonBetweenTwoUnresolvedColumnReferences_LedgersDistinctlyFromNoColumnOperand()
    {

        var result = ExtractAll(
            "SELECT * FROM OPENQUERY(RemoteServer, 'SELECT A, B FROM Remote') AS r WHERE r.A = r.B;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "unresolved column comparison");
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "no column operand");
    }

    [Fact]
    public void Extract_ComparisonInsideCaseWhenBranchWithinWhere_NotAFinding_ButLedgered()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE CASE WHEN Col = N'X' THEN 1 ELSE 0 END = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(
            result.SkippedConstructs,
            s => s.ConstructKind == "comparison inside scalar expression" && s.Reason.Contains("CASE/IIF/COALESCE/NULLIF", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ComparisonInsideIifPredicateWithinWhere_NotAFinding_ButLedgered()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE IIF(Col = N'X', 1, 0) = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_CaseInSelectList_StillSilentlyExcluded_NotLedgered()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT CASE WHEN Col = N'X' THEN 1 ELSE 0 END FROM dbo.T;");

        Assert.Empty(result.TypedFindings);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_MergeUpdateSetClauseWithCase_NoFindingFromSetClause_OnAndActionConditionStillFire()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.TargetMergeCase (Id INT NOT NULL, Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Flag INT NOT NULL);",
            "CREATE TABLE dbo.SourceMergeCase (Id INT NOT NULL, Code NVARCHAR(20) NOT NULL);",
            """
            MERGE INTO dbo.TargetMergeCase AS t
            USING dbo.SourceMergeCase AS s
            ON t.Id = s.Id
            WHEN MATCHED AND t.Code = N'y' THEN UPDATE SET t.Flag = CASE WHEN t.Code = N'x' THEN 1 ELSE 0 END;
            """);

        var codeFindings = result.TypedFindings.Where(f => f.Column.ColumnName == "Code").ToList();
        var finding = Assert.Single(codeFindings);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_NotExistsWithInnerComparison_ClassifiesNormally_NotNegated()
    {

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

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT LIKE N'a%';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT LIKE", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedEqualsComparison_IsTreatedAsNotEqual_NotAsEquals()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col = N'a');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("<>", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedNotEqualsComparison_IsTreatedAsEquals()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col <> N'a');");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("=", finding.Operator);
    }

    [Fact]
    public void Extract_DoubleNotWrappedEqualsComparison_ResolvesBackToEquals()
    {

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

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE Col NOT BETWEEN N'a' AND N'z';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT BETWEEN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotWrappedBetween_IsTreatedTheSameAsNotBetweenKeyword()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (Col BETWEEN N'a' AND N'z');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "non-seekable operator" && s.Reason.Contains("NOT BETWEEN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DoubleNotWrappedBetween_ClassifiesAsOrdinaryBetween()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "SELECT Col FROM dbo.T WHERE NOT (NOT (Col BETWEEN N'a' AND N'z'));");

        Assert.Equal(2, result.TypedFindings.Count);
        Assert.All(result.TypedFindings, f => Assert.Equal(Verdict.ScanForced, f.Verdict));
    }

    [Fact]
    public void Extract_IsNullPredicate_ProducesNoFindingAndNoLedgerNoise()
    {

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

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Amount INT NOT NULL); CREATE TABLE dbo.U (Amount INT NOT NULL);",
            "SELECT Amount FROM dbo.T WHERE Amount > ANY (SELECT Amount FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "subquery comparison predicate");
    }

    [Fact]
    public void Extract_ColumnComparedToScalarSubquery_ResolvesSubqueryOutputColumnType()
    {

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

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Amount INT NOT NULL); CREATE TABLE dbo.Wide (A INT NOT NULL, B INT NOT NULL);",
            "SELECT Amount FROM dbo.T WHERE Amount = (SELECT A, B FROM dbo.Wide);");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, finding.Verdict);
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

        Assert.Equal("dbo.vw_Orders", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void Extract_InsteadOfTriggerBody_OnView_InsertedPseudoTable_DoesNotClaimTheBaseColumnsIndex()
    {

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

        var result = ExtractAll(
            "SELECT session_id FROM sys.dm_exec_requests WHERE session_id = 1;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference" && s.Reason.Contains("sys.dm_exec_requests", StringComparison.Ordinal));
    }

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

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(5) NOT NULL);",
            "SELECT 1 FROM dbo.Customers WHERE Code = 'a much longer literal than the column';");

        Assert.Empty(result.OversizedParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToLongerMaxTypedVariable_NeverFires()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(MAX) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.OversizedParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToLongerVariableOfDifferentCategory_NeverFires()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p NVARCHAR(200) = N'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.OversizedParameterFindings);
    }

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

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.Customers WHERE Code = 'x';");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterMaxTypedVariable_NeverFiresUnderLength()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p VARCHAR(MAX) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToShorterVariableOfDifferentCategory_NeverFiresUnderLength()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);",
            "DECLARE @p NVARCHAR(5) = N'ABC'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.UnderLengthParameterFindings);
    }

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

        var result = ExtractAll(
            "CREATE TABLE dbo.Customers (Code VARCHAR(10) NOT NULL);",
            "DECLARE @x VARCHAR(50) = 'ABCDE'; SELECT 1 FROM dbo.Customers WHERE Code = CONVERT(VARCHAR, @x);");

        var finding = Assert.Single(result.OversizedParameterFindings);
        Assert.Equal(10, finding.ColumnLength);
        Assert.Equal(30, finding.OtherOperandLength);
    }

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

        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code = 'abc ';");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_NonPaddedColumnLikeAgainstVariable_NeverFires()
    {

        var findings = ExtractAnsiPaddingMismatch(
            isAnsiPadded: false, "DECLARE @p VARCHAR(20) = 'abc '; SELECT 1 FROM dbo.Customers WHERE Code LIKE @p;");

        Assert.Empty(findings);
    }

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

        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "SELECT 1 FROM dbo.Customers WHERE Status = 'Active';");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_DifferentColumnComparedToVariable_NeverFiresFilteredIndexParameterMismatch()
    {

        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL, Code VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "DECLARE @p VARCHAR(20) = 'X'; SELECT 1 FROM dbo.Customers WHERE Code = @p;");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToVariable_OptionRecompile_StillFires()
    {

        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL);",
            "([Status]='Active')",
            "DECLARE @p VARCHAR(20) = 'Active'; SELECT 1 FROM dbo.Customers WHERE Status = @p OPTION (RECOMPILE);");

        Assert.Single(result.FilteredIndexParameterMismatchFindings);
    }

    [Fact]
    public void Extract_ColumnComparedToVariable_MultiPredicateFilter_NeverGuessesMatch()
    {

        var result = ExtractWithFilteredIndex(
            "CREATE TABLE dbo.Customers (Status VARCHAR(20) NOT NULL, Region VARCHAR(20) NOT NULL);",
            "([Status]='Active' AND [Region]='West')",
            "DECLARE @p VARCHAR(20) = 'Active'; SELECT 1 FROM dbo.Customers WHERE Status = @p;");

        Assert.Empty(result.FilteredIndexParameterMismatchFindings);
    }

    [Theory]
    [InlineData(">", "<=")]
    [InlineData("<", ">=")]
    [InlineData(">=", "<")]
    [InlineData("<=", ">")]
    [InlineData("!<", "<")]
    [InlineData("!>", ">")]
    public void Extract_NegatedComparisonOperator_AppliesCorrectNegation(string operatorText, string expectedNegatedOperator)
    {

        var findings = Extract(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            $"SELECT 1 FROM dbo.T WHERE NOT (Col {operatorText} 5);");

        var finding = Assert.Single(findings);
        Assert.Equal(expectedNegatedOperator, finding.Operator);
    }

    [Fact]
    public void Extract_DeadBetweenWithInvertedBounds_NoFindingAndLedgeredAsNormalizationEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col BETWEEN 10 AND 5;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization");
    }

    [Fact]
    public void Extract_NotBetweenNestedInsideCaseWithinWhere_LedgeredAsOperandPosition()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (CASE WHEN Col NOT BETWEEN 1 AND 10 THEN 1 ELSE 0 END) = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_DeadLikeAsSiblingOfContradictingConjuncts_LedgeredAsNormalizationEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Name VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col IS NULL AND Col IS NOT NULL AND Name LIKE 'x%';");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization" && s.Reason.Contains("contradiction", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NotLikeNestedInsideCaseWithinWhere_LedgeredAsOperandPosition()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (CASE WHEN Col NOT LIKE 'x%' THEN 1 ELSE 0 END) = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression");
    }

    [Fact]
    public void Extract_DeadInAsSiblingOfContradictingConjuncts_LedgeredAsNormalizationEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Name VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col IS NULL AND Col IS NOT NULL AND Name IN ('a', 'b');");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization" && s.Reason.Contains("contradiction", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_InPredicateOutsideAnyFromScope_LedgeredAsSkip()
    {

        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Test @Id INT AS BEGIN IF @Id IN (1, 2, 3) BEGIN RETURN; END END;");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison outside FROM scope");
    }

    [Fact]
    public void Extract_InPredicateNestedInsideCountCaseWithinSelectList_LedgeredWithoutOperandPositionReason()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT COUNT(CASE WHEN Col IN (1, 2) THEN 1 END) FROM dbo.T;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "comparison inside scalar expression" && s.Reason.Contains("IN", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_InPredicateWithArithmeticLeftOperand_NoColumnOperandLedgered()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (Col + 1) IN (1, 2, 3);");

        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Extract_InsertSelectIntoUnresolvedTargetColumn_DoesNotCrashAndReportsNoWriteLoss()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "INSERT INTO dbo.T (BadCol) SELECT 1;");

        Assert.Empty(result.WriteLossFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "write target" && s.Reason.Contains("BadCol", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DeadSubqueryComparisonAsSiblingOfContradictingConjuncts_LedgeredAsNormalizationEliminated()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL, Name VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.U (Code VARCHAR(20) NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col IS NULL AND Col IS NOT NULL AND Name = ANY (SELECT Code FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate eliminated by normalization" && s.Reason.Contains("contradiction", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SubqueryComparisonOutsideAnyFromScope_LedgeredAsSkip()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.U (Id INT NOT NULL);",
            "CREATE PROCEDURE dbo.usp_Test @Id INT AS BEGIN IF @Id = ANY (SELECT Id FROM dbo.U) BEGIN RETURN; END END;");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "comparison outside FROM scope");
    }

    [Fact]
    public void Extract_SubqueryComparisonNestedInsideCountCaseWithinSelectList_NotAFinding()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "CREATE TABLE dbo.U (Id INT NOT NULL);",
            "SELECT COUNT(CASE WHEN Col = ANY (SELECT Id FROM dbo.U) THEN 1 END) FROM dbo.T;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
    }

    [Fact]
    public void Extract_SubqueryComparisonWithArithmeticLeftOperand_NoFinding()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "CREATE TABLE dbo.U (Id INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (Col + 1) = ANY (SELECT Id FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Extract_SubqueryComparisonWhereSubqueryOutputTypeUnresolvable_LedgeredAsSkip()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Name VARCHAR(20) NOT NULL);",
            "CREATE TABLE dbo.U (Id INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Name = ANY (SELECT CAST(Id AS dbo.UnknownAlias) FROM dbo.U);");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "subquery comparison predicate" && s.Reason.Contains("output column type", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_LiteralOnLeftColumnOnRightComparison_StillResolvesAsAFinding()
    {

        var findings = Extract(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE 5 = Col;");

        var finding = Assert.Single(findings);
        Assert.Equal("Col", finding.Column.ColumnName);
    }

    [Fact]
    public void Extract_OrDisjunctWithFoldableArithmeticLiteralComparison_LedgeredAsFoldableNotArbitrary()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col > 5 OR 1 + 1 = 2;");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "foldable literal comparison" && s.Reason.Contains("always true", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_LikeAgainstHexLiteralWithNoQuotes_DoesNotCrashAndIsNotAnsiPaddingMismatch()
    {

        var findings = ExtractAnsiPaddingMismatch(isAnsiPadded: false, "SELECT 1 FROM dbo.Customers WHERE Code LIKE 0x48656C6C6F;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_ComparisonAgainstNextValueForExpression_LedgersUnresolvedOperand()
    {

        var result = ExtractAll(
            "CREATE SEQUENCE dbo.Seq AS INT;",
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col = NEXT VALUE FOR dbo.Seq;");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("NextValueForExpression", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ScalarUdfWithUnresolvableReturnType_LedgersUnresolvedOperand()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            """
            CREATE FUNCTION dbo.Fn() RETURNS dbo.UnknownAlias
            AS
            BEGIN
                RETURN NULL;
            END
            """,
            "SELECT 1 FROM dbo.T WHERE Col = dbo.Fn();");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("RETURNS type could not be resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CastToUnknownTypeAlias_LedgersUnresolvedOperand()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE Col = CAST(Col AS dbo.UnknownAlias);");

        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("CAST/CONVERT target type", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_UnionViewBranchesDisagreeOnType_LedgersInsteadOfGuessing()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.Recent (Id INT NOT NULL);",
            "CREATE TABLE dbo.Archive (Id VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_Combined AS SELECT Id FROM dbo.Recent UNION ALL SELECT Id FROM dbo.Archive;",
            "SELECT 1 FROM dbo.vw_Combined WHERE Id = 5;");

        Assert.Empty(result.TypedFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "predicate operand" && s.Reason.Contains("UNION view whose branches disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SimpleCaseExpressionUsedAsComparisonOperand_DoesNotCrashAndIsNotAttributedToItsInputColumn()
    {

        var result = ExtractAll(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "SELECT 1 FROM dbo.T WHERE (CASE Col WHEN 1 THEN 1 ELSE 0 END) = 1;");

        Assert.DoesNotContain(result.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "no column operand");
    }
}

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

        "CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;",
        "CREATE TABLE dbo.OrdersAlias (OrderId dbo.MyIntAlias NOT NULL);",

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

        Assert.Equal("dbo.OrdersView", finding.Column.TableQualifiedName);
        Assert.Equal("dbo.vw_OrdersView", finding.Column.ImmediateRelationQualifiedName);
        Assert.Equal("OrderCode", finding.Column.ImmediateColumnName);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, findings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task UnionViewWithAllPassthroughBranchesAgreeingOnType_ColumnConverts_ScanForced_OracleConfirmed()
    {

        var findings = Extract(
            "CREATE TABLE dbo.RecentUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.ArchiveUnion (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_CombinedUnion AS SELECT Code FROM dbo.RecentUnion UNION ALL SELECT Code FROM dbo.ArchiveUnion;",
            "SELECT Code FROM dbo.vw_CombinedUnion WHERE Code = N'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.vw_CombinedUnion WHERE Code = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LikeColumnVsNvarcharPattern_ColumnConverts_ScanForced_OracleConfirmed()
    {

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

        Assert.Equal(2, findings.Count);

        Assert.All(findings, f => Assert.Equal(Verdict.SeekPreserved, f.Verdict));

        await AssertNoColumnConversionAsync(findings[0]);
        await AssertNoColumnConversionAsync(findings[1]);
    }

    [Fact]
    public async Task BetweenPredicate_UpperBoundAloneForcesConversion_IsReported_OracleConfirmed()
    {

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

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "TrackingCode");
        Assert.Equal("dbo.ShipmentsQualifier", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CorrelatedExistsSubquery_OuterAliasResolvesThroughScopeChain_OracleConfirmed()
    {

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

        var finding = Assert.Single(findings, f => f.Column.ColumnName == "DisplayName");
        Assert.Equal("dbo.UsersCteShadow", finding.Column.TableQualifiedName);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task CteVisibleInsideNestedSubquery_ResolvesCorrelatedReference_OracleConfirmed()
    {

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

        var probe = "DECLARE @p NVARCHAR(20); SELECT 1 FROM dbo.fn_GetCodesMstvf(1) WHERE Code = @p;";
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InListHomogeneousVarchar_SqlCollation_SeekPreserved_OracleConfirmed()
    {

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

        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_Find @T VARCHAR(20) AS BEGIN SELECT name FROM sys.objects WHERE type_desc = @T; END");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("sys.objects", finding.Column.TableQualifiedName);
        Assert.Equal("type_desc", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        Assert.Null(finding.Column.Indexed);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task PredicateAgainstInformationSchemaColumnsIntColumn_ResolvesAndClassifies_OracleConfirmed()
    {

        var result = ExtractAll(
            "CREATE PROCEDURE dbo.usp_FindColumn @Pos INT AS BEGIN SELECT column_name FROM INFORMATION_SCHEMA.COLUMNS WHERE ORDINAL_POSITION = @Pos; END");

        var finding = Assert.Single(result.TypedFindings);
        Assert.Equal("INFORMATION_SCHEMA.COLUMNS", finding.Column.TableQualifiedName);
        Assert.Equal("ORDINAL_POSITION", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);

        Assert.Null(finding.Column.Indexed);
        Assert.DoesNotContain(result.SkippedConstructs, s => s.ConstructKind == "FROM table reference");

        await AssertNoColumnConversionAsync(finding);
    }

    [Fact]
    public async Task DoubleNotWrappedComparison_ClassifiesTheSameAsTheBareComparison_OracleConfirmed()
    {

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
