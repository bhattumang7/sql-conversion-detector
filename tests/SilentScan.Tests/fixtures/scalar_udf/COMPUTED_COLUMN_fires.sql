-- Source: Microsoft Learn, "Scalar UDF Inlining - SQL Server" (dbo.discount_price, the same real,
-- documented function used throughout this fixture set)
-- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/scalar-udf-inlining
-- A computed column referencing a scalar UDF is itself one of the documented 2019+ inlining
-- blockers ("You don't use the UDF in a computed column or a check constraint definition") -
-- every query touching the table pays the per-row cost, even one that never selects the column.
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
    Discount DECIMAL(12, 2) NOT NULL,
    DiscountedPrice AS dbo.discount_price(ExtendedPrice, Discount)
);
