-- The inner query text is fully known (a real predicate against an indexed varchar/SQL_*
-- column), then spliced INSIDE a second layer of dynamic SQL - the outer @outer variable is
-- itself "EXEC('...')" wrapping the inner text, single-quotes doubled so it round-trips as a
-- string literal. The dynamic-SQL engine has to reparse the outer EXEC's own folded text, notice
-- IT contains a further EXEC, and recurse into that nested script's own scope
-- (DynamicSqlPipeline.AnalyzeNested) to reach the predicate two dynamic-SQL layers deep - the
-- same depth concept the static view/TVF lineage resolver already applies to CREATE VIEW layers,
-- applied here to EXEC layers instead.
CREATE TABLE dbo.Customers
(
    CustomerId  INT         NOT NULL PRIMARY KEY,
    DisplayName VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
GO
CREATE INDEX IX_Customers_DisplayName ON dbo.Customers(DisplayName);
GO

CREATE PROCEDURE dbo.usp_FindCustomerNested AS
BEGIN
    DECLARE @inner NVARCHAR(MAX) = N'SELECT CustomerId FROM dbo.Customers WHERE DisplayName = N''Ada''';
    DECLARE @outer NVARCHAR(MAX) = N'EXEC(''' + REPLACE(@inner, N'''', N'''''') + N''')';
    EXEC(@outer);
END;
