using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Lineage;

public sealed class LineageResolverTests
{
    private static (DatabaseCatalog Catalog, LineageCatalog Lineage) Build(params string[] batches)
    {

        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return (catalog, lineage);
    }

    [Fact]
    public void Resolve_SimplePassthroughView_ResolvesToBaseColumn()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var orderCode = view.FindColumn("OrderCode")!;

        var baseColumn = Assert.IsType<ColumnProvenance.BaseColumn>(orderCode.Provenance);
        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName);
        Assert.Equal("OrderCode", baseColumn.ColumnName);
        Assert.Equal(SqlTypeCategory.VarChar, baseColumn.Type!.Category);
        Assert.Equal(0, baseColumn.Depth);
    }

    [Fact]
    public void Resolve_CastOfStringColumnToStringType_PropagatesSourceCollation()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT CAST(OrderCode AS NVARCHAR(50)) AS OrderCodeWide FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var cast = Assert.IsType<ColumnProvenance.Cast>(view.FindColumn("OrderCodeWide")!.Provenance);

        Assert.Equal(SqlTypeCategory.NVarChar, cast.ExplicitType.Category);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", cast.ExplicitType.Collation?.Name);
    }

    [Fact]
    public void Resolve_CastOfNonStringColumnToStringType_LeavesCollationNull()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT CAST(OrderId AS NVARCHAR(20)) AS OrderIdText FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var cast = Assert.IsType<ColumnProvenance.Cast>(view.FindColumn("OrderIdText")!.Provenance);

        Assert.Null(cast.ExplicitType.Collation);
    }

    [Fact]
    public void Resolve_CastInsideView_RecordsCastOriginAndDepthAtWhereItAppears()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_L1 AS SELECT CAST(OrderId AS VARCHAR(10)) AS OrderIdText FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_L2 AS SELECT OrderIdText FROM dbo.vw_L1;");

        var view = lineage.Find("dbo.vw_L2")!;
        var cast = Assert.IsType<ColumnProvenance.Cast>(view.FindColumn("OrderIdText")!.Provenance);

        Assert.Equal(SqlTypeCategory.VarChar, cast.ExplicitType.Category);
        Assert.Equal(1, cast.Depth);
        Assert.NotNull(cast.OriginSourcePath);
    }

    [Fact]
    public void Resolve_FiveDeepViewChain_ResolvesToOriginalBaseColumn()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_L1 AS SELECT OrderId, OrderCode FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_L2 AS SELECT OrderId, OrderCode FROM dbo.vw_L1;",
            "CREATE VIEW dbo.vw_L3 AS SELECT OrderId, OrderCode FROM dbo.vw_L2;",
            "CREATE VIEW dbo.vw_L4 AS SELECT OrderId, OrderCode FROM dbo.vw_L3;",
            "CREATE VIEW dbo.vw_L5 AS SELECT OrderId, OrderCode FROM dbo.vw_L4;");

        var view = lineage.Find("dbo.vw_L5")!;
        var orderCode = view.FindColumn("OrderCode")!;

        var baseColumn = Assert.IsType<ColumnProvenance.BaseColumn>(orderCode.Provenance);
        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", baseColumn.Type!.Collation!.Name);

        Assert.Equal(4, baseColumn.Depth);
    }

    [Fact]
    public void Resolve_UnionBranchesWithMismatchedColumnCounts_DegradesToUnknownAndLedgers()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.OrdersReal (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            """
            CREATE VIEW dbo.vw_MismatchedUnion AS
                SELECT OrderId, OrderCode FROM dbo.OrdersReal
                UNION ALL
                SELECT * FROM dbo.Missing;
            """);

        var view = lineage.Find("dbo.vw_MismatchedUnion")!;

        Assert.All(view.Columns, c => Assert.IsType<ColumnProvenance.Unknown>(c.Provenance));
        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "query expression" && e.Reason.Contains("different column counts", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_MixedCollationUnion_RecordsBothBranchProvenances()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.OrdersUs (OrderId INT NOT NULL, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE TABLE dbo.OrdersEu (OrderId INT NOT NULL, OrderCode VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);",
            """
            CREATE VIEW dbo.vw_AllOrders AS
                SELECT OrderId, OrderCode FROM dbo.OrdersUs
                UNION ALL
                SELECT OrderId, OrderCode FROM dbo.OrdersEu;
            """);

        var view = lineage.Find("dbo.vw_AllOrders")!;
        var orderCode = view.FindColumn("OrderCode")!;

        var union = Assert.IsType<ColumnProvenance.Union>(orderCode.Provenance);
        Assert.Equal(2, union.Branches.Count);

        var first = Assert.IsType<ColumnProvenance.BaseColumn>(union.Branches[0]);
        var second = Assert.IsType<ColumnProvenance.BaseColumn>(union.Branches[1]);
        Assert.True(first.Type!.Collation!.IsSqlFamily);
        Assert.True(second.Type!.Collation!.IsWindowsFamily);
    }

    [Fact]
    public void Resolve_SelectStar_ExpandsAllColumnsInOrder()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL, CreatedAt DATETIME2 NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT * FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;

        Assert.Equal(["OrderId", "OrderCode", "CreatedAt"], view.Columns.Select(c => c.Name));
        Assert.IsType<ColumnProvenance.BaseColumn>(view.Columns[1].Provenance);
    }

    [Fact]
    public void Resolve_ColumnAlias_UsesAliasAsOutputName()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId AS Id FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;

        Assert.NotNull(view.FindColumn("Id"));
        Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("Id")!.Provenance);
    }

    [Fact]
    public void Resolve_ExplicitCastInSelectList_ResolvesToCastProvenance()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT CAST(OrderId AS VARCHAR(10)) AS OrderIdText FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var cast = Assert.IsType<ColumnProvenance.Cast>(view.FindColumn("OrderIdText")!.Provenance);

        Assert.Equal(SqlTypeCategory.VarChar, cast.ExplicitType.Category);
        Assert.Equal(10, cast.ExplicitType.Length);
    }

    [Fact]
    public void Resolve_BuiltinFixedReturnTypeFunctionCallInSelectList_ResolvesInferredType()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (CreatedAt DATETIME2 NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT YEAR(CreatedAt) AS CreatedYear FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("CreatedYear")!.Provenance);

        Assert.NotNull(expr.InferredType);
        Assert.Equal(SqlTypeCategory.Int, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_MinOfTinyIntColumn_OracleVerified_PreservesExactArgumentType()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Qty TINYINT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT MIN(Qty) AS MinQty FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("MinQty")!.Provenance);

        Assert.Equal(SqlTypeCategory.TinyInt, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_MaxOfMoneyColumn_OracleVerified_PreservesExactArgumentType()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Total MONEY NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT MAX(Total) AS MaxTotal FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("MaxTotal")!.Provenance);

        Assert.Equal(SqlTypeCategory.Money, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_SumOfTinyIntColumn_OracleVerified_WidensToInt()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Qty TINYINT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT SUM(Qty) AS TotalQty FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("TotalQty")!.Provenance);

        Assert.Equal(SqlTypeCategory.Int, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_AvgOfSmallIntColumn_OracleVerified_WidensToInt()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Qty SMALLINT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT AVG(Qty) AS AvgQty FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("AvgQty")!.Provenance);

        Assert.Equal(SqlTypeCategory.Int, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_SumOfMoneyColumn_OracleVerified_PreservesMoneyCategory()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Total MONEY NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT SUM(Total) AS TotalSum FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("TotalSum")!.Provenance);

        Assert.Equal(SqlTypeCategory.Money, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_DateAddOnDateColumn_OracleVerified_TakesThirdArgumentType()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (StartDate DATE NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT DATEADD(day, 1, StartDate) AS NextDate FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("NextDate")!.Provenance);

        Assert.Equal(SqlTypeCategory.Date, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_DateAddDayTruncationIdiom_OracleVerified_ResolvesToDateTimeNotInt()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (Placed DATETIME NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT DATEADD(day, DATEDIFF(day, 0, Placed), 0) AS PlacedDate FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("PlacedDate")!.Provenance);

        Assert.Equal(SqlTypeCategory.DateTime, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_DateAddOnStringLiteralBaseDate_OracleVerified_ResolvesToDateTimeNotVarChar()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Shifts (StartMinuteOfDay INT NOT NULL);",
            "CREATE VIEW dbo.vw_Shifts AS SELECT DATEADD(minute, StartMinuteOfDay, '12/30/1899') AS StartAsTime FROM dbo.Shifts;");

        var view = lineage.Find("dbo.vw_Shifts")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("StartAsTime")!.Provenance);

        Assert.Equal(SqlTypeCategory.DateTime, expr.InferredType!.Category);
    }

    [Fact]
    public void Resolve_ScalarUdfCallInSelectList_ResolvesToExpressionWithNoInferredType()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (CreatedAt DATETIME2 NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT dbo.fn_FormatDate(CreatedAt) AS CreatedLabel FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var expr = Assert.IsType<ColumnProvenance.Expression>(view.FindColumn("CreatedLabel")!.Provenance);

        Assert.Null(expr.InferredType);
    }

    [Fact]
    public void Resolve_JoinAcrossTwoTables_ResolvesEachSideCorrectly()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, CustomerId INT NOT NULL);",
            "CREATE TABLE dbo.Customers (CustomerId INT NOT NULL, Name VARCHAR(50) NOT NULL);",
            """
            CREATE VIEW dbo.vw_OrderCustomers AS
                SELECT o.OrderId, c.Name
                FROM dbo.Orders AS o
                JOIN dbo.Customers AS c ON o.CustomerId = c.CustomerId;
            """);

        var view = lineage.Find("dbo.vw_OrderCustomers")!;

        var orderId = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("OrderId")!.Provenance);
        Assert.Equal("dbo.Orders", orderId.TableQualifiedName);

        var name = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("Name")!.Provenance);
        Assert.Equal("dbo.Customers", name.TableQualifiedName);
    }

    [Fact]
    public void Resolve_DerivedTableSubqueryInFrom_ResolvesThroughIt()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            """
            CREATE VIEW dbo.vw_Orders AS
                SELECT d.OrderCode
                FROM (SELECT OrderId, OrderCode FROM dbo.Orders) AS d;
            """);

        var view = lineage.Find("dbo.vw_Orders")!;

        Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("OrderCode")!.Provenance);
    }

    [Fact]
    public void Resolve_CyclicViews_MarksBothCyclicWithUnknownProvenance()
    {
        var (_, lineage) = Build(
            "CREATE VIEW dbo.vw_A AS SELECT Id FROM dbo.vw_B;",
            "CREATE VIEW dbo.vw_B AS SELECT Id FROM dbo.vw_A;");

        Assert.Contains("dbo.vw_A", lineage.CyclicViews);
        Assert.Contains("dbo.vw_B", lineage.CyclicViews);

        var viewA = lineage.Find("dbo.vw_A")!;
        Assert.IsType<ColumnProvenance.Unknown>(viewA.FindColumn("Id")!.Provenance);
    }

    [Fact]
    public void Resolve_InlineTvfCallingAnotherInlineTvf_DeclaredOuterFirst_StillResolvesToBaseColumn()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_Outer() RETURNS TABLE AS RETURN SELECT Col FROM dbo.fn_Inner();",
            "CREATE FUNCTION dbo.fn_Inner() RETURNS TABLE AS RETURN SELECT Col FROM dbo.T;");

        var outer = lineage.Find("dbo.fn_Outer")!;
        var provenance = Assert.IsType<ColumnProvenance.BaseColumn>(outer.FindColumn("Col")!.Provenance);
        Assert.Equal("dbo.T", provenance.TableQualifiedName);
        Assert.Equal(1, provenance.Depth);
    }

    [Fact]
    public void Resolve_CyclicInlineTvfs_DetectedAsCyclicNotSilentlyUnknown()
    {
        var (_, lineage) = Build(
            "CREATE FUNCTION dbo.fn_A() RETURNS TABLE AS RETURN SELECT Id FROM dbo.fn_B();",
            "CREATE FUNCTION dbo.fn_B() RETURNS TABLE AS RETURN SELECT Id FROM dbo.fn_A();");

        Assert.Contains("dbo.fn_A", lineage.CyclicViews);
        Assert.Contains("dbo.fn_B", lineage.CyclicViews);
    }

    [Fact]
    public void Resolve_UnknownBaseTable_ProducesUnknownProvenanceNotAGuess()
    {
        var (_, lineage) = Build("CREATE VIEW dbo.vw_Orphan AS SELECT SomeColumn FROM dbo.NoSuchTable;");

        var view = lineage.Find("dbo.vw_Orphan")!;
        var provenance = Assert.IsType<ColumnProvenance.Unknown>(view.FindColumn("SomeColumn")!.Provenance);
        Assert.Contains("not found", provenance.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_MultiStatementTvf_ReturnsDeclaredProvenance()
    {
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE FUNCTION dbo.fn_GetOrders()
            RETURNS @Result TABLE (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL)
            AS
            BEGIN
                INSERT INTO @Result SELECT OrderId, OrderCode FROM dbo.Orders;
                RETURN;
            END
            """);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);

        var tvf = lineage.Find("dbo.fn_GetOrders")!;
        var declared = Assert.IsType<ColumnProvenance.Declared>(tvf.FindColumn("OrderCode")!.Provenance);
        Assert.Equal(SqlTypeCategory.VarChar, declared.Type.Category);
    }

    [Fact]
    public void Resolve_ExplicitViewColumnList_RenamesOutputColumns()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_Orders (Id, Code) AS SELECT OrderId, OrderCode FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;

        Assert.Equal(["Id", "Code"], view.Columns.Select(c => c.Name));
        Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("Code")!.Provenance);
    }

    [Fact]
    public void Resolve_ExplicitViewColumnListCountMismatch_DegradesEveryColumnToUnknownRatherThanMisattributing()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Known (KnownCol INT NOT NULL);",
            "CREATE VIEW dbo.vw_Mismatched (A, B, C) AS SELECT * FROM dbo.Unknown, dbo.Known;");

        var view = lineage.Find("dbo.vw_Mismatched")!;

        Assert.All(view.Columns, c => Assert.IsType<ColumnProvenance.Unknown>(c.Provenance));
    }

    [Fact]
    public void Resolve_DuplicateFromAliasAcrossSchemas_ResolvesAmbiguousRatherThanLastWins()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.T (Col INT NOT NULL);",
            "CREATE SCHEMA audit;",
            "CREATE TABLE audit.T (Col VARCHAR(10) NOT NULL);",
            "CREATE VIEW dbo.vw_Ambiguous AS SELECT T.Col FROM dbo.T JOIN audit.T ON 1 = 1;");

        var view = lineage.Find("dbo.vw_Ambiguous")!;

        Assert.IsType<ColumnProvenance.Unknown>(view.FindColumn("Col")!.Provenance);
    }

    [Fact]
    public void Resolve_UnionViewReadThroughAnotherView_BumpsDepthOnEveryBranch()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.T1 (Code VARCHAR(10) NOT NULL);",
            "CREATE TABLE dbo.T2 (Code VARCHAR(10) NOT NULL);",
            "CREATE VIEW dbo.vw_Union AS SELECT Code FROM dbo.T1 UNION ALL SELECT Code FROM dbo.T2;",
            "CREATE VIEW dbo.vw_OuterOverUnion AS SELECT Code FROM dbo.vw_Union;");

        var outer = lineage.Find("dbo.vw_OuterOverUnion")!;
        var union = Assert.IsType<ColumnProvenance.Union>(outer.FindColumn("Code")!.Provenance);

        Assert.Equal(2, union.Branches.Count);
        Assert.All(union.Branches, b => Assert.Equal(1, Assert.IsType<ColumnProvenance.BaseColumn>(b).Depth));
    }

    [Fact]
    public void Resolve_ViewRedefinedAcrossFiles_LastDefinitionWinsRatherThanCrashing()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL, Notes VARCHAR(100) NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode, Notes FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;

        Assert.Equal(["OrderId", "OrderCode", "Notes"], view.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Resolve_CteShadowsRealTableOfSameName_ResolvesToCte()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            """
            CREATE VIEW dbo.vw_FromCte AS
            WITH Orders AS (SELECT OrderCode AS Id FROM dbo.Orders)
            SELECT Id FROM Orders;
            """);

        var view = lineage.Find("dbo.vw_FromCte")!;
        var baseColumn = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("Id")!.Provenance);

        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName);
        Assert.Equal("OrderCode", baseColumn.ColumnName);
        Assert.Equal(SqlTypeCategory.VarChar, baseColumn.Type!.Category);
    }

    [Fact]
    public void Resolve_NearMissWithoutCte_StillResolvesToRealTable()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_NoCte AS SELECT OrderCode AS Id FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_NoCte")!;
        var baseColumn = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("Id")!.Provenance);

        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName);
        Assert.Equal("OrderCode", baseColumn.ColumnName);
    }

    [Fact]
    public void Resolve_CteWithExplicitColumnList_RenamesOutputColumns()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            """
            CREATE VIEW dbo.vw_FromCte AS
            WITH Renamed (X, Y) AS (SELECT OrderId, OrderCode FROM dbo.Orders)
            SELECT X, Y FROM Renamed;
            """);

        var view = lineage.Find("dbo.vw_FromCte")!;

        Assert.Equal(["X", "Y"], view.Columns.Select(c => c.Name));
        var xColumn = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("X")!.Provenance);
        Assert.Equal("OrderId", xColumn.ColumnName);
    }

    [Fact]
    public void Resolve_LaterCteReferencesEarlierCteInSameWithClause_Chains()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            """
            CREATE VIEW dbo.vw_Chained AS
            WITH First AS (SELECT OrderId, OrderCode FROM dbo.Orders),
                 Second AS (SELECT OrderCode FROM First)
            SELECT OrderCode FROM Second;
            """);

        var view = lineage.Find("dbo.vw_Chained")!;
        var baseColumn = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("OrderCode")!.Provenance);

        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName);
    }

    [Fact]
    public void Resolve_RecursiveCte_AnchorTypeIsUsedDirectly_IndexClaimDropped()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Employees (EmployeeId INT NOT NULL, ManagerId INT NULL);",
            """
            CREATE VIEW dbo.vw_OrgChart AS
            WITH OrgChart AS (
                SELECT EmployeeId FROM dbo.Employees WHERE ManagerId IS NULL
                UNION ALL
                SELECT e.EmployeeId FROM dbo.Employees AS e JOIN OrgChart AS o ON e.ManagerId = o.EmployeeId
            )
            SELECT EmployeeId FROM OrgChart;
            """);

        var view = lineage.Find("dbo.vw_OrgChart")!;
        var declared = Assert.IsType<ColumnProvenance.Declared>(view.FindColumn("EmployeeId")!.Provenance);

        Assert.Equal(SqlTypeCategory.Int, declared.Type.Category);
        Assert.Equal("OrgChart", declared.TableQualifiedName);
    }

    [Fact]
    public void Resolve_RecursiveCte_RecordsSkipInLedger()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Employees (EmployeeId INT NOT NULL, ManagerId INT NULL);",
            """
            CREATE VIEW dbo.vw_OrgChart AS
            WITH OrgChart AS (
                SELECT EmployeeId FROM dbo.Employees WHERE ManagerId IS NULL
                UNION ALL
                SELECT e.EmployeeId FROM dbo.Employees AS e JOIN OrgChart AS o ON e.ManagerId = o.EmployeeId
            )
            SELECT EmployeeId FROM OrgChart;
            """);

        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "recursive CTE");
    }

    [Fact]
    public void Resolve_AlterView_RedefinesTheViewResolvedFromCreate()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE VIEW dbo.vw_T AS SELECT Col FROM dbo.T;",
            "ALTER VIEW dbo.vw_T AS SELECT CAST(Col AS INT) AS Col FROM dbo.T;");

        var column = Assert.Single(lineage.AllRelations["dbo.vw_T"].Columns);
        Assert.IsType<ColumnProvenance.Cast>(column.Provenance);
    }

    [Fact]
    public void Resolve_CreateOrAlterView_ResolvesIntoLineage()
    {
        var (_, lineage) = Build("CREATE OR ALTER VIEW dbo.vw_T AS SELECT 1 AS Col;");

        var column = Assert.Single(lineage.AllRelations["dbo.vw_T"].Columns);
        Assert.Equal("Col", column.Name);
    }

    [Fact]
    public void Resolve_AlterFunctionReturningTable_RedefinesTheInlineTvfResolvedFromCreate()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);",
            "CREATE FUNCTION dbo.fn_T() RETURNS TABLE AS RETURN SELECT Col FROM dbo.T;",
            "ALTER FUNCTION dbo.fn_T() RETURNS TABLE AS RETURN SELECT CAST(Col AS INT) AS Col FROM dbo.T;");

        var column = Assert.Single(lineage.AllRelations["dbo.fn_T"].Columns);
        Assert.IsType<ColumnProvenance.Cast>(column.Provenance);
    }

    [Fact]
    public void Resolve_CreateOrAlterFunctionReturningTable_ResolvesIntoLineage()
    {
        var (_, lineage) = Build("CREATE OR ALTER FUNCTION dbo.fn_T() RETURNS TABLE AS RETURN SELECT 1 AS Col;");

        var column = Assert.Single(lineage.AllRelations["dbo.fn_T"].Columns);
        Assert.Equal("Col", column.Name);
    }

    [Fact]
    public void Resolve_ClrTableValuedFunction_ResolvesDeclaredReturnShapeLikeAnMstvf()
    {

        var (catalog, lineage) = Build("CREATE FUNCTION dbo.fn_Clr() RETURNS TABLE (Col INT NOT NULL) AS EXTERNAL NAME MyAssembly.[MyClass].[MyMethod];");

        var relation = lineage.AllRelations["dbo.fn_Clr"];
        var column = Assert.Single(relation.Columns);
        Assert.Equal("Col", column.Name);
        Assert.IsType<ColumnProvenance.Declared>(column.Provenance);
        Assert.DoesNotContain(catalog.Skipped.Entries, e => e.ConstructKind == "column type");
    }

    [Fact]
    public void Resolve_DropView_RemovesViewFromLineage()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;",
            "DROP VIEW dbo.vw_Orders;");

        Assert.Null(lineage.Find("dbo.vw_Orders"));
    }

    [Fact]
    public void Resolve_DropViewThenRecreateWithDifferentShape_KeepsTheRecreatedShape()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;",
            "DROP VIEW dbo.vw_Orders;",
            "CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_Orders")!;
        Assert.Null(view.FindColumn("OrderId"));
        Assert.NotNull(view.FindColumn("OrderCode"));
    }

    [Fact]
    public void Resolve_DropFunction_RemovesInlineTvfFromLineage()
    {
        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE FUNCTION dbo.itvf_Orders() RETURNS TABLE AS RETURN SELECT OrderId FROM dbo.Orders;",
            "DROP FUNCTION dbo.itvf_Orders;");

        Assert.Null(lineage.Find("dbo.itvf_Orders"));
    }

    [Fact]
    public void Resolve_FourPartLinkedServerReference_NeverCollidesWithLocalTableOfSameTail()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.vw_Local AS SELECT OrderId FROM dbo.Orders;",
            "CREATE VIEW dbo.vw_Remote AS SELECT OrderId FROM LinkedSrv.SomeDb.dbo.Orders;");

        var localView = lineage.Find("dbo.vw_Local")!;
        Assert.IsType<ColumnProvenance.BaseColumn>(localView.FindColumn("OrderId")!.Provenance);

        var remoteView = lineage.Find("dbo.vw_Remote")!;
        Assert.IsType<ColumnProvenance.Unknown>(remoteView.FindColumn("OrderId")!.Provenance);
    }

    [Fact]
    public void Resolve_ScalarSubqueryInSelectList_DoesNotAttributeInnerScopeColumnToOuterTable()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Amount DECIMAL(9,2) NOT NULL);",
            "CREATE TABLE dbo.Payments (PaymentId INT NOT NULL, Amount DECIMAL(9,2) NOT NULL);",
            "CREATE VIEW dbo.vw_OrdersWithPaymentTotal AS " +
            "SELECT OrderId, (SELECT SUM(Amount) FROM dbo.Payments) AS Total FROM dbo.Orders;");

        var view = lineage.Find("dbo.vw_OrdersWithPaymentTotal")!;
        var total = view.FindColumn("Total")!;

        var underlyingTables = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(total.Provenance)
            .Select(bc => bc.TableQualifiedName)
            .ToList();
        Assert.DoesNotContain("dbo.Orders", underlyingTables);
    }

    [Fact]
    public void Resolve_CteSharingTheEnclosingViewsOwnName_NeverRecordsAFalseSelfCycle()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Orders (OrderId INT NOT NULL);",
            "CREATE VIEW dbo.Foo AS WITH Foo AS (SELECT OrderId FROM dbo.Orders) SELECT OrderId FROM Foo;");

        Assert.DoesNotContain("dbo.Foo", lineage.CyclicViews);
        var view = lineage.Find("dbo.Foo")!;
        Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("OrderId")!.Provenance);
    }

    [Fact]
    public void Resolve_ParenthesizedRecursiveCte_ResolvesAnchorColumnsInsteadOfZero()
    {

        var (_, lineage) = Build(
            "CREATE TABLE dbo.Categories (CategoryCode VARCHAR(20) NOT NULL, ParentCode VARCHAR(20) NULL);",
            """
            CREATE VIEW dbo.vw_Tree AS
            WITH Tree AS (
                (SELECT CategoryCode, ParentCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode FROM dbo.Categories c INNER JOIN Tree t ON c.ParentCode = t.CategoryCode)
            )
            SELECT CategoryCode FROM Tree;
            """);

        var view = lineage.Find("dbo.vw_Tree")!;
        Assert.NotNull(view.FindColumn("CategoryCode"));
    }
}
