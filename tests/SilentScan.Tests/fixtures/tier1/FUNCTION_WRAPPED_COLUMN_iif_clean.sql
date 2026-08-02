-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_iif_fires.sql: the plain, unwrapped equivalent
-- comparison (IIF(MobileNumber = 'x', 1, 0) = 1 is logically equivalent to MobileNumber = 'x'
-- directly). Must NOT fire.
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
WHERE MobileNumber = '123456789';
