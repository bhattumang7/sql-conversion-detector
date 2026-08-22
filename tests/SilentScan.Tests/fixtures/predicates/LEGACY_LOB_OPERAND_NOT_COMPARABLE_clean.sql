-- Near-miss: same table shape as LEGACY_LOB_OPERAND_NOT_COMPARABLE_fires.sql, but ORDER BY sorts
-- on the plain (non-LOB) ArticleId column instead of the TEXT column - stays quiet.
CREATE TABLE dbo.Article
(
    ArticleId INT NOT NULL PRIMARY KEY,
    Body      TEXT NOT NULL
);
GO
SELECT ArticleId
FROM dbo.Article
ORDER BY ArticleId;
