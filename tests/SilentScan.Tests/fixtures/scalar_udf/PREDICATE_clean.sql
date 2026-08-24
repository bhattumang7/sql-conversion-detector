-- Near-miss for PREDICATE_fires.sql: a built-in function (UPPER) and an unregistered 2-part
-- name in the exact same WHERE position - the "never guess" rule this stream shares with
-- TvfFenceScanner (docs/detection-tasklist.md: "an unresolved call never produces a finding
-- here, rather than guessing either way").
CREATE TABLE dbo.LineItem
(
    LineItemId INT NOT NULL PRIMARY KEY,
    Notes VARCHAR(50) NOT NULL
);
GO
SELECT LineItemId
FROM dbo.LineItem
WHERE UPPER(Notes) = 'X'
  AND dbo.fn_NeverDeclaredAnywhere(LineItemId) = 1;
