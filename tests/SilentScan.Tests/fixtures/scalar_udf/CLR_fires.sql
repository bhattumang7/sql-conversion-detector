-- Source: Microsoft Learn, "CLR Scalar-Valued Functions - SQL Server"
-- https://learn.microsoft.com/en-us/sql/relational-databases/clr-integration-database-objects-user-defined-functions/clr-scalar-valued-functions
-- CREATE ASSEMBLY FirstUdf / CREATE FUNCTION CountSalesOrderHeader is the article's own worked
-- example verbatim (registers a .NET SVF that accesses data with DataAccessKind.Read), used here
-- to confirm the scalar-UDF catalog registers UdfKind = Clr and never runs the T-SQL-only
-- inlineability blocker scan on a body it has no StatementList for.
CREATE ASSEMBLY FirstUdf
    FROM 'FirstUdf.dll';
GO

CREATE FUNCTION dbo.CountSalesOrderHeader()
RETURNS INT
AS EXTERNAL NAME FirstUdf.T.ReturnOrderCount;
GO

SELECT dbo.CountSalesOrderHeader();
