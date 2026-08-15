-- Source: Erik Darling / Darling Data, "SQL Server 2019: What Kind Of Scalar Functions Can't Be
-- Inlined?"
-- https://erikdarling.com/sql-server-2019-what-kind-of-functions-cant-be-inlined/
-- dbo.YearDiff is the article's own worked example of a function SQL Server 2019+ refuses to
-- inline because it calls GETDATE() (a time-dependent intrinsic) - reproduced verbatim, including
-- its WITH SCHEMABINDING clause.
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
    OccurredAt DATETIME NOT NULL
);
GO
SELECT EventId, dbo.YearDiff(OccurredAt)
FROM dbo.Events;
