-- Near-miss for NOT_INLINEABLE_fires.sql: the same real, cited dbo.discount_price function used
-- throughout this fixture set (Microsoft Learn's own "inlineable" worked example) - a clean body
-- scan must report Unknown, never Inlineable, since a static scan proving nothing found is never
-- proof of inlineability (only the live engine's own is_inlineable flag can assert that).
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
SELECT LineItemId, dbo.discount_price(ExtendedPrice, Discount)
FROM dbo.LineItem;
