using SilentScan.Core.Catalog;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Catalog;

/// <summary>
/// Pure, no-database coverage for <see cref="LiveDescribeProbeBuilder"/> - every input is
/// already-read catalog metadata, so this is cheap to run exhaustively without the Docker oracle
/// the reader it feeds needs.
/// </summary>
public sealed class LiveDescribeProbeBuilderTests
{
    [Fact]
    public void BuildViewProbe_BracketsSchemaAndObjectName() =>
        Assert.Equal("SELECT * FROM [dbo].[vw_Orders];", LiveDescribeProbeBuilder.BuildViewProbe("dbo.vw_Orders"));

    [Fact]
    public void BuildViewProbe_EscapesABracketInTheObjectName() =>
        Assert.Equal("SELECT * FROM [dbo].[vw_Order]]s];", LiveDescribeProbeBuilder.BuildViewProbe("dbo.vw_Order]s"));

    [Fact]
    public void BuildFunctionProbe_NoParameters_EmitsEmptyParens()
    {
        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_AllOrders", []);

        Assert.Null(reason);
        Assert.Equal("SELECT * FROM [dbo].[fn_AllOrders]();", probe);
    }

    [Fact]
    public void BuildFunctionProbe_MultipleParameters_OrdersArgumentsAsGiven()
    {
        var parameters = new List<FunctionParameterSpec>
        {
            new("@id", new SqlType(SqlTypeCategory.Int), IsTableType: false),
            new("@name", new SqlType(SqlTypeCategory.VarChar, Length: 50), IsTableType: false),
        };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_Find", parameters);

        Assert.Null(reason);
        Assert.Equal("SELECT * FROM [dbo].[fn_Find](CAST(NULL AS INT), CAST(NULL AS VARCHAR(50)));", probe);
    }

    [Fact]
    public void BuildFunctionProbe_DecimalParameter_RendersPrecisionAndScale()
    {
        var parameters = new List<FunctionParameterSpec> { new("@amount", new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 2), IsTableType: false) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_Totals", parameters);

        Assert.Null(reason);
        Assert.Equal("SELECT * FROM [dbo].[fn_Totals](CAST(NULL AS DECIMAL(10,2)));", probe);
    }

    [Fact]
    public void BuildFunctionProbe_TableValuedParameter_ReturnsNullWithAReasonNamingIt()
    {
        var parameters = new List<FunctionParameterSpec> { new("@ids", Type: null, IsTableType: true) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_Filter", parameters);

        Assert.Null(probe);
        Assert.Contains("@ids", reason);
        Assert.Contains("table-valued parameter", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFunctionProbe_ParameterWithNoResolvedType_ReturnsNullWithAReasonNamingIt()
    {
        var parameters = new List<FunctionParameterSpec> { new("@doc", Type: null, IsTableType: false) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_Read", parameters);

        Assert.Null(probe);
        Assert.Contains("@doc", reason);
    }

    [Fact]
    public void BuildFunctionProbe_ParameterWithUnrenderableSqlType_ReturnsNullWithAReasonNamingIt()
    {
        // SqlVariant/UserDefined-adjacent categories with no fixed T-SQL DECLARE spelling -
        // SqlTypeSyntaxFormatter.Format itself returns null for these; the probe builder must
        // surface that as an unrenderable reason rather than emitting broken SQL.
        var parameters = new List<FunctionParameterSpec> { new("@tag", new SqlType(SqlTypeCategory.UserDefined), IsTableType: false) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe("dbo.fn_Tagged", parameters);

        Assert.Null(probe);
        Assert.Contains("@tag", reason);
    }

    [Fact]
    public void BuildProcedureProbe_NoParameters_EmitsBareExec()
    {
        var (probe, reason) = LiveDescribeProbeBuilder.BuildProcedureProbe("dbo.usp_AllOrders", []);

        Assert.Null(reason);
        Assert.Equal("EXEC [dbo].[usp_AllOrders];", probe);
    }

    [Fact]
    public void BuildProcedureProbe_MultipleParameters_PositionalBareNullArguments()
    {
        // EXECUTE's own grammar accepts only a constant or a variable as an argument value, never
        // an arbitrary expression - CAST(NULL AS type) (used for the function-probe sibling) is a
        // real parse error here, oracle-confirmed (Msg 156). A bare, untyped NULL compiles and
        // implicitly converts to whatever the parameter's own declared type is.
        var parameters = new List<ProcedureParameterSpec>
        {
            new("@id", IsTableType: false, IsOutput: false),
            new("@name", IsTableType: false, IsOutput: false),
        };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildProcedureProbe("dbo.usp_Find", parameters);

        Assert.Null(reason);
        Assert.Equal("EXEC [dbo].[usp_Find] NULL, NULL;", probe);
    }

    [Fact]
    public void BuildProcedureProbe_OutputParameter_ReturnsNullWithAReasonNamingIt()
    {
        var parameters = new List<ProcedureParameterSpec> { new("@total", IsTableType: false, IsOutput: true) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildProcedureProbe("dbo.usp_Totals", parameters);

        Assert.Null(probe);
        Assert.Contains("@total", reason);
        Assert.Contains("OUTPUT", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProcedureProbe_TableValuedParameter_ReturnsNullWithAReasonNamingIt()
    {
        var parameters = new List<ProcedureParameterSpec> { new("@ids", IsTableType: true, IsOutput: false) };

        var (probe, reason) = LiveDescribeProbeBuilder.BuildProcedureProbe("dbo.usp_Filter", parameters);

        Assert.Null(probe);
        Assert.Contains("@ids", reason);
        Assert.Contains("table-valued parameter", reason, StringComparison.Ordinal);
    }

}
