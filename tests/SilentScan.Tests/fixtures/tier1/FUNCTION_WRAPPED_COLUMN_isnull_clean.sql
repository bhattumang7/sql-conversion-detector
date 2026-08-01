-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_isnull_fires.sql: the sargable rewrite from
-- the same Brent Ozar article this rule is sourced from
-- (https://www.brentozar.com/archive/2018/06/can-non-sargable-predicates-ever-seek/).
-- "Age = 0 OR Age IS NULL" is logically equivalent to ISNULL(Age, 0) = 0 but leaves Age
-- unwrapped on both branches, so the engine can seek. Must NOT fire.
CREATE TABLE dbo.Users
(
    UserId INT NOT NULL PRIMARY KEY,
    Age    INT NULL
);
GO
CREATE INDEX IX_Users_Age ON dbo.Users(Age);
GO

SELECT UserId
FROM dbo.Users AS u
WHERE u.Age = 0 OR u.Age IS NULL;
