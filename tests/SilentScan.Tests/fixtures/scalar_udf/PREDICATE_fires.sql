-- Source: Microsoft Learn, "Scalar UDF Inlining - SQL Server"
-- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/scalar-udf-inlining
-- The dbo.discount_price function is documented there verbatim (the article's own "single
-- statement scalar UDF" example, measured at 29 minutes vs 1.6 seconds without the UDF on a
-- 10-GB TPC-H database). Placed here in a WHERE clause - the article calls it from a SELECT
-- list instead, but the same per-row/non-sargable cost the article documents applies identically
-- to a predicate use, which is exactly what this fixture's PredicateInvocation kind claims.
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
SELECT LineItemId
FROM dbo.LineItem
WHERE dbo.discount_price(ExtendedPrice, Discount) > 100.00;
