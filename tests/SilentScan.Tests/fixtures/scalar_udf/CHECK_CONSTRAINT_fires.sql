-- Source: SQLShack, "Improvements of Scalar User-defined function performance in SQL Server 2019"
-- (Sales.SalesQuantity, the article's own WideWorldImporters-schema worked example)
-- https://www.sqlshack.com/improvements-of-scalar-user-defined-function-performance-in-sql-server-2019/
-- A CHECK constraint referencing a scalar UDF is documented as its own 2019+ inlining blocker
-- (Microsoft Learn: "You don't use the UDF in a computed column or a check constraint
-- definition") and forces serialized validation on every INSERT/UPDATE to the table.
CREATE FUNCTION Sales.SalesQuantity
    (@Description NVARCHAR(100))
RETURNS SMALLINT
AS
BEGIN
    DECLARE @Count SMALLINT
    SELECT @Count = Quantity
    FROM Sales.OrderLines
    WHERE Description = @Description;
    RETURN(@Count)
END;
GO
CREATE TABLE Sales.OrderLines
(
    OrderLineId INT NOT NULL PRIMARY KEY,
    Description NVARCHAR(100) NOT NULL,
    Quantity SMALLINT NOT NULL,
    CONSTRAINT CK_OrderLines_MinQuantity CHECK (Sales.SalesQuantity(Description) > 0)
);
