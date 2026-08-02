-- Source: "Is CASE..WHEN..THEN sargable ?" - Microsoft Q&A (answered by Erland Sommarskog,
-- SQL Server MVP: "Correct. CASE expressions are not sargable. Very few expressions where an
-- indexed column is entangled in an expression of any sort can lead to Index Seek.")
-- https://learn.microsoft.com/en-us/answers/questions/960508/is-case-when-then-sargable
-- The thread's own measured repro:
--   SELECT * FROM [dbo].[Mobile] WHERE ((CASE WHEN ([MobileNumber] = '123456789')
--     THEN CAST(1 AS BIT) END) = 1)
-- ran as an index scan (199 logical reads) vs. a plain `WHERE MobileNumber = '123456789'`
-- seek (122 logical reads). MobileNumber is wrapped in the CASE's own WHEN test, not its THEN
-- value - the shape this fixture is named for and the one Tier-1 must search for specifically
-- (a naive "only check THEN/ELSE" implementation would miss this exact, documented repro).
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
WHERE (CASE WHEN (MobileNumber = '123456789') THEN CAST(1 AS BIT) END) = 1;
