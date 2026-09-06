using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CaseFoldOnColumnOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanCaseFoldOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public CaseFoldOnColumnOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Users
            (
                DisplayNameCi NVARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                DisplayNameCs NVARCHAR(40) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL
            );
            GO
            CREATE INDEX IX_Users_DisplayNameCi ON dbo.Users(DisplayNameCi);
            GO
            CREATE INDEX IX_Users_DisplayNameCs ON dbo.Users(DisplayNameCs);
            GO
            INSERT INTO dbo.Users(DisplayNameCi, DisplayNameCs)
            SELECT TOP (5000) 'Name' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10)),
                   'Name' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            GO
            UPDATE STATISTICS dbo.Users WITH FULLSCAN;
            GO
            CREATE PROCEDURE dbo.ProbeUpperCi @x NVARCHAR(40) AS
            BEGIN
                SELECT DisplayNameCi FROM dbo.Users WHERE UPPER(DisplayNameCi) = @x;
            END
            GO
            CREATE PROCEDURE dbo.ProbeUpperCs @x NVARCHAR(40) AS
            BEGIN
                SELECT DisplayNameCs FROM dbo.Users WHERE UPPER(DisplayNameCs) = @x;
            END
            GO
            CREATE PROCEDURE dbo.ProbeBareColumn @x NVARCHAR(40) AS
            BEGIN
                SELECT DisplayNameCi FROM dbo.Users WHERE DisplayNameCi = @x;
            END
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private async Task<bool> HasIndexSeek(string probe, string indexName)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return IndexAccessDetector.HasIndexSeek(planXml, indexName);
    }

    [Fact]
    public async Task UpperOnCaseInsensitiveColumn_StillNeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeUpperCi @x = N'NAME1';", "IX_Users_DisplayNameCi"));

    [Fact]
    public async Task UpperOnCaseSensitiveColumn_NeverSeeks() =>
        Assert.False(await HasIndexSeek("EXEC dbo.ProbeUpperCs @x = N'NAME1';", "IX_Users_DisplayNameCs"));

    [Fact]
    public async Task BareColumnComparison_Seeks() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeBareColumn @x = N'Name1';", "IX_Users_DisplayNameCi"));
}
