-- Source: Microsoft Learn, "Scalar UDF Inlining - SQL Server" (dbo.discount_price, the same real,
-- documented function used throughout this fixture set)
-- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/scalar-udf-inlining
-- Called here with two literal arguments and no WITH SCHEMABINDING on the function - the engine
-- can't prove the call deterministic without schemabinding, so it can't constant-fold it even
-- though both arguments are literals.
CREATE FUNCTION dbo.discount_price
(
    @price DECIMAL (12, 2),
    @discount DECIMAL (12, 2)
)
RETURNS DECIMAL (12, 2)
AS
BEGIN
    RETURN @price * (1 - @discount);
END;
GO
CREATE TABLE dbo.LineItem
(
    LineItemId INT NOT NULL PRIMARY KEY
);
GO
SELECT LineItemId, dbo.discount_price(100.00, 0.10)
FROM dbo.LineItem;
