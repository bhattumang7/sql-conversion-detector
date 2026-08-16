-- Source: Microsoft Learn Q&A forum, "Charindex very bad performance" - a real user reporting
-- CHARINDEX(x, col) in a WHERE clause causing a full scan on a large table. This fixture uses
-- the specific = 1 prefix-match shape (rewritable to LIKE 'x%'), the differentiator this rule
-- exists to surface.
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
WHERE CHARINDEX('AB', Code) = 1;
