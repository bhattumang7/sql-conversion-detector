-- Near-miss for NON_SCHEMABOUND_CONSTANT_ARGS_fires.sql: same real dbo.discount_price function
-- and the same all-literal call, but WITH SCHEMABINDING added - the engine can now prove the
-- call deterministic, so ConstantArgumentsNotFolded must be false.
CREATE FUNCTION dbo.discount_price
(
    @price DECIMAL (12, 2),
    @discount DECIMAL (12, 2)
)
RETURNS DECIMAL (12, 2)
WITH SCHEMABINDING
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
