-- Near-miss for DEFAULT_CONSTRAINT_fires.sql: a plain constant DEFAULT with no scalar UDF call -
-- must never fire.
CREATE TABLE dbo.Events
(
    EventId INT NOT NULL PRIMARY KEY,
    HoursSinceOccurred INT NOT NULL DEFAULT (0)
);
