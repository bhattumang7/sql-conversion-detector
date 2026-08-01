-- docs/audit-remediation-plan.md Phase 3.1: a JOIN's ON clause is a filter context exactly
-- like WHERE - proves the context-gating rewrite (fire only in WHERE/ON/HAVING) didn't
-- regress ON-clause detection while excluding SELECT-list/GROUP BY/ORDER BY. Same
-- YEAR()-defeats-the-index principle as the WHERE-clause sibling fixture. Must fire.
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
JOIN dbo.Orders AS o ON YEAR(o.CreatedAt) = 2024;
