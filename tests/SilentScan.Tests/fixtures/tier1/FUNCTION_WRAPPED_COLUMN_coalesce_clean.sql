-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_coalesce_fires.sql: the sargable rewrite of
-- COALESCE(ClosedDate, LastActivityDate) >= @x, same rewrite pattern as
-- FUNCTION_WRAPPED_COLUMN_isnull_clean.sql's "Age = 0 OR Age IS NULL". Splitting into the
-- two exhaustive cases (ClosedDate present vs. NULL) leaves ClosedDate unwrapped in both
-- branches, so the engine can seek. Must NOT fire.
CREATE TABLE dbo.Posts
(
    Id                 INT NOT NULL PRIMARY KEY,
    ClosedDate         DATETIME NULL,
    LastActivityDate   DATETIME NOT NULL
);
GO
CREATE INDEX IX_Posts_ClosedDate ON dbo.Posts(ClosedDate);
GO

SELECT Id
FROM dbo.Posts AS p
WHERE (p.ClosedDate IS NOT NULL AND p.ClosedDate >= '20170101')
   OR (p.ClosedDate IS NULL AND p.LastActivityDate >= '20170101');
