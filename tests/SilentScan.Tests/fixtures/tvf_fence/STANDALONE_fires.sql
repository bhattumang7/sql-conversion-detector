-- Source: "SQL Server Table Valued Function Performance Comparison - Multi-Statement vs Inline"
-- - MSSQLTips
-- https://www.mssqltips.com/sqlservertip/11632/sql-server-table-valued-function-performance-multi-statement-vs-inline/
-- A standalone SELECT against a multi-statement TVF, with nothing else in the FROM clause for
-- the fixed cardinality estimate to poison - the fence and the fabricated estimate are both
-- genuinely present, which is exactly what MSSQLTips' own multi-statement-vs-inline benchmark
-- measures the cost of, but there is no surrounding plan here for the bad estimate to distort.
CREATE FUNCTION dbo.fn_ActiveOrderIds()
RETURNS @Ids TABLE (OrderId INT)
AS
BEGIN
    INSERT INTO @Ids (OrderId)
    SELECT 1;
    RETURN;
END;
GO

SELECT OrderId
FROM dbo.fn_ActiveOrderIds();
