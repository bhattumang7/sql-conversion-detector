EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'polybase enabled', 1;
RECONFIGURE;
EXEC sp_configure 'hadoop connectivity', 7;
RECONFIGURE;
GO
