-- PLANTED: direct table, SQL_* collation, indexed column -> ScanForced, depth 0.
CREATE PROCEDURE dbo.usp_FindUserByName_Fires
    @DisplayName NVARCHAR(40)
AS
BEGIN
    SELECT UserId FROM dbo.Users WHERE DisplayName = @DisplayName;
END
GO

-- CLEAN TWIN: same predicate shape, varchar param matching the column's own family/collation.
CREATE PROCEDURE dbo.usp_FindUserByName_Clean
    @DisplayName VARCHAR(40)
AS
BEGIN
    SELECT UserId FROM dbo.Users WHERE DisplayName = @DisplayName;
END
GO

-- PLANTED: Windows collation, non-indexed column -> RangeSeek, depth 0.
CREATE PROCEDURE dbo.usp_FindUserByRegion_Fires
    @Region NVARCHAR(20)
AS
BEGIN
    SELECT UserId FROM dbo.Users WHERE Region = @Region;
END
GO

-- PLANTED: Tier-1 function-wrapped column (separate finding stream from the verdict engine).
CREATE PROCEDURE dbo.usp_FindUserByCreatedYear_Fires
    @Year INT
AS
BEGIN
    SELECT UserId FROM dbo.Users WHERE YEAR(CreatedAt) = @Year;
END
GO

-- CLEAN TWIN: sargable date-range rewrite of the same intent - must not fire Tier-1.
CREATE PROCEDURE dbo.usp_FindUserByCreatedYear_Clean
    @RangeStart DATETIME,
    @RangeEnd DATETIME
AS
BEGIN
    SELECT UserId FROM dbo.Users WHERE CreatedAt >= @RangeStart AND CreatedAt < @RangeEnd;
END
GO

-- PLANTED: predicate reaches the base column through two view layers -> ScanForced, depth 2.
CREATE PROCEDURE dbo.usp_FindOrderThroughViews_Fires
    @OrderCode NVARCHAR(20)
AS
BEGIN
    SELECT OrderId FROM dbo.vw_OrdersLevel2 WHERE OrderCode = @OrderCode;
END
GO

-- PLANTED: dynamic SQL, string literal only (the analyzable-in-principle case).
CREATE PROCEDURE dbo.usp_DynamicLiteral_Fires
AS
BEGIN
    EXEC('SELECT 1');
END
GO

-- PLANTED: dynamic SQL, literal-only, whose inner predicate is itself a ScanForced finding -
-- proves Tier A actually reparses and analyzes the folded text, not just detects the call site.
CREATE PROCEDURE dbo.usp_DynamicLiteralWithFinding_Fires
AS
BEGIN
    EXEC('SELECT UserId FROM dbo.Users WHERE Email = N''x''');
END
GO

-- PLANTED: dynamic SQL via sp_executesql with a declared parameter type (Tier B) - the
-- classic ORM-generated shape: nvarchar param declared against a varchar/SQL_* column.
CREATE PROCEDURE dbo.usp_DynamicSpExecuteSqlDeclaredParam_Fires
    @Phone NVARCHAR(20)
AS
BEGIN
    EXEC sp_executesql N'SELECT UserId FROM dbo.Users WHERE Phone = @Phone',
        N'@Phone nvarchar(20)', @Phone = @Phone;
END
GO

-- PLANTED: dynamic SQL built via straight-line DECLARE/SET accumulation (Tier C) - no
-- branches between the assignments and the EXEC, so the folded text is provably constant.
CREATE PROCEDURE dbo.usp_DynamicTierCAccumulated_Fires
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'SELECT UserId FROM dbo.Users ';
    SET @sql = @sql + N'WHERE AccountCode = N''x''';
    EXEC(@sql);
END
GO

-- PLANTED: dynamic SQL, variable-driven (the unanalyzable case).
CREATE PROCEDURE dbo.usp_DynamicVariable_Fires
    @Sql NVARCHAR(MAX)
AS
BEGIN
    EXEC(@Sql);
END
GO

-- CLEAN TWIN: an ordinary stored-procedure call is not dynamic SQL.
CREATE PROCEDURE dbo.usp_CallsAnotherProc_Clean
AS
BEGIN
    EXEC dbo.usp_FindUserByName_Clean @DisplayName = 'x';
END
GO
