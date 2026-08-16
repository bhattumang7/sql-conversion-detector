-- Near-miss sibling of CASE_FOLD_ON_COLUMN_fires.sql: an indexed computed column defined as the
-- EXACT SAME UPPER(Email) expression lets the engine substitute the call and seek on it, the
-- same precision guard already shipped for JSON_VALUE (ComputedColumnMatcher, generalized from
-- JsonComputedColumnMatcher). Must NOT fire.
CREATE TABLE dbo.Users
(
    UserId INT NOT NULL PRIMARY KEY,
    Email  VARCHAR(100) NOT NULL,
    EmailUpper AS UPPER(Email)
);
GO
CREATE INDEX IX_Users_EmailUpper ON dbo.Users(EmailUpper);
GO

SELECT UserId
FROM dbo.Users
WHERE UPPER(Email) = 'USER@EXAMPLE.COM';
