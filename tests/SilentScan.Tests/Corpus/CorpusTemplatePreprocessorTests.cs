using SilentScan.Core.Corpus;

namespace SilentScan.Tests.Corpus;

public sealed class CorpusTemplatePreprocessorTests
{
    [Fact]
    public void Apply_NoSubstitutions_ReturnsTextUnchanged()
    {
        Assert.Equal("SELECT 1;", CorpusTemplatePreprocessor.Apply(null, "SELECT 1;"));
    }

    [Fact]
    public void Apply_EmptySubstitutionMap_ReturnsTextUnchanged()
    {
        Assert.Equal("SELECT 1;", CorpusTemplatePreprocessor.Apply(new Dictionary<string, string>(), "SELECT 1;"));
    }

    [Fact]
    public void Apply_SubstitutionMap_ReplacesEveryToken()
    {
        // docs/audit-remediation-plan.md Phase 6.1: the map comes from the manifest entry, not
        // a hardcoded repo-name switch - adding a new repo with its own tokens is a manifest
        // edit only.
        var substitutions = new Dictionary<string, string>
        {
            ["{databaseOwner}"] = "dbo.",
            ["{objectQualifier}"] = string.Empty,
        };

        var result = CorpusTemplatePreprocessor.Apply(
            substitutions,
            "SELECT * FROM {databaseOwner}{objectQualifier}Users;");

        Assert.Equal("SELECT * FROM dbo.Users;", result);
    }

    [Fact]
    public void Apply_UnrelatedRepoWithNoManifestEntry_ReturnsTextUnchanged()
    {
        Assert.Equal("CREATE TABLE dbo.T (Id INT);", CorpusTemplatePreprocessor.Apply(null, "CREATE TABLE dbo.T (Id INT);"));
    }
}
