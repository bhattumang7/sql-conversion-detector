CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    OrderDate  DATETIME NOT NULL,
    OrderDay   AS DATEPART(dd, OrderDate)
);
GO
CREATE INDEX IX_Orders_OrderDay ON dbo.Orders(OrderDay);
GO

SELECT OrderId
FROM dbo.Orders
WHERE DAY(OrderDate) = 15;
