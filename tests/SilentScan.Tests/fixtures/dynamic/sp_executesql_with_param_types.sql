-- A common ORM/hand-rolled dispatch shape: the predicate text is built once as a literal, and
-- sp_executesql's own @params argument declares the exact parameter type, giving Tier B better
-- type info than most static SQL gets at all. @Name is declared NVARCHAR(50) against
-- DisplayName's VARCHAR(50)/SQL_Latin1_General_CP1_CI_AS column - a genuine column-side
-- conversion (T-SQL precedence converts the LOWER-precedence varchar column, not the nvarchar
-- parameter), so this must resolve ScanForced once folded through the dynamic-SQL pipeline.
CREATE TABLE dbo.Customers
(
    CustomerId  INT         NOT NULL PRIMARY KEY,
    DisplayName VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
GO
CREATE INDEX IX_Customers_DisplayName ON dbo.Customers(DisplayName);
GO

EXEC sp_executesql
    N'SELECT CustomerId FROM dbo.Customers WHERE DisplayName = @Name',
    N'@Name NVARCHAR(50)',
    @Name = N'Ada';
