-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_nullif_fires.sql: the logically equivalent
-- sargable rewrite. NULLIF(Region, DefaultRegion) = 'US' is NULL (never 'US') whenever
-- Region = DefaultRegion, and equals Region otherwise - so the rewrite is "Region = 'US' AND
-- Region <> DefaultRegion", leaving Region unwrapped. Must NOT fire.
CREATE TABLE dbo.Accounts
(
    Id            INT NOT NULL PRIMARY KEY,
    DefaultRegion VARCHAR(20) NOT NULL,
    Region        VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Accounts_Region ON dbo.Accounts(Region);
GO

SELECT Id
FROM dbo.Accounts
WHERE Region = 'US' AND Region <> DefaultRegion;
