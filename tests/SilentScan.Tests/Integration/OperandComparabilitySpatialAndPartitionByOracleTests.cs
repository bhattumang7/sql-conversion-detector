using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class OperandComparabilitySpatialAndPartitionByOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(OperandComparabilitySpatialAndPartitionByOracleTests);

    protected override string Ddl =>
        "CREATE TABLE dbo.Parcel (Id INT NOT NULL PRIMARY KEY, Boundary GEOMETRY NOT NULL, Prior GEOMETRY NOT NULL);"
        + "CREATE TABLE dbo.Document (Id INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);";

    private static IReadOnlyList<OperandComparabilityFinding> Scan(string ddl, string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return OperandComparabilityScanner.Scan(result, catalog);
    }

    [Fact]
    public async Task GeometryEquality_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT Id FROM dbo.Parcel WHERE Boundary = Prior;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(403, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(OperandComparabilityFindingKind.Spatial, finding.Kind);
        Assert.Equal(OperandComparabilityContext.Comparison, finding.Context);
    }

    [Fact]
    public async Task GeometryOrderBy_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT Id FROM dbo.Parcel ORDER BY Boundary;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(249, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(OperandComparabilityFindingKind.Spatial, finding.Kind);
        Assert.Equal(OperandComparabilityContext.OrderBy, finding.Context);
    }

    [Fact]
    public async Task GeometryIsNull_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt()
    {
        const string sql = "SELECT Id FROM dbo.Parcel WHERE Boundary IS NULL;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Empty(Scan(Ddl, sql));
    }

    [Fact]
    public async Task PartitionByXmlColumn_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT Id, ROW_NUMBER() OVER (PARTITION BY Payload ORDER BY Id) FROM dbo.Document;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(305, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(OperandComparabilityFindingKind.Xml, finding.Kind);
        Assert.Equal(OperandComparabilityContext.PartitionBy, finding.Context);
    }

    [Fact]
    public async Task PartitionByGeometryColumn_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT Id, ROW_NUMBER() OVER (PARTITION BY Boundary ORDER BY Id) FROM dbo.Parcel;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(249, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(OperandComparabilityFindingKind.Spatial, finding.Kind);
        Assert.Equal(OperandComparabilityContext.PartitionBy, finding.Context);
    }

    [Fact]
    public async Task PartitionByOrdinaryColumn_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt()
    {
        const string sql = "SELECT Id, ROW_NUMBER() OVER (PARTITION BY Id ORDER BY Id) FROM dbo.Document;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Empty(Scan(Ddl, sql));
    }
}
