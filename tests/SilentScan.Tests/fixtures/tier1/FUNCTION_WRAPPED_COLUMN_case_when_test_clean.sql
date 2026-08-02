-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql: the sargable rewrite
-- the same Microsoft Q&A thread measures as an index seek (122 logical reads) -
-- https://learn.microsoft.com/en-us/answers/questions/960508/is-case-when-then-sargable -
-- the plain, unwrapped comparison with no CASE at all. Must NOT fire.
CREATE TABLE dbo.Mobile
(
    Id           INT NOT NULL PRIMARY KEY,
    MobileNumber VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Mobile_MobileNumber ON dbo.Mobile(MobileNumber);
GO

SELECT *
FROM dbo.Mobile
WHERE MobileNumber = '123456789';
