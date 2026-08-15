-- Near-miss for NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_fires.sql: an inline TVF over a base
-- table, with no scalar UDF anywhere in its own definition - must never fire.
CREATE TABLE dbo.LineItem
(
    LineItemId INT NOT NULL PRIMARY KEY,
    ExtendedPrice DECIMAL(12, 2) NOT NULL
);
GO
CREATE FUNCTION dbo.itvf_LineItemPlain()
RETURNS TABLE
AS
RETURN (SELECT LineItemId, ExtendedPrice FROM dbo.LineItem);
GO

SELECT LineItemId
FROM dbo.itvf_LineItemPlain()
WHERE ExtendedPrice > 100.00;
