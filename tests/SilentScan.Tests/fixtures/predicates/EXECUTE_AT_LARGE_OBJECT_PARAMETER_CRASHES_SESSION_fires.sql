DECLARE @payload NVARCHAR(MAX) = N'...';
EXEC ('SELECT 1', @payload) AT MyLinkedServer;
