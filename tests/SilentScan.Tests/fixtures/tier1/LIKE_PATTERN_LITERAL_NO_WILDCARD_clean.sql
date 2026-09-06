-- A literal LIKE pattern with no leading wildcard: statically known to be seekable, and no
-- other tier-1 rule fires on it either. Must NOT fire.
CREATE TABLE dbo.Users
(
    UserId      INT           NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40)  NOT NULL
);
GO
CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
GO

SELECT UserId
FROM dbo.Users
WHERE DisplayName LIKE 'John%';
