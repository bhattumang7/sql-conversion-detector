using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ExecResultSetsShapeLiveOracleTests
{
    [Fact]
    public async Task ColumnCountMismatch_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Id, CAST('x' AS VARCHAR(10)) AS Name;
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Callee WITH RESULT SETS ((Id INT NOT NULL));
            END;
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<ExecResultSetsShapeFinding>("ExecResultSetsShapeScanner"));
        Assert.Equal(ExecResultSetsShapeFindingKind.ColumnCountMismatch, finding.Kind);
        Assert.Equal("dbo.usp_Callee", finding.ExecutedProcQualifiedName);
        Assert.Equal(1, finding.DeclaredColumnCount);
        Assert.Equal(2, finding.DescribedColumnCount);
        Assert.Equal("dbo.usp_Caller", finding.CallerScopeQualifiedName);
    }

    [Fact]
    public async Task ColumnTypeMismatch_StringTruncation_FiresAtTheDeclaredPosition()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Id, CAST('hello world' AS VARCHAR(100)) AS Name;
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Callee WITH RESULT SETS ((Id INT NOT NULL, Name VARCHAR(3) NOT NULL));
            END;
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<ExecResultSetsShapeFinding>("ExecResultSetsShapeScanner"));
        Assert.Equal(ExecResultSetsShapeFindingKind.ColumnTypeMismatch, finding.Kind);
        Assert.Equal("dbo.usp_Callee", finding.ExecutedProcQualifiedName);
        Assert.Equal(2, finding.ColumnPosition);
        Assert.Equal("Name", finding.ColumnName);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.WriteLoss);
    }

    [Fact]
    public async Task MatchingShape_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Id, CAST('hello' AS VARCHAR(50)) AS Name;
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Callee WITH RESULT SETS ((Id INT NOT NULL, Name VARCHAR(50) NOT NULL));
            END;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<ExecResultSetsShapeFinding>("ExecResultSetsShapeScanner"));
    }

    [Fact]
    public async Task ExecWithoutResultSetsClause_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Id, CAST('x' AS VARCHAR(10)) AS Name;
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Callee;
            END;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<ExecResultSetsShapeFinding>("ExecResultSetsShapeScanner"));
    }
}
