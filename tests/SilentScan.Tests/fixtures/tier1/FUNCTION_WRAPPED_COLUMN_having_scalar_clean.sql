-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_having_scalar_fires.sql: the same date-range
-- rewrite as FUNCTION_WRAPPED_COLUMN_clean.sql, but in a HAVING clause over a grouped raw
-- column instead of WHERE - proves the context-gating rewrite doesn't false-positive on an
-- unwrapped column comparison in HAVING just because it fires on the equivalent
-- YEAR()-wrapped comparison there. Must NOT fire.
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
HAVING SomeDate >= '20200101' AND SomeDate < '20210101';
