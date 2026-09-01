using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class UnindexedTempTableUsageCommaCrossJoinOracleTests : OracleTestFixture
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    protected override string DatabaseNameSeed => nameof(UnindexedTempTableUsageCommaCrossJoinOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Source (Id INT NOT NULL, Code INT NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Source (Id, Code)
            SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            UPDATE STATISTICS dbo.Source WITH FULLSCAN;
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

        var planXml = string.Empty;
        await using (var probeCommand = new SqlCommand(probe, connection))
        await using (var reader = await probeCommand.ExecuteReaderAsync())
        {
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

    private static bool HasOperatorOnTempTable(string planXml, string tempTableNamePrefix, string physicalOpSubstring)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "RelOp")
            .Where(relOp => (string?)relOp.Attribute("PhysicalOp") is { } physicalOp
                && physicalOp.Contains(physicalOpSubstring, StringComparison.OrdinalIgnoreCase))
            .SelectMany(relOp => relOp.Descendants(ShowPlanNs + "Object"))
            .Any(obj => ((string?)obj.Attribute("Table"))?.Trim('[', ']')
                .StartsWith(tempTableNamePrefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IReadOnlyList<UnindexedTempTableUsageFinding> ScanSameShape(string query)
    {
        var result = SqlScriptParser.ParseText(
            "test.sql",
            $"CREATE PROCEDURE dbo.usp_Probe AS BEGIN {query} END");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return UnindexedTempTableUsageScanner.Scan(result, catalog);
    }

    [Fact]
    public async Task UnindexedTempTable_CommaJoinedInWhereClause_RealPlanScansNotSeeks_ScannerReportsFinding()
    {
        const string Probe = """
            SELECT Id, Code INTO #t FROM dbo.Source;
            SELECT s.Id FROM dbo.Source s, #t WHERE s.Id = #t.Id;
            """;

        var planXml = await CaptureRealExecutionPlanAsync(Probe);
        Assert.True(HasOperatorOnTempTable(planXml, "#t", "Scan"));
        Assert.False(HasOperatorOnTempTable(planXml, "#t", "Seek"));

        const string Query = """
            SELECT Id, Code INTO #t FROM dbo.Source;
            SELECT s.Id FROM dbo.Source s, #t WHERE s.Id = #t.Id;
            """;

        var findings = ScanSameShape(Query);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.JoinOperand, finding.Kind);
    }

    [Fact]
    public async Task UnindexedTempTable_ExplicitCrossJoinedFilteredInWhereClause_RealPlanScansNotSeeks_ScannerReportsFinding()
    {
        const string Probe = """
            SELECT Id, Code INTO #t2 FROM dbo.Source;
            SELECT s.Id FROM dbo.Source s CROSS JOIN #t2 WHERE s.Id = #t2.Id;
            """;

        var planXml = await CaptureRealExecutionPlanAsync(Probe);
        Assert.True(HasOperatorOnTempTable(planXml, "#t2", "Scan"));
        Assert.False(HasOperatorOnTempTable(planXml, "#t2", "Seek"));

        const string Query = """
            SELECT Id, Code INTO #t2 FROM dbo.Source;
            SELECT s.Id FROM dbo.Source s CROSS JOIN #t2 WHERE s.Id = #t2.Id;
            """;

        var findings = ScanSameShape(Query);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.JoinOperand, finding.Kind);
    }
}
