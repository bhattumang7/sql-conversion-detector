using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class CrossProcedureTempTableScopeTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SubProcedureQueriesCallersTempTable_SingleKnownCaller_Resolves()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_Sub AS
            BEGIN
                SELECT Col FROM #Results WHERE Col = N'x';
            END
            GO
            CREATE PROCEDURE dbo.usp_Driver AS
            BEGIN
                CREATE TABLE #Results (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                EXEC dbo.usp_Sub;
            END
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Col");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Results", finding.Column.TableQualifiedName);
    }

    [Fact]
    public async Task SubProcedureQueriesTempTable_TwoCallersAgreeingOnShape_Resolves()
    {

        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_Sub AS
            BEGIN
                SELECT Col FROM #Results WHERE Col = N'x';
            END
            GO
            CREATE PROCEDURE dbo.usp_DriverA AS
            BEGIN
                CREATE TABLE #Results (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                EXEC dbo.usp_Sub;
            END
            GO
            CREATE PROCEDURE dbo.usp_DriverB AS
            BEGIN
                CREATE TABLE #Results (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                EXEC dbo.usp_Sub;
            END
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Col");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Results", finding.Column.TableQualifiedName);

        Assert.Null(finding.Column.Indexed);
    }

    [Fact]
    public async Task SubProcedureQueriesTempTable_TwoCallersWithDifferentShapes_StaysUnresolved()
    {

        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_Sub AS
            BEGIN
                SELECT Col FROM #Results WHERE Col = N'x';
            END
            GO
            CREATE PROCEDURE dbo.usp_DriverA AS
            BEGIN
                CREATE TABLE #Results (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                EXEC dbo.usp_Sub;
            END
            GO
            CREATE PROCEDURE dbo.usp_DriverB AS
            BEGIN
                CREATE TABLE #Results (Col INT NOT NULL);
                EXEC dbo.usp_Sub;
            END
            """);

        Assert.DoesNotContain(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Col" && f.Column.TableQualifiedName == "#Results");
        Assert.Contains(report.SkippedConstructs, s => s.ConstructKind == "FROM table reference" && s.Reason.Contains("#Results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubProcedureQueriesCallersTempTable_InsideDynamicSql_SingleKnownCaller_Resolves()
    {

        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_Sub AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM #Results WHERE Col = N''x''';
                EXEC(@sql);
            END
            GO
            CREATE PROCEDURE dbo.usp_Driver AS
            BEGIN
                CREATE TABLE #Results (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
                EXEC dbo.usp_Sub;
            END
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Col");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Results", finding.Column.TableQualifiedName);
    }
}
