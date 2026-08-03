using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SilentScan.Core.Corpus;

/// <summary>Loads and validates corpus/manifest.json (CLAUDE.md: "repo URL, commit SHA pinned, license, ...").</summary>
public static partial class CorpusManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitShaPattern();

    // SQL Server collation names are an identifier-shaped ASCII token (e.g.
    // SQL_Latin1_General_CP1_CI_AS, Latin1_General_CI_AS, Japanese_CI_AS_KS_WS) - this is a
    // shape check, not a lookup against the real collation list (CLAUDE.md: verify against the
    // Docker oracle, not by hand), so it catches typos/pasted-wrong-value without pretending to
    // validate the name is real.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{2,127}$")]
    private static partial Regex CollationNameShape();

    public static CorpusManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static CorpusManifest Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Manifest deserialized to null.");

        var repos = dto.Repos.Select(ValidateAndConvert).ToList();
        return new CorpusManifest(repos);
    }

    private static CorpusRepoEntry ValidateAndConvert(RepoDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new InvalidDataException("A corpus manifest entry is missing 'name'.");
        }

        if (string.IsNullOrWhiteSpace(dto.CommitSha) || !CommitShaPattern().IsMatch(dto.CommitSha))
        {
            throw new InvalidDataException($"'{dto.Name}': commitSha must be a full 40-character lowercase hex SHA, never a branch name (CLAUDE.md: pin the commit).");
        }

        if (string.IsNullOrWhiteSpace(dto.License))
        {
            throw new InvalidDataException($"'{dto.Name}': license is required before a repo can be scanned.");
        }

        if (string.IsNullOrWhiteSpace(dto.Url))
        {
            throw new InvalidDataException($"'{dto.Name}': url is required.");
        }

        if (dto.DdlPaths is not { Count: > 0 })
        {
            throw new InvalidDataException($"'{dto.Name}': ddlPaths is empty - a corpus entry with no declared DDL paths can't be scanned meaningfully.");
        }

        if (dto.DeclaredCollation is { Length: > 0 } collation && !CollationNameShape().IsMatch(collation))
        {
            throw new InvalidDataException($"'{dto.Name}': declaredCollation '{collation}' doesn't look like a SQL Server collation name.");
        }

        if (dto.TempdbCollation is { Length: > 0 } tempdbCollation && !CollationNameShape().IsMatch(tempdbCollation))
        {
            throw new InvalidDataException($"'{dto.Name}': tempdbCollation '{tempdbCollation}' doesn't look like a SQL Server collation name.");
        }

        return new CorpusRepoEntry(
            dto.Name,
            dto.Url,
            dto.CommitSha,
            dto.License,
            dto.DdlPaths,
            dto.ProcPaths ?? [],
            dto.DeclaredCollation,
            dto.Notes,
            dto.TemplateSubstitutions,
            dto.TempdbCollation);
    }

    private sealed record ManifestDto([property: JsonPropertyName("repos")] IReadOnlyList<RepoDto> Repos);

    private sealed record RepoDto(
        string Name,
        string Url,
        string CommitSha,
        string License,
        IReadOnlyList<string>? DdlPaths,
        IReadOnlyList<string>? ProcPaths,
        string? DeclaredCollation,
        string? Notes,
        IReadOnlyDictionary<string, string>? TemplateSubstitutions,
        string? TempdbCollation = null);
}
