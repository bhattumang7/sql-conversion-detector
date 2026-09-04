using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
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

    private static IReadOnlyList<VectorFunctionArgumentFinding> ScanVectorFunctionArguments(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return VectorFunctionArgumentScanner.Scan(result, new DatabaseCatalog());
    }

    [Fact]
    public async Task VectorDistance_WithVarcharOperand_FailsToCompileWithMsg8116_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @a VARCHAR(50) = '[1,2,3]';
            DECLARE @b VECTOR(3) = CAST('[4,5,6]' AS VECTOR(3));
            SELECT VECTOR_DISTANCE('cosine', @a, @b);
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(8116, exception.Number);

        var finding = Assert.Single(ScanVectorFunctionArguments(Sql));
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
        Assert.Equal("VECTOR_DISTANCE", finding.FunctionName);
    }

    [Fact]
    public async Task VectorDistance_WithTwoVectorOperandsOfMatchingDimension_SucceedsAndScannerDoesNotFlagIt()
    {
        const string Sql = """
            DECLARE @a VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3));
            DECLARE @b VECTOR(3) = CAST('[4,5,6]' AS VECTOR(3));
            SELECT VECTOR_DISTANCE('cosine', @a, @b);
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteScalarAsync();

        Assert.Empty(ScanVectorFunctionArguments(Sql));
    }

    [Fact]
    public async Task VectorDistance_WithMismatchedVectorDimensions_FailsAtExecutionWithMsg42204_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @a VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3));
            DECLARE @b VECTOR(4) = CAST('[1,2,3,4]' AS VECTOR(4));
            SELECT VECTOR_DISTANCE('cosine', @a, @b);
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(42204, exception.Number);

        var finding = Assert.Single(ScanVectorFunctionArguments(Sql));
        Assert.Equal(VectorFunctionArgumentFindingKind.DimensionMismatch, finding.Kind);
    }

    [Fact]
    public async Task VectorProperty_WithNvarcharOperand_FailsToCompileWithMsg8116_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @a NVARCHAR(50) = N'[1,2,3]';
            SELECT VECTORPROPERTY(@a, 'Dimensions');
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(8116, exception.Number);

        var finding = Assert.Single(ScanVectorFunctionArguments(Sql));
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
        Assert.Equal("VECTORPROPERTY", finding.FunctionName);
    }

    private static IReadOnlyList<VectorLiteralConversionFinding> ScanVectorLiteralConversions(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return VectorLiteralConversionScanner.Scan(result, new DatabaseCatalog());
    }

    [Fact]
    public async Task CastToVector_WithBooleanJsonElement_FailsAtExecutionWithMsg13670_AndScannerFlagsIt()
    {
        const string Sql = "SELECT CAST('[1.0, true, 3.0]' AS VECTOR(3));";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(13670, exception.Number);

        var finding = Assert.Single(ScanVectorLiteralConversions(Sql));
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
        Assert.Equal("boolean", finding.ElementKind);
    }

    [Fact]
    public async Task DeclareVectorVariable_WithBooleanJsonInitializer_FailsAtExecutionWithMsg13670_AndScannerFlagsIt()
    {
        const string Sql = "DECLARE @v VECTOR(3) = '[1.0, true, 3.0]'; SELECT @v;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(13670, exception.Number);

        var finding = Assert.Single(ScanVectorLiteralConversions(Sql));
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
    }

    [Fact]
    public async Task SetVectorVariable_WithBooleanJsonAssignment_FailsAtExecutionWithMsg13670_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @v VECTOR(3);
            SET @v = '[1.0, true, 3.0]';
            SELECT @v;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(13670, exception.Number);

        var finding = Assert.Single(ScanVectorLiteralConversions(Sql));
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
    }

    [Fact]
    public async Task CastToVector_WithElementCountMismatch_FailsAtExecutionWithMsg42204_AndScannerFlagsIt()
    {
        const string Sql = "SELECT CAST('[1.0, 2.0]' AS VECTOR(3));";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(42204, exception.Number);

        var finding = Assert.Single(ScanVectorLiteralConversions(Sql));
        Assert.Equal(VectorLiteralConversionFindingKind.ElementCountMismatch, finding.Kind);
        Assert.Equal(2, finding.ActualElementCount);
        Assert.Equal(3, finding.DeclaredDimensions);
    }

    [Fact]
    public async Task CastToVector_WithAllNumericElementsMatchingDimension_SucceedsAndScannerDoesNotFlagIt()
    {
        const string Sql = "SELECT CAST('[1.0, 2.0, 3.0]' AS VECTOR(3));";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteScalarAsync();

        Assert.Empty(ScanVectorLiteralConversions(Sql));
    }
}
