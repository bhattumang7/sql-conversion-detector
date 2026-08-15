-- Near-miss for INSERT_EXEC_fires.sql: an ordinary INSERT ... SELECT, no EXEC involved at all -
-- must not fire.
CREATE TABLE dbo.Staging
(
    OrderId INT NOT NULL
);
GO
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY
);
GO

INSERT INTO dbo.Staging (OrderId)
SELECT OrderId FROM dbo.Orders;
