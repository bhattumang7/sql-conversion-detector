using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Deployment;

/// <summary>
/// The code-level backstop for CLAUDE.md's "corpus DML is never executed, anywhere" hard scope -
/// before this existed, that guarantee rested entirely on manifest curation. These tests don't
/// need the Docker oracle: they only exercise classification against real parsed batches.
/// </summary>
public sealed class DdlStatementWhitelistTests
{
    private static TSqlBatch ParseSingleBatch(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        return Assert.Single(script.Batches);
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.T (Id INT NOT NULL);")]
    [InlineData("CREATE INDEX IX_T_Id ON dbo.T(Id);")]
    [InlineData("CREATE VIEW dbo.V AS SELECT 1 AS Col;")]
    [InlineData("ALTER VIEW dbo.V AS SELECT 2 AS Col;")]
    [InlineData("CREATE OR ALTER VIEW dbo.V AS SELECT 3 AS Col;")]
    [InlineData("CREATE FUNCTION dbo.fn_T() RETURNS TABLE AS RETURN SELECT 1 AS Col;")]
    [InlineData("CREATE TYPE dbo.T_Type AS TABLE (Id INT NOT NULL);")]
    [InlineData("CREATE SCHEMA audit;")]
    [InlineData("CREATE SYNONYM dbo.S FOR dbo.T;")]
    [InlineData("DROP TABLE dbo.T;")]
    [InlineData("DROP VIEW dbo.V;")]
    public void DisallowedStatementTypeNames_KnownSchemaOnlyStatements_AllAllowed(string sql)
    {
        var batch = ParseSingleBatch(sql);

        Assert.Empty(DdlStatementWhitelist.DisallowedStatementTypeNames(batch));
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T (Id) VALUES (1);", "InsertStatement")]
    [InlineData("UPDATE dbo.T SET Id = 1;", "UpdateStatement")]
    [InlineData("DELETE FROM dbo.T;", "DeleteStatement")]
    [InlineData("EXEC dbo.usp_DoSomething;", "ExecuteStatement")]
    [InlineData("GRANT SELECT ON dbo.T TO SomeUser;", "GrantStatement")]
    [InlineData("CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT 1; END;", "CreateProcedureStatement")]
    public void DisallowedStatementTypeNames_DmlAndProceduralLogic_AreRejected(string sql, string expectedTypeName)
    {
        var batch = ParseSingleBatch(sql);

        var disallowed = DdlStatementWhitelist.DisallowedStatementTypeNames(batch);

        Assert.Contains(expectedTypeName, disallowed);
    }

    [Fact]
    public void DisallowedStatementTypeNames_MixedBatch_ReportsOnlyTheDisallowedOnes()
    {
        var result = SqlScriptParser.ParseText(
            "test.sql",
            "CREATE TABLE dbo.T (Id INT NOT NULL); INSERT INTO dbo.T (Id) VALUES (1);");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        var batch = Assert.Single(script.Batches);

        var disallowed = DdlStatementWhitelist.DisallowedStatementTypeNames(batch);

        Assert.Equal(["InsertStatement"], disallowed);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS ON;")]
    [InlineData("SET QUOTED_IDENTIFIER ON;")]
    [InlineData("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; CREATE TABLE dbo.T (Id INT NOT NULL);")]
    public void DisallowedStatementTypeNames_PredicateSetStatements_Allowed(string sql)
    {
        var batch = ParseSingleBatch(sql);

        Assert.Empty(DdlStatementWhitelist.DisallowedStatementTypeNames(batch));
    }

    [Theory]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'T') CREATE TABLE dbo.T (Id INT NOT NULL);")]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'T') BEGIN CREATE TABLE dbo.T (Id INT NOT NULL); CREATE INDEX IX_T ON dbo.T(Id); END")]
    [InlineData("IF OBJECT_ID('dbo.T') IS NULL CREATE TABLE dbo.T (Id INT NOT NULL); ELSE ALTER TABLE dbo.T ADD Code VARCHAR(10) NULL;")]
    public void DisallowedStatementTypeNames_DdlGuardedByIfNotExists_Allowed(string sql)
    {
        var batch = ParseSingleBatch(sql);

        Assert.Empty(DdlStatementWhitelist.DisallowedStatementTypeNames(batch));
    }

    [Fact]
    public void DisallowedStatementTypeNames_IfBranchWithDisallowedStatement_ReportsIt()
    {
        var batch = ParseSingleBatch("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'T') BEGIN CREATE TABLE dbo.T (Id INT NOT NULL); INSERT INTO dbo.T (Id) VALUES (1); END");

        var disallowed = DdlStatementWhitelist.DisallowedStatementTypeNames(batch);

        Assert.Equal(["InsertStatement"], disallowed);
    }

    [Fact]
    public void DisallowedStatementTypeNames_CreateSequence_Allowed()
    {
        var batch = ParseSingleBatch("CREATE SEQUENCE dbo.Seq_T START WITH 1 INCREMENT BY 1;");

        Assert.Empty(DdlStatementWhitelist.DisallowedStatementTypeNames(batch));
    }
}
