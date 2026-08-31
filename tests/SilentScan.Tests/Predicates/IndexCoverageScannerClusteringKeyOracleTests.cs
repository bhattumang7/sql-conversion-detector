using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class IndexCoverageScannerClusteringKeyOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(IndexCoverageScannerClusteringKeyOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.HeapT (Id INT NOT NULL, A INT NOT NULL, CONSTRAINT PK_HeapT PRIMARY KEY NONCLUSTERED (Id));
        CREATE NONCLUSTERED INDEX IX_HeapT_A ON dbo.HeapT(A);
        GO
        CREATE TABLE dbo.ClusteredT (Id INT NOT NULL PRIMARY KEY CLUSTERED, A INT NOT NULL);
        CREATE NONCLUSTERED INDEX IX_ClusteredT_A ON dbo.ClusteredT(A);
        GO
        CREATE TABLE dbo.UniqueClusteredT (Id INT NOT NULL, Code VARCHAR(10) NOT NULL, A INT NOT NULL,
            CONSTRAINT UQ_UniqueClusteredT_Code UNIQUE CLUSTERED (Code),
            CONSTRAINT PK_UniqueClusteredT PRIMARY KEY NONCLUSTERED (Id));
        CREATE NONCLUSTERED INDEX IX_UniqueClusteredT_A ON dbo.UniqueClusteredT(A);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.HeapT (Id, A)
            SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            INSERT INTO dbo.ClusteredT (Id, A)
            SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            INSERT INTO dbo.UniqueClusteredT (Id, Code, A)
            SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'C' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            UPDATE STATISTICS dbo.HeapT WITH FULLSCAN;
            UPDATE STATISTICS dbo.ClusteredT WITH FULLSCAN;
            UPDATE STATISTICS dbo.UniqueClusteredT WITH FULLSCAN;
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

    private static IReadOnlyList<IndexCoverageFinding> ScanSameShape(string ddl, string query)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{query}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return IndexCoverageScanner.Scan(result, catalog);
    }

    [Fact]
    public async Task HeapWithNonclusteredPrimaryKey_RealPlanNeedsRidLookup_ScannerReportsFinding()
    {
        const string Query =
            "SELECT Id, A FROM dbo.HeapT WITH (INDEX(IX_HeapT_A)) WHERE A = 5;";

        var planXml = await CaptureRealExecutionPlanAsync(Query);
        Assert.Contains("PhysicalOp=\"RID Lookup\"", planXml);

        const string StaticDdl =
            "CREATE TABLE dbo.HeapT (Id INT NOT NULL, A INT NOT NULL, CONSTRAINT PK_HeapT PRIMARY KEY NONCLUSTERED (Id));"
            + "CREATE NONCLUSTERED INDEX IX_HeapT_A ON dbo.HeapT(A);";

        var findings = ScanSameShape(StaticDdl, "SELECT Id, A FROM dbo.HeapT WHERE A = 5;");

        var finding = Assert.Single(findings, f => f.Kind == IndexCoverageFindingKind.KeyLookupProneIndex);
        Assert.Equal("dbo.HeapT", finding.TableQualifiedName);
        Assert.Equal("IX_HeapT_A", finding.IndexName);
        Assert.Contains("Id", finding.UncoveredColumns);
    }

    [Fact]
    public async Task ClusteredPrimaryKey_RealPlanNeedsNoLookup_ScannerSuppresses()
    {
        const string Query =
            "SELECT Id, A FROM dbo.ClusteredT WITH (INDEX(IX_ClusteredT_A)) WHERE A = 5;";

        var planXml = await CaptureRealExecutionPlanAsync(Query);
        Assert.DoesNotContain("Lookup=\"1\"", planXml);

        const string StaticDdl =
            "CREATE TABLE dbo.ClusteredT (Id INT NOT NULL PRIMARY KEY CLUSTERED, A INT NOT NULL);"
            + "CREATE NONCLUSTERED INDEX IX_ClusteredT_A ON dbo.ClusteredT(A);";

        var findings = ScanSameShape(StaticDdl, "SELECT Id, A FROM dbo.ClusteredT WHERE A = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ClusteredUniqueConstraintWithSeparateNonclusteredPrimaryKey_RealPlanNeedsKeyLookupOnlyForId_ScannerMatches()
    {
        const string Query =
            "SELECT Id, Code, A FROM dbo.UniqueClusteredT WITH (INDEX(IX_UniqueClusteredT_A)) WHERE A = 5;";

        var planXml = await CaptureRealExecutionPlanAsync(Query);
        Assert.Contains("PhysicalOp=\"Clustered Index Seek\"", planXml);
        Assert.Contains("IndexKind=\"Clustered\"", planXml);
        Assert.Contains("Lookup=\"1\"", planXml);

        const string StaticDdl =
            "CREATE TABLE dbo.UniqueClusteredT (Id INT NOT NULL, Code VARCHAR(10) NOT NULL, A INT NOT NULL,"
            + " CONSTRAINT UQ_UniqueClusteredT_Code UNIQUE CLUSTERED (Code),"
            + " CONSTRAINT PK_UniqueClusteredT PRIMARY KEY NONCLUSTERED (Id));"
            + "CREATE NONCLUSTERED INDEX IX_UniqueClusteredT_A ON dbo.UniqueClusteredT(A);";

        var findings = ScanSameShape(StaticDdl, "SELECT Id, Code, A FROM dbo.UniqueClusteredT WHERE A = 5;");

        var finding = Assert.Single(findings, f => f.Kind == IndexCoverageFindingKind.KeyLookupProneIndex);
        Assert.Equal("dbo.UniqueClusteredT", finding.TableQualifiedName);
        Assert.Contains("Id", finding.UncoveredColumns);
        Assert.DoesNotContain("Code", finding.UncoveredColumns);
    }
}
