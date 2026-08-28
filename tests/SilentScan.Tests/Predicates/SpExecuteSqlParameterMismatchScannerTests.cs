using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class SpExecuteSqlParameterMismatchScannerTests
{
    private static ProcCallGraph GraphWith(SpExecuteSqlParameterBinding binding) =>
        new([], [new SpExecuteSqlCallSite("dbo.Caller", new SourceSpan("test.sql", 3, 5), [binding])]);

    [Fact]
    public void NarrowerDeclaredParameter_Fires()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Rate", new SqlType(SqlTypeCategory.Int), DeclaredIsOutput: false,
            "@rate", new SqlType(SqlTypeCategory.Float), CallSiteHasOutputKeyword: false,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: false);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("@Rate", finding.ParameterName);
        Assert.Equal("@rate", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.ApproximateToExactTruncation, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
        Assert.Equal("test.sql", finding.SourcePath);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void MatchingTypes_NeverFires()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Rate", new SqlType(SqlTypeCategory.Int), DeclaredIsOutput: false,
            "@rate", new SqlType(SqlTypeCategory.Int), CallSiteHasOutputKeyword: false,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: false);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        Assert.Empty(findings);
    }

    [Fact]
    public void UnassignedCallerVariable_NeverFiresInputNarrowing()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Rate", new SqlType(SqlTypeCategory.VarChar, Length: 3), DeclaredIsOutput: false,
            "@rate", new SqlType(SqlTypeCategory.VarChar, Length: 10), CallSiteHasOutputKeyword: false,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: false, CallerFlowApproximate: false);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        Assert.Empty(findings);
    }

    [Fact]
    public void OutputParameter_NarrowerCallerVariable_FiresAsWriteback()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Tax", new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 4), DeclaredIsOutput: true,
            "@tax", new SqlType(SqlTypeCategory.Decimal, Precision: 4, Scale: 1), CallSiteHasOutputKeyword: true,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: false);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
        Assert.Equal("@tax", finding.CallerExpressionDisplay);
    }

    [Fact]
    public void OutputParameter_CallSiteOmitsOutputKeyword_NeverFiresAsWriteback()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Tax", new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 4), DeclaredIsOutput: true,
            "@tax", new SqlType(SqlTypeCategory.Decimal, Precision: 4, Scale: 1), CallSiteHasOutputKeyword: false,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: false);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        Assert.Empty(findings);
    }

    [Fact]
    public void InputNarrowing_ApproximateFlow_FiresAtMediumConfidence()
    {
        var binding = new SpExecuteSqlParameterBinding(
            "@Rate", new SqlType(SqlTypeCategory.VarChar, Length: 3), DeclaredIsOutput: false,
            "@rate", new SqlType(SqlTypeCategory.VarChar, Length: 10), CallSiteHasOutputKeyword: false,
            CallerArgumentExpression: null, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: true);

        var findings = SpExecuteSqlParameterMismatchScanner.Scan(GraphWith(binding));

        var finding = Assert.Single(findings);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }
}
