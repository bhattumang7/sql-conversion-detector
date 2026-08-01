using SilentScan.Core.Corpus;

namespace SilentScan.Tests.Corpus;

public sealed class CorpusManifestLoaderTests
{
    [Fact]
    public void Load_RealCheckedInManifest_ParsesSuccessfully()
    {
        var path = FindRepoRoot() is { } root
            ? Path.Combine(root, "corpus", "manifest.json")
            : throw new InvalidOperationException("Could not locate repo root from test output directory.");

        var manifest = CorpusManifestLoader.Load(path);

        Assert.NotNull(manifest.Repos);
    }

    [Fact]
    public void Parse_ValidEntry_RoundTripsAllFields()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"],
                  "procPaths": ["db/procs/**/*.sql"],
                  "declaredCollation": "SQL_Latin1_General_CP1_CI_AS",
                  "notes": "test entry"
                }
              ]
            }
            """;

        var manifest = CorpusManifestLoader.Parse(json);

        var repo = Assert.Single(manifest.Repos);
        Assert.Equal("example", repo.Name);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", repo.CommitSha);
        Assert.Equal("MIT", repo.License);
        Assert.Equal(["db/schema/**/*.sql"], repo.DdlPaths);
        Assert.Equal(["db/procs/**/*.sql"], repo.ProcPaths);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", repo.DeclaredCollation);
    }

    [Fact]
    public void Parse_TemplateSubstitutions_RoundTrips()
    {
        // docs/audit-remediation-plan.md Phase 6.1: the substitution map lives in the manifest,
        // not a hardcoded repo-name switch in CorpusTemplatePreprocessor.
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"],
                  "templateSubstitutions": {
                    "{databaseOwner}": "dbo.",
                    "{objectQualifier}": ""
                  }
                }
              ]
            }
            """;

        var manifest = CorpusManifestLoader.Parse(json);

        var repo = Assert.Single(manifest.Repos);
        Assert.Equal("dbo.", repo.TemplateSubstitutions!["{databaseOwner}"]);
        Assert.Equal(string.Empty, repo.TemplateSubstitutions!["{objectQualifier}"]);
    }

    [Fact]
    public void Parse_NoTemplateSubstitutions_DefaultsToNull()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """;

        var manifest = CorpusManifestLoader.Parse(json);

        Assert.Null(Assert.Single(manifest.Repos).TemplateSubstitutions);
    }

    [Fact]
    public void Parse_MissingProcPathsAndCollation_DefaultsGracefully()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """;

        var manifest = CorpusManifestLoader.Parse(json);

        var repo = Assert.Single(manifest.Repos);
        Assert.Empty(repo.ProcPaths);
        Assert.Null(repo.DeclaredCollation);
    }

    [Theory]
    [InlineData("branch-name-not-a-sha")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("abc123")]
    [InlineData("")]
    public void Parse_InvalidCommitSha_ThrowsRatherThanTrackingAMovingBranch(string commitSha)
    {
        var json = $$"""
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "{{commitSha}}",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_MissingLicense_Throws()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_EmptyDdlPaths_Throws()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": []
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_MissingUrl_Throws()
    {
        var json = """
            {
              "repos": [
                {
                  "name": "example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Parse(json));
    }

    [Theory]
    [InlineData("not a real collation name")]
    [InlineData("ab")]
    [InlineData("123_starts_with_digit")]
    public void Parse_InvalidDeclaredCollationShape_Throws(string declaredCollation)
    {
        var json = $$"""
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"],
                  "declaredCollation": "{{declaredCollation}}"
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => CorpusManifestLoader.Parse(json));
    }

    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("Latin1_General_CI_AS")]
    [InlineData("Japanese_CI_AS_KS_WS")]
    public void Parse_ValidDeclaredCollationShape_Succeeds(string declaredCollation)
    {
        var json = $$"""
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"],
                  "declaredCollation": "{{declaredCollation}}"
                }
              ]
            }
            """;

        var manifest = CorpusManifestLoader.Parse(json);

        Assert.Equal(declaredCollation, Assert.Single(manifest.Repos).DeclaredCollation);
    }

    [Fact]
    public void Parse_EmptyRepoList_Succeeds()
    {
        var manifest = CorpusManifestLoader.Parse("""{ "repos": [] }""");

        Assert.Empty(manifest.Repos);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SilentScan.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
