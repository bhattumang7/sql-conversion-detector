-- Source: Microsoft Learn, "Scalar UDF Inlining - SQL Server" (dbo.discount_price, the same
-- real, documented function used in PREDICATE_fires.sql)
-- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/scalar-udf-inlining
-- Reproduces the "permissions function wrapped in a view" shape this stream shares with the
-- MSTVF-as-fence stream: a view that looks like an ordinary object at its own call sites secretly
-- calls a scalar UDF in its SELECT list, so every consumer of the view inherits the per-row cost
-- invisibly. The call site below (FROM dbo.vw_LineItemPricing) names something that reads exactly
-- like a harmless view.
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
    LineItemId INT NOT NULL PRIMARY KEY,
    ExtendedPrice DECIMAL(12, 2) NOT NULL,
    Discount DECIMAL(12, 2) NOT NULL
);
GO
CREATE VIEW dbo.vw_LineItemPricing
AS
SELECT LineItemId, dbo.discount_price(ExtendedPrice, Discount) AS DiscountedPrice
FROM dbo.LineItem;
GO

SELECT LineItemId
FROM dbo.vw_LineItemPricing
WHERE DiscountedPrice > 100.00;
