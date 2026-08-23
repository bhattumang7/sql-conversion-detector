using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ScalarUdfInlineabilityClassifierTests
{
    private static ScalarUdfInfo TSqlInfo(bool? engineIsInlineable = null, string? blocker = null, int? tableReferenceCount = null) =>
        new(ScalarUdfKind.TSql, IsSchemaBound: true, engineIsInlineable, blocker, ClrDataAccess: null, tableReferenceCount);

    [Fact]
    public void CompatibilityLevelBelow150_OverridesEngineReportedInlineable()
    {
        var info = TSqlInfo(engineIsInlineable: true);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 140);

        Assert.Equal(ScalarUdfInlineability.NotInlineable, inlineability);
        Assert.Contains("140", blocker!);
        Assert.Contains("150", blocker!);
    }

    [Fact]
    public void CompatibilityLevel150_WithEngineReportedInlineable_ReportsInlineable()
    {
        var info = TSqlInfo(engineIsInlineable: true);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 150);

        Assert.Equal(ScalarUdfInlineability.Inlineable, inlineability);
        Assert.Null(blocker);
    }

    [Fact]
    public void CompatibilityLevelUnknown_WithEngineReportedInlineable_ReportsInlineable()
    {
        var info = TSqlInfo(engineIsInlineable: true);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: null);

        Assert.Equal(ScalarUdfInlineability.Inlineable, inlineability);
        Assert.Null(blocker);
    }

    [Fact]
    public void TableReferenceCountOverLimit_OverridesEngineReportedInlineable()
    {
        var info = TSqlInfo(engineIsInlineable: true, tableReferenceCount: 50);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 150);

        Assert.Equal(ScalarUdfInlineability.NotInlineable, inlineability);
        Assert.Contains("50", blocker!);
        Assert.Contains("49", blocker!);
    }

    [Fact]
    public void TableReferenceCountAtLimit_DoesNotBlock()
    {
        var info = TSqlInfo(engineIsInlineable: true, tableReferenceCount: 49);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 150);

        Assert.Equal(ScalarUdfInlineability.Inlineable, inlineability);
        Assert.Null(blocker);
    }

    [Fact]
    public void TableReferenceCountUnknown_WithEngineReportedInlineable_ReportsInlineable()
    {
        var info = TSqlInfo(engineIsInlineable: true, tableReferenceCount: null);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 150);

        Assert.Equal(ScalarUdfInlineability.Inlineable, inlineability);
        Assert.Null(blocker);
    }

    [Fact]
    public void ClrUdf_IgnoresCompatibilityLevelAndTableReferenceCount()
    {
        var info = new ScalarUdfInfo(ScalarUdfKind.Clr, IsSchemaBound: null, EngineIsInlineable: null, InlineabilityBlocker: null, ClrDataAccess: null, InlineabilityTableReferenceCount: 1000);

        var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, compatibilityLevel: 140);

        Assert.Equal(ScalarUdfInlineability.NotInlineable, inlineability);
        Assert.Equal("CLR scalar UDFs are never inlined", blocker);
    }
}
