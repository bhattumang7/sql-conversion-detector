using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Confirms <see cref="SilentScan.Core.Predicates.ForcedParameterizationFindingKind.GroupByExpressionLiteral"/>
/// against a real engine: under PARAMETERIZATION FORCED, a literal inside a GROUP BY expression
/// stays untouched in the cached plan while an unrelated WHERE-clause literal in the SAME
/// statement correctly parameterizes - proving a real, isolated skip mechanism rather than a
/// coincidence of the probe SQL's shape. Scoped to this test's own <c>DB_ID()</c> instead of
/// <c>DBCC FREEPROCCACHE</c> (which would race other Oracle tests' plan-cache reads running
/// concurrently in the same instance), and to the normalized/parameterized cache entry
/// specifically (<c>LIKE '%(@%'</c>) - the engine caches BOTH that entry and the original literal
/// ad-hoc text side by side, and reading the wrong one would silently defeat this test's own
/// point. The table-name filter avoids the schema-qualifier dot entirely
/// (<c>GroupByProbeTable</c>, not <c>dbo.T</c>) - the normalized text re-spaces punctuation
/// (<c>dbo . T</c>, confirmed directly), which would otherwise make a literal <c>dbo.T</c> pattern
/// never match the very entry this test needs to read.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ForcedParameterizationGroupByOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ForcedParameterizationGroupByOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.GroupByProbeTable (Id INT NOT NULL, Val INT NOT NULL);
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var alterParameterization = connection.CreateCommand();
        alterParameterization.CommandText = $"ALTER DATABASE [{DatabaseName}] SET PARAMETERIZATION FORCED;";
        await alterParameterization.ExecuteNonQueryAsync();
    }

    private async Task<string> RunAndCaptureParameterizedPlanTextAsync(string probeSql)
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = probeSql;
            await probe.ExecuteNonQueryAsync();
        }

        await using var readCachedText = connection.CreateCommand();
        readCachedText.CommandText = """
            SELECT st.text
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            WHERE st.dbid = DB_ID()
              AND st.text LIKE @tablePattern
              AND st.text LIKE @groupByPattern
              AND st.text LIKE @parameterizedPattern
              AND st.text NOT LIKE @selfExclusionPattern;
            """;
        readCachedText.Parameters.AddWithValue("@tablePattern", "%GroupByProbeTable%");
        readCachedText.Parameters.AddWithValue("@groupByPattern", "%group by%");
        readCachedText.Parameters.AddWithValue("@parameterizedPattern", "%(@%");
        readCachedText.Parameters.AddWithValue("@selfExclusionPattern", "%dm_exec_cached_plans%");
        await using var reader = await readCachedText.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "Expected a normalized/parameterized plan-cache entry for the probe statement - none was found.");
        return reader.GetString(0);
    }

    [Fact]
    public async Task GroupByExpressionLiteral_StaysUnparameterized_WhileSiblingWhereClauseLiteralParameterizes()
    {
        var cachedText = await RunAndCaptureParameterizedPlanTextAsync(
            "SELECT Id + 1 AS grp, COUNT(*) FROM dbo.GroupByProbeTable WHERE Val > 5 GROUP BY (Id + 1);");
        var normalized = cachedText.Replace(" ", string.Empty, StringComparison.Ordinal);

        // The WHERE-clause literal (5) parameterized to a real parameter marker...
        Assert.Contains("Val>@", normalized, StringComparison.OrdinalIgnoreCase);
        // ...but the GROUP BY expression's own literal (1) survived verbatim in the SAME plan.
        Assert.Contains("groupby(Id+1)", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupByPlainColumn_FullyParameterizes_NoLiteralSurvives()
    {
        var cachedText = await RunAndCaptureParameterizedPlanTextAsync(
            "SELECT Id, COUNT(*) FROM dbo.GroupByProbeTable WHERE Val > 7 GROUP BY Id;");

        // Every literal in this statement (the WHERE-clause 7) parameterizes - no bare literal
        // survives in the cached text at all, unlike the GROUP BY-expression case above.
        Assert.DoesNotContain("7", cachedText, StringComparison.Ordinal);
    }
}
