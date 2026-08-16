-- Source: Kendra Little, "Sneaky SQL Server Performance Problems: The Case of the Computed
-- Column Index" - discusses exactly this shape (WHERE YEAR(col) = ...) forcing a scan, and the
-- indexed-computed-column rewrite this stream's own precision guard already recognizes (see
-- DATE_YEAR_ON_COLUMN_clean.sql).
CREATE TABLE dbo.Orders
(
    OrderId   INT NOT NULL PRIMARY KEY,
    OrderDate DATETIME NOT NULL
);
GO
CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);
GO

SELECT OrderId
FROM dbo.Orders
WHERE YEAR(OrderDate) = 2024;
