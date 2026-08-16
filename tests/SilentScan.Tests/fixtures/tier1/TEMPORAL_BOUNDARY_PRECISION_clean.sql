-- Near-miss sibling of TEMPORAL_BOUNDARY_PRECISION_fires.sql, from the same Aaron Bertrand
-- article: the precision-correct rewrite (>= start AND < start-of-next-period) has no boundary
-- literal for this rule to compare a fractional-digit count against at all - not a BETWEEN
-- predicate, so it's structurally out of scope for this rule. Must NOT fire.
CREATE TABLE dbo.Events
(
    EventId    INT NOT NULL PRIMARY KEY,
    OccurredAt DATETIME2(7) NOT NULL
);
GO

SELECT EventId
FROM dbo.Events
WHERE OccurredAt >= '2024-01-01' AND OccurredAt < '2025-01-01';
