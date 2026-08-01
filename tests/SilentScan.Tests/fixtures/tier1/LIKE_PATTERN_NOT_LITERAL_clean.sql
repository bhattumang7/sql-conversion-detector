-- Near-miss sibling of LIKE_PATTERN_NOT_LITERAL_fires.sql: the same predicate with a literal
-- pattern instead of a parameter. Whether the pattern has a leading wildcard is statically
-- knowable once it's a literal, so this is not the "unanalyzable" case the sibling fixture
-- pins - and this particular literal has no leading wildcard, so no other tier-1 rule fires
-- either. Must NOT fire.
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
