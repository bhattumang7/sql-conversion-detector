CREATE TABLE dbo.Orders
(
    OrderId     INT             NOT NULL PRIMARY KEY,
    OrderCode   VARCHAR(20)     COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    CreatedAt   DATETIME2(3)    NOT NULL
);
GO
CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
GO

CREATE VIEW dbo.vw_OrdersLevel1
AS
    SELECT OrderId, OrderCode, CreatedAt
    FROM dbo.Orders;
GO

CREATE VIEW dbo.vw_OrdersLevel2
AS
    SELECT OrderId, OrderCode, CreatedAt
    FROM dbo.vw_OrdersLevel1;
GO

-- Phase 1.5 "one binder" binder-parity probe: a CTE named identically to the real base table -
-- a CTE is never schema-qualified, so it always shadows a same-named real base table for its own
-- statement's lifetime. Proves the binder resolves OrderCode through the CTE to the REAL
-- dbo.Orders.OrderCode, not an unrelated table sharing the CTE's name (the exact bug class fixed
-- across the seven scanner migrations this view exists to regression-guard).
CREATE VIEW dbo.vw_CteShadowsRealTable
AS
    WITH Orders AS (SELECT OrderId, OrderCode, CreatedAt FROM dbo.Orders)
    SELECT OrderId, OrderCode
    FROM Orders;
GO
