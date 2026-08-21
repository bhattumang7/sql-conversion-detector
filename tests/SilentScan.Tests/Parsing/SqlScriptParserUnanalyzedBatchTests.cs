using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Parsing;

public sealed class SqlScriptParserUnanalyzedBatchTests
{
    [Fact]
    public void ParseText_MiddleBatchHasSyntaxError_ReportsItAsUnanalyzedWithoutAffectingSurvivors()
    {
        var script = string.Join('\n',
            "CREATE VIEW dbo.vw_First AS SELECT 1 AS X;",
            "GO",
            "CREATE PROCEDURE dbo.usp_Broken AS SELECT 1 FROM FROM;",
            "GO",
            "CREATE VIEW dbo.vw_Third AS SELECT 1 AS X;");

        var result = SqlScriptParser.ParseText("test.sql", script);

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.BatchCount);

        var unanalyzed = Assert.Single(result.UnanalyzedBatches);
        Assert.Equal("test.sql", unanalyzed.SourcePath);
        Assert.Equal(UnanalyzedObjectKind.Procedure, unanalyzed.Kind);
        Assert.Equal("dbo.usp_Broken", unanalyzed.ObjectName);
        Assert.Equal(3, unanalyzed.StartLine);
    }

    [Fact]
    public void ParseText_AllBatchesParseCleanly_ReportsNoUnanalyzedBatches()
    {
        var script = "CREATE VIEW dbo.vw_First AS SELECT 1 AS X;\nGO\nCREATE VIEW dbo.vw_Second AS SELECT 1 AS X;";

        var result = SqlScriptParser.ParseText("test.sql", script);

        Assert.False(result.HasErrors);
        Assert.Empty(result.UnanalyzedBatches);
    }

    [Fact]
    public void ParseText_SingleBatchFileWithSyntaxError_ReportsItAsUnanalyzed()
    {
        var script = "CREATE PROCEDURE dbo.usp_Broken AS SELECT 1 FROM FROM;";

        var result = SqlScriptParser.ParseText("test.sql", script);

        Assert.True(result.HasErrors);
        Assert.Equal(0, result.BatchCount);

        var unanalyzed = Assert.Single(result.UnanalyzedBatches);
        Assert.Equal(UnanalyzedObjectKind.Procedure, unanalyzed.Kind);
        Assert.Equal("dbo.usp_Broken", unanalyzed.ObjectName);
        Assert.Equal(1, unanalyzed.StartLine);
    }
}
