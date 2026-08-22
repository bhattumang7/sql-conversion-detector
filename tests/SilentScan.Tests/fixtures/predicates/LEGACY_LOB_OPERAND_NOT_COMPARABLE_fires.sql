-- Oracle-confirmed against the standing Docker instance: ORDER BY on a TEXT column fails to
-- compile with Msg 306 ("The text, ntext, and image data types cannot be compared or sorted,
-- except when using IS NULL or LIKE operator") every time the statement runs.
CREATE TABLE dbo.Article
(
    ArticleId INT NOT NULL PRIMARY KEY,
    Body      TEXT NOT NULL
);
GO
SELECT ArticleId
FROM dbo.Article
ORDER BY Body;
