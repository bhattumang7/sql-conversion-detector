using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>Turns a catalog <see cref="ScalarUdfInfo"/> into a finding's own <see cref="ScalarUdfInlineability"/> read - shared by <see cref="ScalarUdfScanner"/> and <see cref="SchemaDependencyScanner"/> so the engine-flag-over-blocker-scan preference rule can't drift between the two.</summary>
public static class ScalarUdfInlineabilityClassifier
{
    public static (ScalarUdfInlineability Inlineability, string? Blocker) Classify(ScalarUdfInfo info)
    {
        if (info.Kind == ScalarUdfKind.Clr)
        {
            return (ScalarUdfInlineability.NotInlineable, "CLR scalar UDFs are never inlined");
        }

        if (info.EngineIsInlineable is true)
        {
            return (ScalarUdfInlineability.Inlineable, null);
        }

        if (info.EngineIsInlineable is false)
        {
            return (ScalarUdfInlineability.NotInlineable, info.InlineabilityBlocker);
        }

        return info.InlineabilityBlocker is { Length: > 0 } blocker
            ? (ScalarUdfInlineability.NotInlineable, blocker)
            : (ScalarUdfInlineability.Unknown, null);
    }
}
