-- Source: "If You Can't Index It, It's Probably Not SARGable" - Brent Ozar
-- https://www.brentozar.com/archive/2018/03/cant-index-probably-not-sargable/
-- The article's own repro wraps two Stack Overflow schema columns in a CTE's SELECT list:
-- COALESCE(p.ClosedDate, p.LastActivityDate) AS LastDate, then filters/joins on that computed
-- column - non-sargable because a COALESCE result can't be seeked through, the same reasoning
-- ISNULL already gets in FUNCTION_WRAPPED_COLUMN_isnull_fires.sql. Simplified here to the
-- minimal WHERE-clause shape Tier-1 actually detects (the sargability-defeating mechanism is
-- identical whether the COALESCE sits in a CTE's SELECT list or directly in a WHERE clause).
CREATE TABLE dbo.Posts
(
    Id                 INT NOT NULL PRIMARY KEY,
    ClosedDate         DATETIME NULL,
    LastActivityDate   DATETIME NOT NULL
);
GO
CREATE INDEX IX_Posts_ClosedDate ON dbo.Posts(ClosedDate);
GO

SELECT Id
FROM dbo.Posts AS p
WHERE COALESCE(p.ClosedDate, p.LastActivityDate) >= '20170101';
