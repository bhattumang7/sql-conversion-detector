-- Oracle-confirmed (silentscan-sql2025, SHOWPLAN_XML): a parameterized REGEXP_LIKE pattern
-- still compiles to a real Index Seek, with the seek's bounds computed at runtime from the
-- parameter via engine intrinsics. A non-literal pattern is not a reason to flag this predicate.
CREATE TABLE dbo.Users
(
    UserId      INT           NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40)  NOT NULL
);
GO
CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
GO

CREATE PROCEDURE dbo.usp_FindUsersByRegexpPattern
    @Pattern NVARCHAR(40)
AS
BEGIN
    SELECT UserId
    FROM dbo.Users
    WHERE REGEXP_LIKE(DisplayName, @Pattern);
END
GO
