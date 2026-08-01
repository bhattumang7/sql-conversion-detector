-- docs/audit-remediation-plan.md Phase 3.1: hand-authored to test our own detector. A SELECT-
-- list CASE expression is evaluated per already-located row - there is no index seek for it to
-- lose, since nothing here filters which rows are read. Confirmed as a live false positive
-- against the scanner before this fixture was written (the WHEN condition's YEAR(CreatedAt)
-- comparison fired exactly like a WHERE-clause predicate would, despite being pure output
-- computation). Must NOT fire.
CREATE TABLE dbo.Orders
(
    OrderId   INT      NOT NULL PRIMARY KEY,
    CreatedAt DATETIME NOT NULL
);
GO
CREATE INDEX IX_Orders_CreatedAt ON dbo.Orders(CreatedAt);
GO

SELECT
    OrderId,
    CASE WHEN YEAR(CreatedAt) = 2024 THEN 'this year' ELSE 'earlier' END AS Label
FROM dbo.Orders;
