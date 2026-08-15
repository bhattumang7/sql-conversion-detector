-- Near-miss for NESTED_UNDER_VIEW_OR_TVF_fires.sql: an ordinary view over a real base table,
-- with no multi-statement/CLR TVF anywhere in its own definition - must not be reported as
-- inheriting a fence it never had.
CREATE TABLE dbo.Customers
(
    CustomerId INT NOT NULL PRIMARY KEY,
    Name       VARCHAR(100) NOT NULL
);
GO
CREATE VIEW dbo.vw_CustomerNames
AS
SELECT c.CustomerId, c.Name
FROM dbo.Customers c;
GO

SELECT CustomerId, Name
FROM dbo.vw_CustomerNames;
