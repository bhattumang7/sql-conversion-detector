-- docs/audit-remediation-plan.md Phase 3.1: near-miss sibling of
-- FUNCTION_WRAPPED_COLUMN_having_aggregate_clean.sql - a SCALAR function (not an aggregate)
-- wrapping a grouped column in HAVING is exactly as non-sargable as the same wrap in WHERE
-- (same YEAR()-defeats-the-index principle: https://www.brentozar.com/archive/2018/03/cant-index-probably-not-sargable/).
-- Proves the aggregate exclusion is scoped to aggregate function names specifically, not to
-- "any function call found in HAVING". Must fire.
CREATE TABLE dbo.Orders
(
    OrderId  INT      NOT NULL,
    SomeDate DATETIME NOT NULL
);
GO
CREATE INDEX IX_Orders_SomeDate ON dbo.Orders(SomeDate);
GO

SELECT OrderId
FROM dbo.Orders
GROUP BY OrderId, SomeDate
HAVING YEAR(SomeDate) > 2020;
