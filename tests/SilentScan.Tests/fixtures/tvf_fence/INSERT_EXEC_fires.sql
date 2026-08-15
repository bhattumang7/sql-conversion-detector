-- Source: "Eager Spool Performance Problem on Large Table INSERT" - SQLServerCentral Forums
-- https://www.sqlservercentral.com/forums/topic/eager-spool-performance-problem-on-large-table-insert
-- INSERT...EXEC is the same fence family as an MSTVF: the executed procedure's entire result
-- set is forced to be spooled to a worktable before the INSERT can proceed, with the added
-- constraint (unlike an MSTVF) that INSERT...EXEC cannot nest.
CREATE TABLE dbo.Staging
(
    OrderId INT NOT NULL
);
GO
CREATE PROCEDURE dbo.usp_GetOrderIds
AS
BEGIN
    SELECT 1 AS OrderId;
END;
GO

INSERT INTO dbo.Staging (OrderId)
EXEC dbo.usp_GetOrderIds;
