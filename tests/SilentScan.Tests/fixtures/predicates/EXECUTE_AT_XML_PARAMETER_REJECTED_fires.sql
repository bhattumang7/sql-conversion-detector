DECLARE @doc XML = '<a/>';
EXEC ('SELECT 1', @doc) AT MyLinkedServer;
