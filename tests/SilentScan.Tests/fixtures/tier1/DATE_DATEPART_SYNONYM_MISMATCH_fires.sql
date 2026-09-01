CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    OrderDate  DATETIME NOT NULL,
    OrderMonth AS DATEPART(mm, OrderDate)
);
GO
CREATE INDEX IX_Orders_OrderMonth ON dbo.Orders(OrderMonth);
GO

SELECT OrderId
FROM dbo.Orders
WHERE YEAR(OrderDate) = 2024;
