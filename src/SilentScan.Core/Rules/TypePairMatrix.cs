using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public sealed class TypePairMatrix
{
    public static TypePairMatrix Instance { get; } = LoadEmbedded();

    private readonly Dictionary<(SqlTypeCategory Column, SqlTypeCategory Other, string? Collation), TypePairOutcome> _byKey;

    public string ServerVersion { get; }

    public string ProbedAtUtc { get; }

    public IReadOnlyList<TypePairOutcome> Entries { get; }

    internal TypePairMatrix(string serverVersion, string probedAtUtc, IReadOnlyList<TypePairOutcome> entries)
    {
        ServerVersion = serverVersion;
        ProbedAtUtc = probedAtUtc;
        Entries = entries;
        _byKey = entries.ToDictionary(e => (e.ColumnCategory, e.OtherCategory, e.CollationName));
    }

public TypePairOutcome? TryGetOutcome(SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, string? collationName = null) =>
        _byKey.TryGetValue((columnCategory, otherCategory, collationName), out var outcome) ? outcome : null;

public TypePairOutcome? TryGetOutcomeAgreeingAcrossCollations(SqlTypeCategory columnCategory, SqlTypeCategory otherCategory)
    {
        var variants = Entries.Where(e => e.ColumnCategory == columnCategory && e.OtherCategory == otherCategory && e.CollationName is not null).ToList();
        return TryGetAgreeingOutcome(variants);
    }

public TypePairOutcome? TryGetOutcomeAgreeingWithinFamily(SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, bool isSqlFamily)
    {
        var variants = Entries.Where(e =>
            e.ColumnCategory == columnCategory
            && e.OtherCategory == otherCategory
            && e.CollationName is not null
            && new Collation(e.CollationName).IsSqlFamily == isSqlFamily).ToList();
        return TryGetAgreeingOutcome(variants);
    }

public TypePairOutcome? TryGetOutcomeForColumnCollation(SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, Collation? collation)
    {
        if (collation is null)
        {
            return TryGetOutcomeAgreeingAcrossCollations(columnCategory, otherCategory);
        }

        return TryGetOutcome(columnCategory, otherCategory, collation.Name)
            ?? TryGetOutcomeAgreeingWithinFamily(columnCategory, otherCategory, collation.IsSqlFamily);
    }

    private static TypePairOutcome? TryGetAgreeingOutcome(List<TypePairOutcome> variants)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        var first = variants[0];
        var allAgree = variants.All(v => v.CompileFailed == first.CompileFailed
            && v.ColumnConverts == first.ColumnConverts
            && v.DynamicRangeSeekAvailable == first.DynamicRangeSeekAvailable);

        return allAgree ? first with { CollationName = null } : null;
    }

    private static TypePairMatrix LoadEmbedded()
    {
        var assembly = typeof(TypePairMatrix).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Rules.TypePairMatrix.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found - the matrix data is missing from the build.");

        var document = JsonSerializer.Deserialize<MatrixDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("TypePairMatrix.json deserialized to null.");

        var entries = document.Entries.Select(e => new TypePairOutcome(
            Enum.Parse<SqlTypeCategory>(e.ColumnCategory),
            Enum.Parse<SqlTypeCategory>(e.OtherCategory),
            e.CollationName,
            e.ColumnConverts,
            e.CompileFailed,
            e.DynamicRangeSeekAvailable)).ToList();

        return new TypePairMatrix(document.ServerVersion, document.ProbedAtUtc, entries);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record MatrixDocument(
        [property: JsonPropertyName("ServerVersion")] string ServerVersion,
        [property: JsonPropertyName("ProbedAtUtc")] string ProbedAtUtc,
        [property: JsonPropertyName("Notes")] string Notes,
        [property: JsonPropertyName("Entries")] IReadOnlyList<MatrixEntryDocument> Entries);

    private sealed record MatrixEntryDocument(
        string ColumnCategory,
        string OtherCategory,
        string? CollationName,
        bool ColumnConverts,
        bool CompileFailed,
        bool DynamicRangeSeekAvailable);
}
