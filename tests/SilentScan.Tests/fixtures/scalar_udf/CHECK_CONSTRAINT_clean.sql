-- Near-miss for CHECK_CONSTRAINT_fires.sql: an ordinary CHECK with no scalar UDF call - must
-- never fire.
CREATE TABLE Sales.OrderLines
(
    OrderLineId INT NOT NULL PRIMARY KEY,
    Quantity SMALLINT NOT NULL,
    CONSTRAINT CK_OrderLines_MinQuantity CHECK (Quantity > 0)
);
