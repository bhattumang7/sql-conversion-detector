-- An IF/ELSE where only ONE branch's own assignment actually folds: the THEN branch calls
-- FORMAT, a builtin this scanner deliberately does not whitelist for value-folding (its result
-- is not representable as a literal segment the same way UPPER/LOWER/QUOTENAME's known-shape
-- results are), so that branch stays genuinely unfoldable - not "known shape, unknown value" the
-- way an uninitialized DECLARE or havoc-typed write is, but truly unmodeled. The ELSE branch's
-- own text is a real, complete predicate against an indexed varchar/SQL_* column compared to an
-- nvarchar literal - a genuine column-side conversion. DynamicSqlTransfer/SqlTextValue.Join must
-- recover the ELSE branch's known text as a GuardedAlternative rather than discarding it just
-- because its sibling branch didn't fold, so this predicate must still reach ScanForced.
CREATE TABLE dbo.Customers
(
    CustomerId  INT         NOT NULL PRIMARY KEY,
    DisplayName VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
GO
CREATE INDEX IX_Customers_DisplayName ON dbo.Customers(DisplayName);
GO

CREATE PROCEDURE dbo.usp_FindCustomer @UseUnfoldableLabel BIT AS
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'SELECT CustomerId FROM dbo.Customers WHERE DisplayName = N''Ada''';

    IF @UseUnfoldableLabel = 1
        SET @sql = FORMAT(1, N'a format string this scanner never folds the value of');

    EXEC(@sql);
END;
