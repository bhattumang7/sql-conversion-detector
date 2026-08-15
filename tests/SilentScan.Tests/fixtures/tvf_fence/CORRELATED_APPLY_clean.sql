-- Near-miss for CORRELATED_APPLY_fires.sql: same correlated CROSS APPLY shape
-- (dbo.fn_CustomerTier(o.CustomerId)), but fn_CustomerTier is an INLINE TVF here - expanded into
-- the calling query like a view, no fence regardless of correlation. Confirms the correlation
-- detector's own precision: a correlated argument alone is not enough to fire, only a
-- correlated argument against a genuinely fencing (multi-statement/CLR) function is.
CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL
);
GO
CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
RETURNS TABLE
AS
RETURN (SELECT 'Gold' AS TierName);
GO

SELECT o.OrderId, t.TierName
FROM dbo.Orders o
CROSS APPLY dbo.fn_CustomerTier(o.CustomerId) t;
