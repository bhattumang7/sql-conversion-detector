using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Parsing;

/// <summary>
/// Phase 0 spike: prove ScriptDOM parses a table + two stacked views + a proc, and that we
/// can walk the AST to the WHERE predicate's column reference. See plan.md Phase 0.
/// </summary>
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
        // docs/audit-remediation-plan.md Phase 4.4, audit finding B4: ScriptDOM itself drops
        // only the malformed batch, not the whole file - verified directly against the parser
        // before this was relied on (a throwaway probe program parsing this exact shape).
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
        // Verified directly against the parser: a double-quoted schema-qualified identifier
        // only parses under QUOTED_IDENTIFIER ON, which is the tool's own default - so this
        // exercises the retry path for a script written assuming the opposite default.
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
        // A table name containing an accented character (Windows-1252/Latin-1 corpora are
        // common in older T-SQL scripts), saved without a BOM. Verified directly against the
        // parser: decoding this as UTF-8 does NOT produce a parse error (ScriptDOM's lexer
        // accepts the resulting U+FFFD replacement character inside an identifier without
        // complaint) - it silently produces the WRONG table name instead, which the encoding
        // fallback exists to prevent.
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
        // Under QUOTED_IDENTIFIER OFF (the setting a module was actually CREATEd/ALTERed with,
        // per sys.sql_modules.uses_quoted_identifier), "..." is a plain string literal - the
        // legacy EXEC("...") dynamic-SQL idiom. The live path must be able to parse a module
        // with this ground-truth setting instead of always assuming QI ON.
        var result = SqlScriptParser.ParseText("test.sql", "EXEC(\"SELECT 1\");", initialQuotedIdentifiers: false);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ParseText_QuotedIdentifierOn_RejectsLegacyExecStringLiteralAsUnclosedIdentifier()
    {
        // The same text under QI ON (the tool's existing 2-arg-overload default) is NOT legal -
        // "..." is an identifier delimiter, so this must remain a genuine parse error rather
        // than silently accepted both ways.
        var result = SqlScriptParser.ParseText("test.sql", "EXEC(\"SELECT 1\");", initialQuotedIdentifiers: true);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void DecodeFile_Windows1252EncodedIdentifier_DecodesCorrectlyInsteadOfUtf8Mojibake()
    {
        // DecodeFile is ParseFile's own decode step, exposed for callers (corpus template
        // substitution) that must transform the text before parsing it - it must apply the
        // identical BOM-detection/Latin-1 fallback, not a plain File.ReadAllText.
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
}
