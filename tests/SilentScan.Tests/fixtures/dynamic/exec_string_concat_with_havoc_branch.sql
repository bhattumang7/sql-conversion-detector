-- @Name is mutated through an ordinary stored-procedure OUTPUT argument this scanner has no
-- visibility into (an "unmodeled write" - DynamicSqlTransfer's HavocOrTaint helper). @Name's own
-- DECLARE still pins its type (NVARCHAR(50)) as a hard T-SQL guarantee regardless of which value
-- the callee actually assigns, so the concatenation folds to a typed hole SPLICED into the
-- surrounding known SQL text - not a bare taint of the whole EXEC argument, and not a fold that
-- pretends to know @Name's real value either. The predicate is real (DisplayName, an indexed
-- varchar/SQL_* column) with an nvarchar-typed hole standing in for the unknown value, so this
-- still reaches a real, honestly Medium-confidence ScanForced finding.
CREATE TABLE dbo.Customers
(
    CustomerId  INT         NOT NULL PRIMARY KEY,
    DisplayName VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
GO
CREATE INDEX IX_Customers_DisplayName ON dbo.Customers(DisplayName);
GO

CREATE PROCEDURE dbo.usp_FindByComputedName AS
BEGIN
    DECLARE @Name NVARCHAR(50);
    EXEC dbo.usp_ComputeCustomerName @Name OUTPUT;

    DECLARE @sql NVARCHAR(MAX) = N'SELECT CustomerId FROM dbo.Customers WHERE DisplayName = N''' + @Name + N'''';
    EXEC(@sql);
END;
