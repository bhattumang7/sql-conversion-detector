IF NOT EXISTS (SELECT 1 FROM sys.servers WHERE name = N'SILENTSCAN_LOOPBACK')
BEGIN
    EXEC sp_addlinkedserver
        @server = N'SILENTSCAN_LOOPBACK',
        @srvproduct = N'',
        @provider = N'MSOLEDBSQL',
        @datasrc = N'localhost';
    EXEC sp_addlinkedsrvlogin
        @rmtsrvname = N'SILENTSCAN_LOOPBACK',
        @useself = N'True';
END
GO
