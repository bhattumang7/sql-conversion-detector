-- Oracle-confirmed (SHOWPLAN_XML against a live instance): a parameterized
-- LIKE pattern always compiles to an attempted Index Seek with a
-- runtime-computed range, even when the pattern turns out to have a leading
-- wildcard at execution time. The optimizer never falls back to a Scan for
-- this shape, so it must not fire as a sargability finding.
CREATE TABLE dbo.Users
(
    UserId      INT           NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40)  NOT NULL
);
GO
CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
GO

CREATE PROCEDURE dbo.usp_FindUsersByNamePattern
    @Pattern NVARCHAR(40)
AS
BEGIN
    SELECT UserId
    FROM dbo.Users
    WHERE DisplayName LIKE @Pattern;
END
GO
