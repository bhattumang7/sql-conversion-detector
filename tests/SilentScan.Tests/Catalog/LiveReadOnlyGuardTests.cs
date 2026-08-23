using Microsoft.Data.SqlClient;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Catalog;

[Trait("Category", "Oracle")]
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

    [Fact]
    public void AssertSelectOnly_ViewDescribeBatchCrossApply_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertSelectOnly("""
            SELECT s.name AS schema_name, o.name AS object_name,
                   r.error_number, r.error_message,
                   r.name AS column_name, ty.name AS type_name, r.max_length, r.precision, r.scale, r.collation_name
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            CROSS APPLY sys.dm_exec_describe_first_result_set(
                N'SELECT * FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), NULL, 0) r
            LEFT JOIN sys.types ty ON ty.user_type_id = r.system_type_id
            WHERE o.type = 'V' AND o.is_ms_shipped = 0
            ORDER BY o.object_id, r.column_ordinal;
            """);

    [Fact]
    public void AssertSelectOnly_FunctionDescribeByParameterizedProbeText_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertSelectOnly("""
            SELECT r.error_number, r.error_message,
                   r.name AS column_name, ty.name AS type_name, r.max_length, r.precision, r.scale, r.collation_name
            FROM sys.dm_exec_describe_first_result_set(@probeText, NULL, 0) r
            LEFT JOIN sys.types ty ON ty.user_type_id = r.system_type_id
            ORDER BY r.column_ordinal;
            """);

    [Theory]
    [InlineData("SELECT * FROM [dbo].[vw_Orders];")]
    [InlineData("SELECT * FROM [dbo].[fn_Orders](CAST(NULL AS INT), CAST(NULL AS VARCHAR(50)));")]
    [InlineData("SELECT * FROM [dbo].[fn_NoArgs]();")]
    public void AssertSelectOnly_SynthesizedDescribeProbeText_DoesNotThrow(string probeText) =>
        LiveReadOnlyGuard.AssertSelectOnly(probeText);

    [Fact]
    public void AssertDescribeFirstResultSetProbeOnly_PlainSelect_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly("SELECT name FROM sys.tables;");

    [Fact]
    public void AssertDescribeFirstResultSetProbeOnly_NamedProcedureExecNoArguments_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly("EXEC [dbo].[usp_AllOrders];");

    [Fact]
    public void AssertDescribeFirstResultSetProbeOnly_NamedProcedureExecWithPositionalArguments_DoesNotThrow() =>
        LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly("EXEC [dbo].[usp_Find] NULL, NULL;");

    [Theory]
    [InlineData("EXEC('SELECT 1');")]
    [InlineData("EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC(@sql);")]
    public void AssertDescribeFirstResultSetProbeOnly_StringFormExec_StillThrows(string sql) =>
        Assert.Throws<InvalidOperationException>(() => LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly(sql));

    [Theory]
    [InlineData("INSERT INTO dbo.T (Id) VALUES (1);")]
    [InlineData("DROP TABLE dbo.T;")]
    [InlineData("EXEC [dbo].[usp_Find]; DROP TABLE dbo.T;")]
    public void AssertDescribeFirstResultSetProbeOnly_AnyOtherStatement_Throws(string sql) =>
        Assert.Throws<InvalidOperationException>(() => LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly(sql));

    [Fact]
    public void AssertSelectOnly_NamedProcedureExec_StillThrows() =>
        Assert.Throws<InvalidOperationException>(() => LiveReadOnlyGuard.AssertSelectOnly("EXEC [dbo].[usp_AllOrders];"));
}
