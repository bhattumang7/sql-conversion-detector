-- No confirmed distinct real-world bug report found for this exact shape (only opinion pieces
-- illustrating the general "LEFT(col, n) is not sargable" pattern, no independent user-reported
-- repro) - explicitly authored per CLAUDE.md's rare-exception allowance. The pattern itself
-- (LEFT(col, n) = 'x' with LEN('x') = n, exactly equivalent to col LIKE 'x%') is textbook,
-- well-documented T-SQL.
CREATE TABLE dbo.Products
(
    ProductId INT NOT NULL PRIMARY KEY,
    Sku       VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Products_Sku ON dbo.Products(Sku);
GO

SELECT ProductId
FROM dbo.Products
WHERE LEFT(Sku, 3) = 'ABC';
