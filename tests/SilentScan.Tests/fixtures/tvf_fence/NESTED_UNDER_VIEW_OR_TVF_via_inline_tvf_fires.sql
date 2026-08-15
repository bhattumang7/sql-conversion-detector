-- Real shape found on the local test database (RM_AZ_ValleyMetro, not disclosed here per the
-- schema-leak rule beyond its shape): an inline TVF's own body directly names a multi-statement
-- TVF, and a stored procedure elsewhere references the inline TVF via ordinary function-call
-- syntax (FROM dbo.itvf(@x)) - textually indistinguishable from calling the fencing MSTVF
-- directly. This is the case NESTED_UNDER_VIEW_OR_TVF_fires.sql does NOT cover: there the outer
-- call site is a bare NamedTableReference (FROM dbo.SomeView); here it is a
-- SchemaObjectFunctionTableReference (FROM dbo.SomeInlineTvf(@x)) - a distinct ScriptDom node
-- type the scanner must check separately, which an earlier version of this stream did not.
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
CREATE FUNCTION dbo.itvf_CustomerTierWrapper(@CustomerId INT)
RETURNS TABLE
AS
RETURN (SELECT TierName FROM dbo.fn_CustomerTier(@CustomerId));
GO

SELECT o.OrderId, t.TierName
FROM dbo.Orders o
CROSS APPLY dbo.itvf_CustomerTierWrapper(o.CustomerId) t;
