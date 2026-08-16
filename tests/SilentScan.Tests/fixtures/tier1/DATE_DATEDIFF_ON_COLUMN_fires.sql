-- No confirmed distinct real-world bug report found for this exact shape (only textbook,
-- near-universal advisory coverage of the general "DATEDIFF/date-function in WHERE forces a
-- scan" pattern) - explicitly authored per CLAUDE.md's rare-exception allowance. The pattern
-- itself is the single largest named-rule opportunity measured in this stream: 1,633 real
-- occurrences of `WHERE DATEDIFF(...)` in the local RM_ test database's own module corpus
-- (aggregate count only, docs/detection-checklist.md), far exceeding every other item this
-- section measured, including ISNULL/COALESCE.
CREATE TABLE dbo.Sessions
(
    SessionId    INT NOT NULL PRIMARY KEY,
    LastActiveAt DATETIME NOT NULL
);
GO
CREATE INDEX IX_Sessions_LastActiveAt ON dbo.Sessions(LastActiveAt);
GO

SELECT SessionId
FROM dbo.Sessions
WHERE DATEDIFF(day, LastActiveAt, GETDATE()) = 0;
