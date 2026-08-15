-- Near-miss for NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_fires.sql: same two-layer inline-TVF-
-- calling-another-function shape, but the inner function is ALSO an inline TVF, not a
-- multi-statement one - no fence anywhere in the chain, so the outer call site must not fire.
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
CREATE FUNCTION dbo.itvf_CustomerTierWrapper(@CustomerId INT)
RETURNS TABLE
AS
RETURN (SELECT TierName FROM dbo.fn_CustomerTier(@CustomerId));
GO

SELECT o.OrderId, t.TierName
FROM dbo.Orders o
CROSS APPLY dbo.itvf_CustomerTierWrapper(o.CustomerId) t;
