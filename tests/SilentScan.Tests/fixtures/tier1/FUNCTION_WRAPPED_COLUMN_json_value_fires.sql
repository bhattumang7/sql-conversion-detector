-- Corrections-to-shipped-work: JSON_VALUE(col, '$.path') false-positives the blanket
-- function-wrapped-column rule when no matching indexed computed column exists to seek on.
-- No computed column here at all, so this really is non-sargable - must fire.
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY,
    Payload NVARCHAR(MAX) NOT NULL
);
GO

SELECT OrderId
FROM dbo.Orders
WHERE JSON_VALUE(Payload, '$.status') = 'ACTIVE';
