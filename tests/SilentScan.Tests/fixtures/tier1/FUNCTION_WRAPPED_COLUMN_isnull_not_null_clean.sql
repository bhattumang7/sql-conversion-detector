-- Oracle-verified precision guard (docs/detection-tasklist.md Tier 1 "Type-aware upgrade of
-- the sargability stream" #1): ISNULL(col, x) on a NOT NULL column is a false positive the
-- blanket function-wrap rule doesn't catch on its own - the optimizer proves
-- ISNULL(NOT-NULL-col, x) = col and simplifies the wrap away entirely, seeking on Age directly,
-- regardless of the default argument's own type. Near-miss sibling of
-- FUNCTION_WRAPPED_COLUMN_isnull_fires.sql (same shape, nullable column, which still fires).
-- Must NOT fire.
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY,
    Age     INT NOT NULL
);
GO
CREATE INDEX IX_Orders_Age ON dbo.Orders(Age);
GO

SELECT OrderId
FROM dbo.Orders
WHERE ISNULL(Age, 0) = 0;
