using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlOperandPositionClassifierTests
{
    private static DynamicSqlOperandPosition ClassifyAt(string sql, string marker)
    {
        var offset = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"marker '{marker}' not found in '{sql}'");

        var parseResult = SqlScriptParser.ParseText("probe.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        return DynamicSqlOperandPositionClassifier.Classify(parseResult.Fragment, offset);
    }

    [Fact]
    public void ComparisonRightHandSideStringLiteral_IsValue()
    {
        Assert.Equal(DynamicSqlOperandPosition.Value, ClassifyAt("SELECT * FROM dbo.T WHERE Code = 'ABC'", "ABC"));
    }

    [Fact]
    public void InListLiteral_IsValue()
    {
        Assert.Equal(DynamicSqlOperandPosition.Value, ClassifyAt("SELECT * FROM dbo.T WHERE Code IN ('ABC', 'DEF')", "DEF"));
    }

    [Fact]
    public void LikePatternLiteral_IsValue()
    {
        Assert.Equal(DynamicSqlOperandPosition.Value, ClassifyAt("SELECT * FROM dbo.T WHERE Code LIKE 'ABC%'", "ABC"));
    }

    [Fact]
    public void FunctionCallArgumentLiteral_IsValue()
    {
        Assert.Equal(DynamicSqlOperandPosition.Value, ClassifyAt("SELECT * FROM dbo.T WHERE Code = UPPER('abc')", "abc"));
    }

    [Fact]
    public void TableNameIdentifier_IsIdentifier()
    {
        Assert.Equal(DynamicSqlOperandPosition.Identifier, ClassifyAt("SELECT * FROM dbo.CustomerOrders", "CustomerOrders"));
    }

    [Fact]
    public void ColumnNameIdentifier_IsIdentifier()
    {
        Assert.Equal(DynamicSqlOperandPosition.Identifier, ClassifyAt("SELECT * FROM dbo.T WHERE CustomerCode = 'ABC'", "CustomerCode"));
    }

    [Fact]
    public void SchemaQualifierIdentifier_IsIdentifier()
    {
        Assert.Equal(DynamicSqlOperandPosition.Identifier, ClassifyAt("SELECT * FROM sales.T", "sales"));
    }

    [Fact]
    public void KeywordPosition_IsAmbiguous()
    {
        Assert.Equal(DynamicSqlOperandPosition.Ambiguous, ClassifyAt("SELECT * FROM dbo.T WHERE Code = 'ABC'", "WHERE"));
    }
}
