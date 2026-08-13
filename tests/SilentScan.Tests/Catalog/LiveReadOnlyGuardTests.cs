using Microsoft.Data.SqlClient;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Catalog;

/// <summary>
/// The code-level backstop for "a connected live database is scanned read-only" (CLAUDE.md hard
/// scope). Every SQL string every live reader sends passes through <see cref="LiveReadOnlyGuard"/>
/// via its <c>CreateReadOnlyCommand</c> extension - this pins the guard's own logic directly, so
/// a future edit that weakens it (e.g. widening the allowed statement set) fails here first.
/// </summary>
public sealed class LiveReadOnlyGuardTests
{
    [Fact]
    public void AssertSelectOnly_PlainSelect_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertSelectOnly("SELECT name FROM sys.tables;");

    [Fact]
    public void AssertSelectOnly_SelectWithJoinsAndWhere_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertSelectOnly("""
            SELECT c.name, t.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.is_ms_shipped = 0;
            """);

    [Theory]
    [InlineData("INSERT INTO dbo.T (Id) VALUES (1);")]
    [InlineData("UPDATE dbo.T SET Id = 1;")]
    [InlineData("DELETE FROM dbo.T;")]
    [InlineData("DROP TABLE dbo.T;")]
    [InlineData("CREATE TABLE dbo.T (Id INT);")]
    [InlineData("ALTER TABLE dbo.T ADD Id INT;")]
    [InlineData("EXEC dbo.usp_DoSomething;")]
    [InlineData("TRUNCATE TABLE dbo.T;")]
    [InlineData("SELECT 1; DROP TABLE dbo.T;")]
    public void AssertSelectOnly_AnyNonSelectStatement_Throws(string sql) =>
        Assert.Throws<InvalidOperationException>(() => LiveReadOnlyGuard.AssertSelectOnly(sql));

    [Fact]
    public void AssertSelectOnly_UnparseableText_Throws() =>
        Assert.Throws<InvalidOperationException>(() => LiveReadOnlyGuard.AssertSelectOnly("this is not SQL at all ;;; ("));

    [Fact]
    public void CreateReadOnlyCommand_WithoutExplicitTimeout_UsesTheDefault()
    {
        using var connection = new SqlConnection("Server=(local);Database=SilentScanTests;Integrated Security=true;");

        using var command = connection.CreateReadOnlyCommand("SELECT name FROM sys.tables;");

        Assert.Equal(LiveReadOnlyGuard.DefaultCommandTimeoutSeconds, command.CommandTimeout);
        Assert.Equal(300, LiveReadOnlyGuard.DefaultCommandTimeoutSeconds);
    }

    [Fact]
    public void CreateReadOnlyCommand_WithExplicitTimeout_UsesIt()
    {
        using var connection = new SqlConnection("Server=(local);Database=SilentScanTests;Integrated Security=true;");

        using var command = connection.CreateReadOnlyCommand("SELECT name FROM sys.tables;", commandTimeoutSeconds: 45);

        Assert.Equal(45, command.CommandTimeout);
    }

    [Fact]
    public void CreateReadOnlyCommand_NonSelectStatement_ThrowsBeforeBuildingACommand()
    {
        using var connection = new SqlConnection("Server=(local);Database=SilentScanTests;Integrated Security=true;");

        Assert.Throws<InvalidOperationException>(() => connection.CreateReadOnlyCommand("DROP TABLE dbo.T;"));
    }
}
