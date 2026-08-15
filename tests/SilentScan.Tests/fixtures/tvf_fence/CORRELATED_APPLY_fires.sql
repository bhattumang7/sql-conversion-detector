-- Source: "Cross Apply Executing Too Many Times" - SQLServerCentral Forums
-- https://www.sqlservercentral.com/forums/topic/cross-apply-executing-too-many-times
-- A real report of a CROSS APPLY over a multi-statement TVF re-executing the function body once
-- per outer row, exactly as this fixture reproduces: the argument (o.CustomerId) is a column
-- from the outer table, so the join can only be written as APPLY (SQL Server rejects a plain
-- JOIN whose function argument references a sibling table). Interleaved execution (2017+) is
-- documented as explicitly NOT covering this correlated case (Microsoft Learn: "Introducing
-- Interleaved Execution for Multi-Statement Table Valued Functions").
CREATE TABLE dbo.Orders
(
    OrderId    INT NOT NULL PRIMARY KEY,
    CustomerId INT NOT NULL
);
GO
CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
RETURNS @Tier TABLE (TierName VARCHAR(20))
AS
BEGIN
    INSERT INTO @Tier (TierName)
    SELECT 'Gold';
    RETURN;
END;
GO

SELECT o.OrderId, t.TierName
FROM dbo.Orders o
CROSS APPLY dbo.fn_CustomerTier(o.CustomerId) t;
