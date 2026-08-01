-- Regression fixture for a real crash found during the Phase 4 corpus pilot.
-- Source: olahallengren/SQL-Server-Maintenance-Solution, DatabaseIntegrityCheck.sql,
-- line 754 (commit 660acca46cecc9e94f1078ec4c24442e3f309e0c):
--   IF EXISTS (SELECT * FROM @SelectedCheckCommands GROUP BY CheckCommand HAVING COUNT(*) > 1)
-- COUNT(*)'s argument is a Wildcard ColumnReferenceExpression with a null
-- MultiPartIdentifier (ScriptDOM represents "*" this way, not as a regular column with an
-- empty identifier list) - NonSargablePredicateScanner.ColumnName() dereferenced it
-- unconditionally and crashed the whole scan with a NullReferenceException on every file
-- containing this extremely common pattern. Must NOT fire (COUNT(*) is not "a column"
-- being wrapped) and, more importantly, must not crash.
CREATE TABLE dbo.CheckCommands (CheckCommand VARCHAR(50) NOT NULL);
GO

CREATE PROCEDURE dbo.usp_ValidateCheckCommands
AS
BEGIN
    IF EXISTS (SELECT * FROM dbo.CheckCommands GROUP BY CheckCommand HAVING COUNT(*) > 1)
    BEGIN
        SELECT 1;
    END
END
GO
