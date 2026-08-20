using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Call-boundary half of docs/detection-checklist.md Tier 1's "call-boundary argument mismatch"
/// item - reuses <see cref="Rules.WriteLossClassifier"/> against a caller-side variable's
/// declared type vs. the callee's own declared parameter type. Built directly against a
/// hand-constructed <see cref="ProcCallGraph"/> - the graph itself (including
/// <see cref="ProcCallArgument.CallerArgumentType"/> resolution) is covered separately in
/// <c>ProcCallGraphBuilderTests</c>.
/// </summary>
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
        // A literal argument has no CallerVariableName at all - nothing to look up a declared
        // type for, so it's out of scope for this rule by construction (the literal's own value,
        // not a declared type, is what would matter, and this rule only reasons about types).
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
