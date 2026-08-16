-- Near-miss sibling of CHARINDEX_PREFIX_MATCH_fires.sql, same real-world source (MS Learn forum
-- "Charindex very bad performance"): CHARINDEX(x, col) > 0 is a genuine substring search - it
-- still fires (still wraps the column, still non-sargable), but unlike the = 1 prefix-match
-- case, no sargable rewrite exists for it. Both must fire; only the Detail/remediation differs.
CREATE TABLE dbo.Customers
(
    CustomerId INT NOT NULL PRIMARY KEY,
    Code       VARCHAR(50) NOT NULL
);
GO
CREATE INDEX IX_Customers_Code ON dbo.Customers(Code);
GO

SELECT CustomerId
FROM dbo.Customers
WHERE CHARINDEX('AB', Code) > 0;
