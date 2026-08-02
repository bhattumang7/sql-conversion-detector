-- No dedicated IIF-specific repro/article was found (search performed while implementing this
-- rule) beyond Microsoft's own T-SQL reference documenting IIF as literal syntactic sugar for
-- a two-branch CASE expression - the same "CASE expressions are not sargable" finding this
-- file's FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql cites (Microsoft Q&A, Erland
-- Sommarskog: https://learn.microsoft.com/en-us/answers/questions/960508/is-case-when-then-sargable)
-- applies identically to its IIF shorthand. Wraps the tested column in IIF's own predicate,
-- mirroring the CASE WHEN-test fixture's shape.
CREATE TABLE dbo.Mobile2
(
    Id           INT NOT NULL PRIMARY KEY,
    MobileNumber VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Mobile2_MobileNumber ON dbo.Mobile2(MobileNumber);
GO

SELECT *
FROM dbo.Mobile2
WHERE IIF(MobileNumber = '123456789', 1, 0) = 1;
