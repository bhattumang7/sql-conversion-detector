using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class ProcCallArgumentMismatchScannerTests
{
    private static ProcCallGraph GraphWith(ProcCallArgument argument) =>
        new([new ProcCallEdge("dbo.Caller", "dbo.Callee", new SourceSpan("test.sql", 3, 5), [argument])]);

    [Fact]
    public void UnicodeToNonUnicodeRisk_Fires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 20), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.NVarChar, Length: 20));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("dbo.Callee", finding.CalleeQualifiedName);
        Assert.Equal("@P", finding.FormalParameterName);
        Assert.Equal("@Local", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
        Assert.Equal("test.sql", finding.SourcePath);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void ApproximateToExactTruncationRisk_Fires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Int), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.Float));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.ApproximateToExactTruncation, finding.Kind);
    }

    [Fact]
    public void MatchingTypes_NeverFires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Int), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.Int));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }

    [Fact]
    public void LiteralArgumentWithUnresolvedType_NeverFires()
    {

        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Int), FormalParameterIsOutput: false,
            CallerVariableName: null, IsLiteral: true, CallerArgumentType: null);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }

    [Fact]
    public void LiteralArgumentWithResolvedNarrowingType_Fires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 3), FormalParameterIsOutput: false,
            CallerVariableName: null, IsLiteral: true, CallerArgumentType: new SqlType(SqlTypeCategory.VarChar, Length: 10));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Equal("@P", finding.CallerExpressionDisplay);
    }

    [Fact]
    public void MoneySourceIntoNarrowerDecimalTarget_Fires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 2), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.Money));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void NarrowerVarcharTarget_Fires()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 3), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.VarChar, Length: 10));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
    }

    [Fact]
    public void UnresolvedCallerType_NeverGuesses()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Int), FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: null);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvedFormalParameterType_NeverGuesses()
    {
        var argument = new ProcCallArgument(
            "@P", null, FormalParameterIsOutput: false,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.Int));

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }

    [Fact]
    public void OutputParameter_NarrowerCallerVariable_FiresAsWriteback()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 10), FormalParameterIsOutput: true,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.VarChar, Length: 3),
            CallSiteHasOutputKeyword: true);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
        Assert.Equal("@Local", finding.CallerExpressionDisplay);
    }

    [Fact]
    public void UnassignedCallerVariable_NarrowerThanFormal_NeverFiresInputNarrowing()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 3), FormalParameterIsOutput: true,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.VarChar, Length: 10),
            CallSiteHasOutputKeyword: true, CallerVariableWasAssignedBeforeCall: false);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }

    [Fact]
    public void OutputParameter_CallSiteOmitsOutputKeyword_NeverFiresAsWriteback()
    {
        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.VarChar, Length: 10), FormalParameterIsOutput: true,
            "@Local", IsLiteral: false, CallerArgumentType: new SqlType(SqlTypeCategory.VarChar, Length: 3),
            CallSiteHasOutputKeyword: false);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
    }
}
