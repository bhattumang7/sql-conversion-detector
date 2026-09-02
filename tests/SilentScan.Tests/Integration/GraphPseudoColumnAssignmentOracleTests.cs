using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class GraphPseudoColumnAssignmentOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(GraphPseudoColumnAssignmentOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Person (Name NVARCHAR(50) NOT NULL) AS NODE;
        CREATE TABLE dbo.Follows AS EDGE;
        """;

    private static IReadOnlyList<GraphPseudoColumnAssignmentFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return GraphPseudoColumnAssignmentScanner.Scan(result);
    }

    [Fact]
    public async Task RealServer_InsertExplicitNodeId_AlwaysFails()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync("INSERT INTO dbo.Person ($node_id, Name) VALUES (DEFAULT, 'Alice');"));

        Assert.NotEqual(0, ex.Number);

        var findings = Scan("INSERT INTO dbo.Person ($node_id, Name) VALUES (DEFAULT, 'Alice');");
        var finding = Assert.Single(findings);
        Assert.Equal("$node_id", finding.PseudoColumnName);
        Assert.Equal("INSERT", finding.StatementKind);
    }

    [Fact]
    public async Task RealServer_UpdateEdgeId_AlwaysFails()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteNonQueryAsync("UPDATE dbo.Follows SET $edge_id = $edge_id;"));

        Assert.NotEqual(0, ex.Number);

        var findings = Scan("UPDATE dbo.Follows SET $edge_id = $edge_id;");
        var finding = Assert.Single(findings);
        Assert.Equal("$edge_id", finding.PseudoColumnName);
        Assert.Equal("UPDATE", finding.StatementKind);
    }

    [Fact]
    public async Task RealServer_MergeInsertActionExplicitNodeId_AlwaysFails()
    {
        const string sql = """
            MERGE dbo.Person AS tgt
            USING (SELECT 'Bob' AS Name) AS src ON tgt.Name = src.Name
            WHEN NOT MATCHED THEN INSERT ($node_id, Name) VALUES (NULL, src.Name);
            """;

        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(sql));

        Assert.NotEqual(0, ex.Number);

        var findings = Scan(sql);
        var finding = Assert.Single(findings);
        Assert.Equal("$node_id", finding.PseudoColumnName);
        Assert.Equal("MERGE INSERT", finding.StatementKind);
    }

    [Fact]
    public async Task RealServer_MergeUpdateActionEdgeId_AlwaysFails()
    {
        const string sql = """
            MERGE dbo.Follows AS tgt
            USING (SELECT 1 AS Id) AS src ON 1 = 0
            WHEN MATCHED THEN UPDATE SET $edge_id = $edge_id;
            """;

        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync(sql));

        Assert.NotEqual(0, ex.Number);

        var findings = Scan(sql);
        var finding = Assert.Single(findings);
        Assert.Equal("$edge_id", finding.PseudoColumnName);
        Assert.Equal("UPDATE", finding.StatementKind);
    }

    [Fact]
    public void Scanner_OrdinaryColumnAssignment_NeverFires()
    {
        var findings = Scan(
            """
            INSERT INTO dbo.Person (Name) VALUES ('Alice');
            UPDATE dbo.Person SET Name = 'Bob';
            """);

        Assert.Empty(findings);
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
