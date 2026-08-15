-- Near-miss for NESTED_UNDER_VIEW_OR_TVF_fires.sql: an ordinary view over a base table, with no
-- scalar UDF anywhere in its own definition - must never fire.
CREATE TABLE dbo.LineItem
(
    LineItemId INT NOT NULL PRIMARY KEY,
    ExtendedPrice DECIMAL(12, 2) NOT NULL
);
GO
CREATE VIEW dbo.vw_LineItemPlain
AS
SELECT LineItemId, ExtendedPrice
FROM dbo.LineItem;
GO

SELECT LineItemId
FROM dbo.vw_LineItemPlain
WHERE ExtendedPrice > 100.00;
