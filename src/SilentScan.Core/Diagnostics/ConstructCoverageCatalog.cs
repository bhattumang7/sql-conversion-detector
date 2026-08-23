using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilentScan.Core.Diagnostics;

public sealed class ConstructCoverageCatalog
{

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
