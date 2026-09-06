CREATE TABLE dbo.Orders
(
    OrderId     INT          NOT NULL PRIMARY KEY,
    CreatedDate DATETIME     NOT NULL
);
GO
CREATE INDEX IX_Orders_CreatedDate ON dbo.Orders(CreatedDate);
GO

SELECT OrderId
FROM dbo.Orders
WHERE CAST(CreatedDate AS VARCHAR(30)) BETWEEN '2016-02-01' AND '2016-02-08';
