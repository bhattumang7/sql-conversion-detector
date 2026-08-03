using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Discovery harness for the real-database lineage parity mismatches CLAUDE.md treats as P0
/// (<c>LiveScanResult.LineageParityMismatches</c>, computed by <c>LiveLineageParityChecker</c>
/// diffing every resolved view/TVF column's inferred type against <c>sys.columns</c>). A live
/// scan against a real production database surfaced 25 mismatches across seven type-pair
/// categories (Bit/Int, Int/TinyInt, Int/VarChar, Decimal/Money, Date/DateTime, DateTime/VarChar,
/// Int/DateTime) with no record of the exact defining views - only the aggregate counts. Rather
/// than guess at the exact repro from those counts alone, this sweeps a spread of plausible view
/// shapes per category through the SAME engine-authoritative path a live scan uses
/// (<see cref="EngineAuthoritativeScan"/> - deploy to Docker, read the real catalog/module text
/// back, run the unchanged Lineage/Predicates pipeline) and lets the real oracle decide which
/// shapes actually disagree. A shape that fails here is a genuine reproduction, worth its own
/// fixture and fix; a shape that passes says nothing about whether some OTHER shape in the same
/// category still mismatches on the original database - the real confirmation is a fresh
/// `scan-db` run against that database once every reproduced category here is fixed.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LineageParityShapeSweepTests
{
    // ---- Bit <-> Int -------------------------------------------------------------------

    [Fact]
    public async Task CoalesceBitColumnWithIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS SELECT COALESCE(IsActive, 0) AS IsActive FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task IsNullBitColumnWithIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS SELECT ISNULL(IsActive, 0) AS IsActive FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task CaseOverBitColumnWithIntLiteralElse_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS
                SELECT CASE WHEN 1 = 1 THEN IsActive ELSE 0 END AS IsActive FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task CaseOverIntLiteralWithBitColumnElse_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS
                SELECT CASE WHEN 1 = 1 THEN 0 ELSE IsActive END AS IsActive FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task IifBitColumnWithIntLiteralBranch_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS SELECT IIF(1 = 1, IsActive, 0) AS IsActive FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task BitColumnReadThroughTwoViewLayers_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_FlagsInner AS SELECT ISNULL(IsActive, 0) AS IsActive FROM dbo.Flags;
            GO
            CREATE VIEW dbo.vw_FlagsOuter AS SELECT IsActive FROM dbo.vw_FlagsInner;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task BitColumnCombinedWithAnotherBitColumnAndIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL, IsArchived BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS
                SELECT CASE WHEN IsActive = 1 THEN IsArchived WHEN 1 = 0 THEN 0 ELSE IsArchived END AS Status
                FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Int <-> TinyInt ----------------------------------------------------------------

    [Fact]
    public async Task SumOfTinyIntColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsTotal AS SELECT SUM(Score) AS TotalScore FROM dbo.Ratings;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task AvgOfTinyIntColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsAverage AS SELECT AVG(Score) AS AverageScore FROM dbo.Ratings;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task TinyIntColumnPlusIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsBumped AS SELECT Score + 1 AS BumpedScore FROM dbo.Ratings;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task MinOfTinyIntColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsMin AS SELECT MIN(Score) AS MinScore FROM dbo.Ratings;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task TinyIntColumnReadThroughTwoViewLayers_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsInner AS SELECT Score FROM dbo.Ratings;
            GO
            CREATE VIEW dbo.vw_RatingsOuter AS SELECT Score FROM dbo.vw_RatingsInner;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Int <-> VarChar ------------------------------------------------------------------

    [Fact]
    public async Task IntColumnCastToVarchar_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_OrdersText AS SELECT CAST(OrderId AS VARCHAR(20)) AS OrderIdText FROM dbo.Orders;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task VarcharColumnConcatenatedWithEmptyLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            GO
            CREATE VIEW dbo.vw_OrdersCode AS SELECT Code + '' AS Code FROM dbo.Orders;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task ViewSelectsLiteralStringAlongsideTypedIntColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_OrdersLabeled AS SELECT OrderId, 'order' AS Label FROM dbo.Orders;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task IntColumnConcatenatedAsStringThroughCase_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Code VARCHAR(20) NOT NULL);
            GO
            CREATE VIEW dbo.vw_OrdersDisplay AS
                SELECT CASE WHEN OrderId > 0 THEN Code ELSE CAST(OrderId AS VARCHAR(20)) END AS Display
                FROM dbo.Orders;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Decimal <-> Money ----------------------------------------------------------------

    [Fact]
    public async Task MoneyColumnMultipliedByDecimalLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount MONEY NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesScaled AS SELECT Amount * 1.5 AS ScaledAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task MoneyColumnPlusDecimalColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount MONEY NOT NULL, Tax DECIMAL(9, 2) NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesTotal AS SELECT Amount + Tax AS TotalAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task SumOfMoneyColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount MONEY NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesSum AS SELECT SUM(Amount) AS TotalAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task SmallMoneyColumnPlusDecimalLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount SMALLMONEY NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesAdjusted AS SELECT Amount + 0.5 AS AdjustedAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Date <-> DateTime ------------------------------------------------------------------

    [Fact]
    public async Task DateTimeColumnCastToDate_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventDates AS SELECT CAST(OccurredAt AS DATE) AS OccurredOn FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task DateTimeColumnConvertedToDate_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventDates AS SELECT CONVERT(DATE, OccurredAt) AS OccurredOn FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task DateCastReadThroughOuterViewAlongsideRawDateTimeColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventDatesInner AS
                SELECT OccurredAt, CAST(OccurredAt AS DATE) AS OccurredOn FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_EventDatesOuter AS SELECT OccurredAt, OccurredOn FROM dbo.vw_EventDatesInner;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- DateTime <-> VarChar --------------------------------------------------------------

    [Fact]
    public async Task VarcharColumnComparedToDateLiteralInSameView_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredOnText VARCHAR(10) NOT NULL);
            GO
            CREATE VIEW dbo.vw_RecentEvents AS
                SELECT OccurredOnText FROM dbo.Events WHERE OccurredOnText > '2020-01-01';
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task VarcharDateShapedColumnReadPlainThroughView_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredOnText VARCHAR(10) NOT NULL);
            GO
            CREATE VIEW dbo.vw_Events AS SELECT OccurredOnText FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_RecentEvents AS
                SELECT OccurredOnText FROM dbo.vw_Events WHERE OccurredOnText > '2020-01-01';
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task VarcharColumnCastToDateTimeInOneViewButPlainInAnother_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredOnText VARCHAR(19) NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventsPlain AS SELECT OccurredOnText FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_EventsTyped AS SELECT CAST(OccurredOnText AS DATETIME) AS OccurredAt FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Int <-> DateTime -------------------------------------------------------------------

    [Fact]
    public async Task DateDiffOverDateTimeColumnAlongsideRawColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventAge AS
                SELECT OccurredAt, DATEDIFF(day, OccurredAt, GETDATE()) AS AgeDays FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task YearOfDateTimeColumnAlongsideRawColumnThroughViewLayer_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventsInner AS
                SELECT OccurredAt, YEAR(OccurredAt) AS OccurredYear FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_EventsOuter AS SELECT OccurredAt, OccurredYear FROM dbo.vw_EventsInner;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task DatePartOfDateTimeColumnAlongsideRawColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventParts AS
                SELECT OccurredAt, DATEPART(month, OccurredAt) AS OccurredMonth FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    // ---- Round 2: more exotic shapes, one per category -------------------------------------

    [Fact]
    public async Task NestedCaseOverBitColumnWithIntLiteralBranches_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL, IsArchived BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS
                SELECT CASE WHEN IsArchived = 1 THEN (CASE WHEN IsActive = 1 THEN IsActive ELSE 0 END) ELSE 1 END AS Status
                FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task BitColumnBitwiseAndWithIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Flags AS SELECT IsActive & 1 AS Masked FROM dbo.Flags;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task BitColumnThroughLeftJoinCoalescedWithIntLiteral_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Users (UserId INT NOT NULL);
            GO
            CREATE TABLE dbo.UserFlags (UserId INT NOT NULL, IsActive BIT NOT NULL);
            GO
            CREATE VIEW dbo.vw_UserStatus AS
                SELECT u.UserId, COALESCE(f.IsActive, 0) AS IsActive
                FROM dbo.Users u LEFT JOIN dbo.UserFlags f ON f.UserId = u.UserId;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task GroupedSumOfTinyIntColumnAlongsideGroupingColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (CategoryId INT NOT NULL, Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsByCategory AS
                SELECT CategoryId, SUM(Score) AS TotalScore FROM dbo.Ratings GROUP BY CategoryId;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task TinyIntAggregateReadThroughSubqueryDerivedTable_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Ratings (Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_RatingsTotal AS
                SELECT TotalScore FROM (SELECT SUM(Score) AS TotalScore FROM dbo.Ratings) x;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task TinyIntColumnThroughLeftJoinIsNullDefault_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Users (UserId INT NOT NULL);
            GO
            CREATE TABLE dbo.UserRatings (UserId INT NOT NULL, Score TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_UserScores AS
                SELECT u.UserId, ISNULL(r.Score, 0) AS Score
                FROM dbo.Users u LEFT JOIN dbo.UserRatings r ON r.UserId = u.UserId;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task UnionAllOfVarcharColumnAndCastIntColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            GO
            CREATE TABLE dbo.LegacyOrders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_AllOrderCodes AS
                SELECT Code FROM dbo.Orders
                UNION ALL
                SELECT CAST(OrderId AS VARCHAR(20)) AS Code FROM dbo.LegacyOrders;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task InlineTvfSelectsCastIntColumnAsVarchar_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_OrderCodes() RETURNS TABLE AS RETURN (
                SELECT CAST(OrderId AS VARCHAR(20)) AS OrderCode FROM dbo.Orders
            );
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task MultiStatementTvfReturnsExplicitBitAndIntColumns_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Flags (IsActive BIT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_FlagSummary()
            RETURNS @Result TABLE (IsActive BIT NOT NULL, Total INT NOT NULL)
            AS
            BEGIN
                INSERT INTO @Result SELECT IsActive, COUNT(*) FROM dbo.Flags GROUP BY IsActive;
                RETURN;
            END;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task MoneyColumnDividedByDecimalColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount MONEY NOT NULL, Rate DECIMAL(9, 4) NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesConverted AS SELECT Amount / Rate AS ConvertedAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task CaseMergingMoneyAndDecimalColumns_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Invoices (Amount MONEY NOT NULL, Tax DECIMAL(9, 2) NOT NULL);
            GO
            CREATE VIEW dbo.vw_InvoicesPrimary AS
                SELECT CASE WHEN Amount > 0 THEN Amount ELSE Tax END AS PrimaryAmount FROM dbo.Invoices;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task CaseMergingDateAndDateTimeColumnsThroughViewLayer_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventDatesInner AS
                SELECT OccurredAt, CAST(OccurredAt AS DATE) AS OccurredOn FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_EventDatesMerged AS
                SELECT CASE WHEN OccurredAt > '2020-01-01' THEN OccurredOn ELSE OccurredAt END AS Combined
                FROM dbo.vw_EventDatesInner;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task SmallDateTimeColumnCastToDate_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt SMALLDATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventDates AS SELECT CAST(OccurredAt AS DATE) AS OccurredOn FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task CoalesceDateTimeColumnWithStringLiteralDefault_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NULL);
            GO
            CREATE VIEW dbo.vw_Events AS SELECT COALESCE(OccurredAt, '2020-01-01') AS OccurredAt FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task IsNullDateTimeColumnWithStringLiteralDefault_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NULL);
            GO
            CREATE VIEW dbo.vw_Events AS SELECT ISNULL(OccurredAt, '2020-01-01') AS OccurredAt FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task VarcharColumnThreeViewLayersDeepWithDateComparison_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredOnText VARCHAR(10) NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventsLayer1 AS SELECT OccurredOnText FROM dbo.Events;
            GO
            CREATE VIEW dbo.vw_EventsLayer2 AS SELECT OccurredOnText FROM dbo.vw_EventsLayer1;
            GO
            CREATE VIEW dbo.vw_EventsLayer3 AS
                SELECT OccurredOnText FROM dbo.vw_EventsLayer2 WHERE OccurredOnText >= '2020-01-01';
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task DateDiffBigOverDateTimeColumnAlongsideRawColumn_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventAge AS
                SELECT OccurredAt, DATEDIFF_BIG(second, OccurredAt, GETDATE()) AS AgeSeconds FROM dbo.Events;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }

    [Fact]
    public async Task DateDiffOverDateTimeColumnGroupedThroughSubquery_NoParityMismatch()
    {
        var result = await EngineAuthoritativeScan.RunAsync(
            """
            CREATE TABLE dbo.Events (OccurredAt DATETIME NOT NULL);
            GO
            CREATE VIEW dbo.vw_EventAges AS
                SELECT OccurredAt, AgeDays FROM (
                    SELECT OccurredAt, DATEDIFF(day, OccurredAt, GETDATE()) AS AgeDays FROM dbo.Events
                ) x;
            """);

        Assert.Empty(result.LineageParityMismatches);
    }
}
