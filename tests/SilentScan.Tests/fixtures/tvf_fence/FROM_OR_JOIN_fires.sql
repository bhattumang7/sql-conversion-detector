-- Source: "Table Valued Function causing performance issue" - SQLServerCentral Forums
-- https://www.sqlservercentral.com/forums/topic/table-valued-function-causing-performance-issue
-- A real-world report of a multi-statement TVF referenced directly in FROM/JOIN poisoning the
-- surrounding plan - the thread's own root cause is exactly the fixed-cardinality-estimate
-- fence this fixture reproduces in miniature: a real base table joined against an MSTVF, whose
-- body the optimizer cannot see into.
CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL
);
GO
CREATE FUNCTION dbo.fn_OrderLines(@OrderId INT)
RETURNS @Lines TABLE (LineId INT, Qty INT)
AS
BEGIN
    INSERT INTO @Lines (LineId, Qty)
    SELECT 1, 1;
    RETURN;
END;
GO

SELECT o.OrderId, l.LineId
FROM dbo.Orders o
JOIN dbo.fn_OrderLines(1) l ON l.LineId = o.OrderId;
