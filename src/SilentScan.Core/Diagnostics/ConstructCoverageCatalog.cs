using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilentScan.Core.Diagnostics;

/// <summary>
/// The checked-in, top-down construct coverage matrix (docs/coverage-remediation-plan.md Phase
/// 0.1) - the counterpart to <see cref="SkipLedger"/>'s bottom-up, per-scan accounting. Where the
/// ledger says what one scan actually saw, this says what the tool is designed to handle at all,
/// so a gap can be found by reading a checked-in table instead of by reading the visitor code and
/// noticing an unmatched node type. Loaded once from the embedded
/// <c>Diagnostics/ConstructCoverage.json</c> resource.
/// </summary>
public sealed class ConstructCoverageCatalog
{
    // Field order matters here: static field initializers run top-to-bottom, and Instance's
    // initializer calls LoadEmbedded(), which reads JsonOptions - JsonOptions must be declared
    // (and therefore initialized) first, or LoadEmbedded() runs against a still-null
    // JsonSerializerOptions and silently falls back to case-sensitive default matching against
    // this file's camelCase JSON, leaving every record null with no exception at the call site
    // that would explain why.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ConstructCoverageCatalog Instance { get; } = LoadEmbedded();

    public IReadOnlyList<ConstructCoverageEntry> Entries { get; }

    internal ConstructCoverageCatalog(IReadOnlyList<ConstructCoverageEntry> entries)
    {
        Entries = entries;
    }

    private static ConstructCoverageCatalog LoadEmbedded()
    {
        var assembly = typeof(ConstructCoverageCatalog).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Diagnostics.ConstructCoverage.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found - the coverage matrix is missing from the build.");

        var document = JsonSerializer.Deserialize<CoverageDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("ConstructCoverage.json deserialized to null.");

        var entries = document.Entries
            .Select(e => new ConstructCoverageEntry(e.Construct, e.Group, Enum.Parse<ConstructCoverageStatus>(e.Status), e.VerifiedBy, e.Rationale))
            .ToList();

        return new ConstructCoverageCatalog(entries);
    }

    private sealed record CoverageDocument(
        [property: JsonPropertyName("Entries")] IReadOnlyList<CoverageEntryDocument> Entries);

    private sealed record CoverageEntryDocument(
        string Construct,
        string Group,
        string Status,
        string? VerifiedBy,
        string? Rationale);
}
