-- Oracle-confirmed against a real SQL Server 2025 engine: with a JSON index on Payload,
-- JSON_VALUE(Payload, '$.status') = 'shipped' still produces a Clustered Index Scan, while
-- JSON_CONTAINS(Payload, 'shipped', '$.status') = 1 against the identical table seeks the
-- JSON index instead.
CREATE TABLE dbo.Orders
(
    OrderId INT NOT NULL PRIMARY KEY,
    Payload JSON NOT NULL
);
CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
GO

SELECT OrderId
FROM dbo.Orders
WHERE JSON_VALUE(Payload, '$.status') = 'shipped';
