-- Source: Erik Darling / Darling Data, "SQL Server 2019: What Kind Of Scalar Functions Can't Be
-- Inlined?" (dbo.YearDiff, the same real, cited function used in NOT_INLINEABLE_fires.sql)
-- https://erikdarling.com/sql-server-2019-what-kind-of-functions-cant-be-inlined/
-- A DEFAULT constraint referencing a scalar UDF runs the function on every row-level INSERT that
-- omits the column - real corpus shape (docs/detection-checklist.md: "37 defaults" in the local
-- production copy reference a UDF).
CREATE OR ALTER FUNCTION dbo.YearDiff(@d DATETIME)
RETURNS INT
WITH SCHEMABINDING,
     RETURNS NULL ON NULL INPUT
AS
BEGIN
DECLARE @YearDiff INT;

SET @YearDiff = DATEDIFF(HOUR, @d, GETDATE())

RETURN @YearDiff
END;
GO
CREATE TABLE dbo.Events
(
    EventId INT NOT NULL PRIMARY KEY,
    OccurredAt DATETIME NOT NULL,
    HoursSinceOccurred INT NOT NULL DEFAULT (dbo.YearDiff(GETDATE()))
);
