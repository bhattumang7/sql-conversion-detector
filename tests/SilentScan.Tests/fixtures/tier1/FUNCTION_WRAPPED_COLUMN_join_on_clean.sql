-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_join_on_fires.sql: the same date-range
-- rewrite as FUNCTION_WRAPPED_COLUMN_clean.sql, but in a JOIN's ON clause instead of WHERE -
-- proves the context-gating rewrite doesn't false-positive on an unwrapped column comparison
-- in ON just because it fires on the equivalent wrapped comparison there. Must NOT fire.
CREATE TABLE dbo.Orders
(
    OrderId   INT      NOT NULL PRIMARY KEY,
    CreatedAt DATETIME NOT NULL
);
GO
CREATE TABLE dbo.Shipments
(
    ShipmentId INT NOT NULL PRIMARY KEY,
    OrderId    INT NOT NULL
);
GO
CREATE INDEX IX_Orders_CreatedAt ON dbo.Orders(CreatedAt);
GO

SELECT s.ShipmentId
FROM dbo.Shipments AS s
JOIN dbo.Orders AS o ON o.CreatedAt >= '20240101' AND o.CreatedAt < '20250101';
