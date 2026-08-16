-- Precision guard for FUNCTION_WRAPPED_COLUMN_json_value_clean.sql's suppression: an indexed
-- computed column exists on this table, but over a DIFFERENT JSON path ('$.category') than the
-- predicate below queries ('$.status') - a similar-but-different computed column must not
-- wrongly suppress a real finding. Must still fire.
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY,
    Payload NVARCHAR(MAX) NOT NULL,
    CategoryVal AS JSON_VALUE(Payload, '$.category')
);
GO
CREATE INDEX IX_Orders_CategoryVal ON dbo.Orders(CategoryVal);
GO

SELECT OrderId
FROM dbo.Orders
WHERE JSON_VALUE(Payload, '$.status') = 'ACTIVE';
