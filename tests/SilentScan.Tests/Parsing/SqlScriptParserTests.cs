using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Parsing;

public sealed class SqlScriptParserTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "phase0_spike.sql");

    [Fact]
    public void ParseFile_Phase0Spike_ProducesNoErrors()
    {
        var result = SqlScriptParser.ParseFile(FixturePath);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ParseFile_Phase0Spike_ProducesFourBatches()
    {
        var result = SqlScriptParser.ParseFile(FixturePath);

        var script = Assert.IsType<TSqlScript>(result.Fragment);
        Assert.Equal(4, script.Batches.Count);
    }

    [Fact]
    public void ParseFile_Phase0Spike_SecondViewSelectsFromFirstView()
    {
        var result = SqlScriptParser.ParseFile(FixturePath);
        var script = (TSqlScript)result.Fragment;

        var view2 = script.Batches
            .SelectMany(b => b.Statements)
            .OfType<CreateViewStatement>()
            .Single(v => v.SchemaObjectName.BaseIdentifier.Value == "vw_OrdersLevel2");

        var querySpec = Assert.IsType<QuerySpecification>(view2.SelectStatement.QueryExpression);
        var fromTable = Assert.IsType<NamedTableReference>(querySpec.FromClause.TableReferences.Single());
        Assert.Equal("vw_OrdersLevel1", fromTable.SchemaObject.BaseIdentifier.Value);
    }

    [Fact]
    public void ParseFile_Phase0Spike_ExtractsWherePredicateColumnReference()
    {
        var result = SqlScriptParser.ParseFile(FixturePath);
        var script = (TSqlScript)result.Fragment;

        var proc = script.Batches
            .SelectMany(b => b.Statements)
            .OfType<CreateProcedureStatement>()
            .Single();

        var beginEnd = proc.StatementList.Statements.OfType<BeginEndBlockStatement>().Single();
        var select = beginEnd.StatementList.Statements.OfType<SelectStatement>().Single();
        var querySpec = Assert.IsType<QuerySpecification>(select.QueryExpression);
        var where = Assert.IsType<BooleanComparisonExpression>(querySpec.WhereClause.SearchCondition);

        var columnRef = Assert.IsType<ColumnReferenceExpression>(where.FirstExpression);
        Assert.Equal("OrderCode", columnRef.MultiPartIdentifier.Identifiers.Last().Value);

        var parameterRef = Assert.IsType<VariableReference>(where.SecondExpression);
        Assert.Equal("@OrderCode", parameterRef.Name);
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var tempDir = Directory.CreateTempSubdirectory("silentscan-tests-");
        var path = Path.Combine(tempDir.FullName, "test.sql");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ParseFile_OneBadBatchAmongGoodOnes_RetainsTheGoodBatches()
    {

        var path = WriteTempFile(Encoding.UTF8.GetBytes(
            """
            CREATE TABLE dbo.A (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.B ((( BAD SYNTAX HERE;
            GO
            CREATE TABLE dbo.C (Id INT NOT NULL);
            GO
            """));

        try
        {
            var result = SqlScriptParser.ParseFile(path);

            Assert.True(result.HasErrors);
            Assert.Equal(2, result.BatchCount);
            var script = (TSqlScript)result.Fragment;
            var tableNames = script.Batches
                .SelectMany(b => b.Statements)
                .OfType<CreateTableStatement>()
                .Select(t => t.SchemaObjectName.BaseIdentifier.Value)
                .ToList();
            Assert.Equal(["A", "C"], tableNames);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_QuotedIdentifierRequiredForSchemaQualifiedName_RecoversViaRetry()
    {

        var path = WriteTempFile(Encoding.UTF8.GetBytes("SELECT Id FROM dbo.\"Orders\";\nGO\n"));

        try
        {
            var result = SqlScriptParser.ParseFile(path);

            Assert.False(result.HasErrors);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_Windows1252EncodedIdentifier_DecodesCorrectlyInsteadOfUtf8Mojibake()
    {

        var sql = "CREATE TABLE dbo.Café (Id INT NOT NULL);\nGO\n";
        var path = WriteTempFile(Encoding.Latin1.GetBytes(sql));

        try
        {
            var result = SqlScriptParser.ParseFile(path);

            Assert.False(result.HasErrors);
            var script = (TSqlScript)result.Fragment;
            var table = script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().Single();
            Assert.Equal("Café", table.SchemaObjectName.BaseIdentifier.Value);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_Utf8BomPresent_DecodesAsUtf8RegardlessOfContent()
    {
        var sql = "CREATE TABLE dbo.Café (Id INT NOT NULL);\nGO\n";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sql)).ToArray();
        var path = WriteTempFile(bytes);

        try
        {
            var result = SqlScriptParser.ParseFile(path);

            Assert.False(result.HasErrors);
            var script = (TSqlScript)result.Fragment;
            var table = script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().Single();
            Assert.Equal("Café", table.SchemaObjectName.BaseIdentifier.Value);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ParseText_QuotedIdentifierOff_ParsesLegacyExecStringLiteralCleanly()
    {

        var result = SqlScriptParser.ParseText("test.sql", "EXEC(\"SELECT 1\");", initialQuotedIdentifiers: false);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ParseText_QuotedIdentifierOn_RejectsLegacyExecStringLiteralAsUnclosedIdentifier()
    {

        var result = SqlScriptParser.ParseText("test.sql", "EXEC(\"SELECT 1\");", initialQuotedIdentifiers: true);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void DecodeFile_Windows1252EncodedIdentifier_DecodesCorrectlyInsteadOfUtf8Mojibake()
    {

        var sql = "CREATE TABLE dbo.Café (Id INT NOT NULL);\nGO\n";
        var path = WriteTempFile(Encoding.Latin1.GetBytes(sql));

        try
        {
            var text = SqlScriptParser.DecodeFile(path);

            Assert.Equal(sql, text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private const string DropTableIfExists = "DROP TABLE IF EXISTS dbo.T;";

    [Fact]
    public void ParseText_DropTableIfExists_UnderCompat120_FailsLikeTheRealCompat120TargetWould()
    {
        var result = SqlScriptParser.ParseText("test.sql", DropTableIfExists, initialQuotedIdentifiers: true, compatibilityLevel: 120);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ParseText_DropTableIfExists_UnderCompat130_Succeeds()
    {
        var result = SqlScriptParser.ParseText("test.sql", DropTableIfExists, initialQuotedIdentifiers: true, compatibilityLevel: 130);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ParseText_DropTableIfExists_UnknownCompatLevel_UsesNewestDialectAndSucceeds()
    {

        var result = SqlScriptParser.ParseText("test.sql", DropTableIfExists, initialQuotedIdentifiers: true, compatibilityLevel: null);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ParseText_DropTableIfExists_CompatLevelBelow100_FloorsToOldestParserRatherThanGuessingNewer()
    {

        var result = SqlScriptParser.ParseText("test.sql", DropTableIfExists, initialQuotedIdentifiers: true, compatibilityLevel: 80);

        Assert.True(result.HasErrors);
    }
}
