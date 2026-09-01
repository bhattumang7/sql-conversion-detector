using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DropProtectedObjectOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DropProtectedObjectOracleTests);

    protected override string Ddl => """
        CREATE SCHEMA Reporting;
        GO
        CREATE TABLE Reporting.MonthlyTotal (TotalId INT NOT NULL);
        """;

    [Fact]
    public async Task DropSchema_SchemaStillOwnsTable_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("DROP SCHEMA Reporting;", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(3729, ex.Number);

        var findings = ScanDropProtectedObject("""
            CREATE SCHEMA Reporting;
            GO
            CREATE TABLE Reporting.MonthlyTotal (TotalId INT NOT NULL);
            DROP SCHEMA Reporting;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(DropProtectedObjectKind.SchemaNotEmpty, finding.Kind);
    }

    [Fact]
    public async Task DropSchema_SchemaAlreadyEmpty_EngineAllowsIt_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var dropTable = new SqlCommand("DROP TABLE Reporting.MonthlyTotal;", connection))
        {
            await dropTable.ExecuteNonQueryAsync();
        }

        await using var dropSchema = new SqlCommand("DROP SCHEMA Reporting;", connection);
        await dropSchema.ExecuteNonQueryAsync();

        var findings = ScanDropProtectedObject("""
            CREATE SCHEMA Reporting;
            GO
            DROP SCHEMA Reporting;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DropRole_FixedDatabaseRole_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("DROP ROLE db_owner;", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(15150, ex.Number);

        var findings = ScanDropProtectedObject("DROP ROLE db_owner;");

        var finding = Assert.Single(findings);
        Assert.Equal(DropProtectedObjectKind.FixedDatabaseRole, finding.Kind);
    }

    [Fact]
    public async Task DropRole_CustomRole_EngineAllowsIt_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var createRole = new SqlCommand("CREATE ROLE OracleProbeRole;", connection))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        await using var dropRole = new SqlCommand("DROP ROLE OracleProbeRole;", connection);
        await dropRole.ExecuteNonQueryAsync();

        var findings = ScanDropProtectedObject("DROP ROLE OracleProbeRole;");

        Assert.Empty(findings);
    }

    private static IReadOnlyList<DropProtectedObjectFinding> ScanDropProtectedObject(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        return DropProtectedObjectScanner.Scan(catalog);
    }
}
