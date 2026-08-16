-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_json_value_fires.sql: an indexed computed
-- column defined as the EXACT SAME JSON_VALUE(Payload, '$.status') expression lets the engine
-- substitute the call and seek on it (SQL Server 2016+). Must NOT fire.
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY,
    Payload NVARCHAR(MAX) NOT NULL,
    StatusVal AS JSON_VALUE(Payload, '$.status')
);
GO
CREATE INDEX IX_Orders_StatusVal ON dbo.Orders(StatusVal);
GO

SELECT OrderId
FROM dbo.Orders
WHERE JSON_VALUE(Payload, '$.status') = 'ACTIVE';
