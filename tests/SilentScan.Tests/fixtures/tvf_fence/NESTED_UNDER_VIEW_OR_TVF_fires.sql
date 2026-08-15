-- Source: "Query Performance and multi-statement table valued functions" - Microsoft Community
-- Hub (SQL Server Support blog)
-- https://techcommunity.microsoft.com/blog/sqlserversupport/query-performance-and-multi-statement-table-valued-functions/316226
-- Documents the "permissions function wrapped in a view" shape this fixture reproduces: a view
-- that looks like an ordinary object at its own call sites secretly wraps a multi-statement TVF,
-- so every consumer of the view inherits the fence invisibly - the call site here
-- (FROM dbo.vw_CustomerTier) names something that reads exactly like a harmless view.
CREATE TABLE dbo.Customers
(
    CustomerId INT NOT NULL PRIMARY KEY,
    Name       VARCHAR(100) NOT NULL
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
CREATE VIEW dbo.vw_CustomerTier
AS
SELECT c.CustomerId, t.TierName
FROM dbo.Customers c
CROSS APPLY dbo.fn_CustomerTier(c.CustomerId) t;
GO

SELECT CustomerId, TierName
FROM dbo.vw_CustomerTier;
