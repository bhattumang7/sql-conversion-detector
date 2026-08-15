-- Near-miss for STANDALONE_fires.sql: same standalone shape (FROM dbo.fn_ActiveOrderIds()) with
-- nothing else joined, but fn_ActiveOrderIds is an INLINE TVF here - expanded like a view, no
-- fence and no fabricated estimate.
CREATE FUNCTION dbo.fn_ActiveOrderIds()
RETURNS TABLE
AS
RETURN (SELECT 1 AS OrderId);
GO

SELECT OrderId
FROM dbo.fn_ActiveOrderIds();
