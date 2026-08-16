-- Near-miss sibling of DATE_YEAR_ON_COLUMN_fires.sql, per Kendra Little's own article: an
-- indexed computed column defined as the EXACT SAME YEAR(OrderDate) expression lets the engine
-- substitute the call and seek on it (ComputedColumnMatcher). Must NOT fire.
CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    OrderDate  DATETIME NOT NULL,
    OrderYear  AS YEAR(OrderDate)
);
GO
CREATE INDEX IX_Orders_OrderYear ON dbo.Orders(OrderYear);
GO

SELECT OrderId
FROM dbo.Orders
WHERE YEAR(OrderDate) = 2024;
