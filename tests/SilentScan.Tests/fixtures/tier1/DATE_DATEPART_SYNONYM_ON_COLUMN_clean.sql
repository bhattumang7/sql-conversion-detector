CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    OrderDate  DATETIME NOT NULL,
    OrderYear  AS DATEPART(yy, OrderDate)
);
GO
CREATE INDEX IX_Orders_OrderYear ON dbo.Orders(OrderYear);
GO

SELECT OrderId
FROM dbo.Orders
WHERE YEAR(OrderDate) = 2024;
