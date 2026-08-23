using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TableVariableModificationOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TableVariableModificationOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.BigTable (Id INT NOT NULL, Grp INT NOT NULL, Val VARCHAR(100) NOT NULL);
        GO
        CREATE TABLE dbo.Audit (Id INT NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.BigTable (Id, Grp, Val)
            SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 100, REPLICATE('x', 50)
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.BigTable WITH FULLSCAN;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<string> CaptureRealExecutionPlanAsync(string probe)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var onCommand = new SqlCommand("SET STATISTICS XML ON;", connection))
        {
            await onCommand.ExecuteNonQueryAsync();
        }

        string planXml;
        await using (var probeCommand = new SqlCommand(probe, connection))
        await using (var reader = await probeCommand.ExecuteReaderAsync())
        {
            planXml = string.Empty;
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                    {
                        var value = reader.GetString(0);
                        if (value.Contains("ShowPlanXML", StringComparison.Ordinal))
                        {
                            planXml = value;
                        }
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await using (var offCommand = new SqlCommand("SET STATISTICS XML OFF;", connection))
        {
            await offCommand.ExecuteNonQueryAsync();
        }

        Assert.NotEmpty(planXml);
        return planXml;
    }

    [Fact]
    public async Task InsertIntoTableVariable_ForcesSerial()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @t TABLE (Grp INT, Cnt INT); INSERT INTO @t (Grp, Cnt) SELECT Grp, COUNT(*) FROM dbo.BigTable GROUP BY Grp OPTION (MAXDOP 0);");

        Assert.Contains("NonParallelPlanReason=\"TableVariableTransactionsDoNotSupportParallelNestedTransaction\"", planXml);
    }

    [Fact]
    public async Task OutputIntoTableVariable_ForcesSerial()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @out TABLE (Id INT); DELETE FROM dbo.BigTable OUTPUT deleted.Id INTO @out WHERE Grp = -1 OPTION (MAXDOP 0);");

        Assert.Contains("NonParallelPlanReason=\"TableVariableTransactionsDoNotSupportParallelNestedTransaction\"", planXml);
    }

    [Fact]
    public async Task ReadOnlyReferenceToTableVariable_NeverBlocksParallelism()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            """
            DECLARE @t TABLE (Grp INT);
            INSERT INTO @t (Grp) VALUES (1);
            SELECT b.Id FROM dbo.BigTable b JOIN @t t ON b.Grp = t.Grp OPTION (MAXDOP 0);
            """);

        Assert.DoesNotContain("NonParallelPlanReason=\"TableVariableTransactionsDoNotSupportParallelNestedTransaction\"", planXml);
    }

    [Fact]
    public async Task OutputIntoRealTable_NeverBlocksParallelism()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DELETE FROM dbo.BigTable OUTPUT deleted.Id INTO dbo.Audit WHERE Grp = -1 OPTION (MAXDOP 0);");

        Assert.DoesNotContain("NonParallelPlanReason=\"TableVariableTransactionsDoNotSupportParallelNestedTransaction\"", planXml);
    }
}
