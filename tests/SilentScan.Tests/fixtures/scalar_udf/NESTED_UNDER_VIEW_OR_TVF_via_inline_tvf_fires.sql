-- Source: Microsoft Learn, "Scalar UDF Inlining - SQL Server" (dbo.discount_price, same real,
-- documented function as NESTED_UNDER_VIEW_OR_TVF_fires.sql)
-- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/scalar-udf-inlining
-- Same nested-cost shape, but reached via inline-TVF function-call syntax (FROM dbo.itvf_...(@x))
-- rather than a bare view name - the call site is textually identical to referencing a harmless
-- inline TVF, so only the lineage pass over the iTVF's own body tells them apart.
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
CREATE FUNCTION dbo.itvf_LineItemPricing()
RETURNS TABLE
AS
RETURN (SELECT LineItemId, dbo.discount_price(ExtendedPrice, Discount) AS DiscountedPrice FROM dbo.LineItem);
GO

SELECT LineItemId
FROM dbo.itvf_LineItemPricing()
WHERE DiscountedPrice > 100.00;
