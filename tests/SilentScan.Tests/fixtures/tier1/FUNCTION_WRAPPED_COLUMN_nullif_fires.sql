-- No dedicated NULLIF-specific repro/article was found (search performed while implementing
-- this rule); this fixture rests on the same general, well-documented principle every sibling
-- fixture in this file cites - a scalar expression wrapping an indexed column defeats seeking
-- regardless of which specific function/construct does the wrapping ("If You Can't Index It,
-- It's Probably Not SARGable" - Brent Ozar,
-- https://www.brentozar.com/archive/2018/03/cant-index-probably-not-sargable/, discusses
-- ISNULL/COALESCE explicitly and generalizes to "wrapping columns in functions"). NULLIF(a, b)
-- returns NULL when a = b, else a - its result is exactly as opaque to the optimizer as
-- COALESCE's or ISNULL's.
CREATE TABLE dbo.Accounts
(
    Id            INT NOT NULL PRIMARY KEY,
    DefaultRegion VARCHAR(20) NOT NULL,
    Region        VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Accounts_Region ON dbo.Accounts(Region);
GO

SELECT Id
FROM dbo.Accounts
WHERE NULLIF(Region, DefaultRegion) = 'US';
