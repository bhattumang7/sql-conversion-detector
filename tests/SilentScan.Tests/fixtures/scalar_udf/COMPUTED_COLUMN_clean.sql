-- Near-miss for COMPUTED_COLUMN_fires.sql: a computed column with an ordinary arithmetic
-- expression and no scalar UDF call anywhere - must never fire.
CREATE TABLE dbo.LineItem
(
    LineItemId INT NOT NULL PRIMARY KEY,
    ExtendedPrice DECIMAL(12, 2) NOT NULL,
    DoubledPrice AS ExtendedPrice * 2
);
