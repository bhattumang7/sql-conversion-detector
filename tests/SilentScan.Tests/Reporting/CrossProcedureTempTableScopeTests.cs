using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// A #temp table is session-scoped in real SQL Server, not proc-scoped - a "driver" proc that
/// creates #Results and then EXECs a sub-procedure to query/insert into it is common, real
/// corpus code (unlike a table variable, which genuinely never crosses a proc boundary).
/// Previously, resolving a temp table declared in one proc from inside another it calls fell to
/// the ordinary "no known DDL" skip, even for the single-known-caller case this scan can prove
/// sound. Runs through <see cref="ScanReportBuilder"/> (via <see cref="EngineAuthoritativeScan"/>),
/// the same entry point production uses.
/// </summary>
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Results", finding.Column.TableQualifiedName);
    }

    [Fact]
    public async Task SubProcedureQueriesTempTable_TwoDifferentCallers_StaysUnresolved()
    {
        // Two callers create #Results with the SAME shape here, but this scan can't assume that
        // in general (a genuinely different shape per caller is exactly the same-name-different-
        // shape pattern CatalogBuilderTests already covers for the same-proc case) - with more
        // than one known caller there is no single #temp shape to fall back to, so this must stay
        // unresolved rather than guess which caller's shape applies.
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

        Assert.DoesNotContain(report.TypedFindings, f => f.Column.ColumnName == "Col" && f.Column.TableQualifiedName == "#Results");
        Assert.Contains(report.SkippedConstructs, s => s.ConstructKind == "FROM table reference" && s.Reason.Contains("#Results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubProcedureQueriesCallersTempTable_InsideDynamicSql_SingleKnownCaller_Resolves()
    {
        // The same single-known-caller resolution as above, but the predicate against #Results
        // is built inside dynamic SQL text in the callee, not written directly in its static
        // body - callerScopeByCalleeScope must reach the reparsed EXEC(@sql) fragment too, not
        // just the callee's own literal SQL.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Col");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Results", finding.Column.TableQualifiedName);
    }
}
