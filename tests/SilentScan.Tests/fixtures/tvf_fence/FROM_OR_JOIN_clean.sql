-- Near-miss for FROM_OR_JOIN_fires.sql: textually identical call site
-- (FROM dbo.fn_OrderLines(1) l), but fn_OrderLines is an INLINE TVF here - RETURNS TABLE AS
-- RETURN (SELECT ...), expanded into the calling query exactly like a view, no fence. The point
-- of this pair: only the catalog (sys.objects.type IF vs TF), not the call site, can tell them
-- apart, which is exactly what MUST NOT fire here.
CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL
);
GO
CREATE FUNCTION dbo.fn_OrderLines(@OrderId INT)
RETURNS TABLE
AS
RETURN (SELECT 1 AS LineId, 1 AS Qty);
GO

SELECT o.OrderId, l.LineId
FROM dbo.Orders o
JOIN dbo.fn_OrderLines(1) l ON l.LineId = o.OrderId;
