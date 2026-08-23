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
        Assert.Equal("@Local", finding.CallerVariableName);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
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
    public void LiteralArgument_NeverFires()
    {

        var argument = new ProcCallArgument(
            "@P", new SqlType(SqlTypeCategory.Int), FormalParameterIsOutput: false,
            CallerVariableName: null, IsLiteral: true, CallerArgumentType: null);

        var findings = ProcCallArgumentMismatchScanner.Scan(GraphWith(argument));

        Assert.Empty(findings);
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
}
