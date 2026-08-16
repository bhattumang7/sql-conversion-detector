-- Precision guard: a BETWEEN upper bound whose own fractional-digit count matches the column's
-- declared scale exactly has no precision gap for a row to fall into - must NOT fire, unlike
-- TEMPORAL_BOUNDARY_PRECISION_fires.sql's 3-digit literal against the same DATETIME2(7) column.
CREATE TABLE dbo.Events
(
    EventId    INT NOT NULL PRIMARY KEY,
    OccurredAt DATETIME2(7) NOT NULL
);
GO

SELECT EventId
FROM dbo.Events
WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.9999999';
