using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class VectorTypeOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(VectorTypeOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() => await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    private static DataTypeReference ParseColumnDataType(string dataTypeSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"CREATE TABLE dbo.T (Col {dataTypeSql});");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var create = (CreateTableStatement)script.Batches[0].Statements[0];
        return create.Definition.ColumnDefinitions[0].DataType;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1998)]
    public async Task Resolve_VectorColumn_AcceptedByLiveEngineAndResolvesToVectorCategoryWithDimensionAsLength(int dimensions)
    {
        var ddl = $"CREATE TABLE dbo.T (Col VECTOR({dimensions}));";
        await new ScriptDeployer(Options).DeployAsync(ddl, _databaseName);

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ty.name FROM sys.columns c JOIN sys.types ty ON c.user_type_id = ty.user_type_id " +
            "WHERE c.object_id = OBJECT_ID('dbo.T') AND c.name = 'Col';";
        var liveTypeName = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("vector", liveTypeName);

        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType($"VECTOR({dimensions})"), columnCollation: null);

        Assert.NotNull(type);
        Assert.Equal(SqlTypeCategory.Vector, type!.Category);
        Assert.Equal(dimensions, type.Length);
    }

    [Fact]
    public async Task EqualityOperator_BetweenTwoVectorColumns_IsRejectedByLiveEngine_SoClassifierMustTreatItAsOutOfModel()
    {
        var ddl = "CREATE TABLE dbo.T (A VECTOR(3), B VECTOR(3));";
        await new ScriptDeployer(Options).DeployAsync(ddl, _databaseName);

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.T WHERE A = B;";

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(8117, exception.Number);

        var vectorType = new SqlType(SqlTypeCategory.Vector, Length: 3);
        var verdict = VerdictClassifier.Classify(vectorType, vectorType);

        Assert.Equal(Verdict.Unknown, verdict);
    }
}
