-- Source: Brent Ozar Unlimited, "How Scalar User-Defined Functions Slow Down Queries"
-- https://www.brentozar.com/archive/2020/11/how-scalar-user-defined-functions-slow-down-queries/
-- dbo.FormatUsername is the article's own example, called against the Stack Overflow database's
-- Users table exactly as shown there - per-row execution and forced-serial cost even though this
-- particular call site never appears in a predicate.
CREATE FUNCTION dbo.FormatUsername
    (@DisplayName NVARCHAR(40), @Location NVARCHAR(100))
RETURNS NVARCHAR(200) AS
BEGIN
    DECLARE @Output NVARCHAR(200);
    SET @Output = @DisplayName + N' from ' + COALESCE(@Location, N'Earth, probably');
    RETURN @Output;
END;
GO
CREATE TABLE dbo.Users
(
    Id INT NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40) NOT NULL,
    Location NVARCHAR(100) NULL,
    Reputation INT NOT NULL
);
GO
SELECT TOP 100 dbo.FormatUsername(DisplayName, Location), Reputation, Id
FROM dbo.Users
ORDER BY Reputation DESC;
