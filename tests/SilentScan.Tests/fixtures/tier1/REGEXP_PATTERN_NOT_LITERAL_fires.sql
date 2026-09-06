-- REGEXP_LIKE analogue of LIKE_PATTERN_NOT_LITERAL_fires.sql: when the pattern is a
-- parameter rather than a literal, whether the predicate can seek at all can't be
-- determined statically, so it must be flagged as unanalyzable rather than silently
-- passed as clean.
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
