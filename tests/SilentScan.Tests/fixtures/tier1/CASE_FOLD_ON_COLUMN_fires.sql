-- No confirmed distinct real-world bug repro found for this exact shape (only general advisory
-- blog posts recommending COLLATE over UPPER()/LOWER() in a predicate, e.g. makolyte's T-SQL
-- performance tips) - explicitly authored per CLAUDE.md's rare-exception allowance for a
-- syntactic-only rule with no internet-sourced repro. The pattern itself (UPPER(col) = 'X') is
-- ubiquitous, textbook non-sargable T-SQL.
CREATE TABLE dbo.Users
(
    UserId INT NOT NULL PRIMARY KEY,
    Email  VARCHAR(100) NOT NULL
);
GO
CREATE INDEX IX_Users_Email ON dbo.Users(Email);
GO

SELECT UserId
FROM dbo.Users
WHERE UPPER(Email) = 'USER@EXAMPLE.COM';
