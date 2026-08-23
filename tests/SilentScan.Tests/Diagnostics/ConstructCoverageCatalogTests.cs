using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Tests.Diagnostics;

public sealed class ConstructCoverageCatalogTests
{
    [Fact]
    public void Instance_LoadsEmbeddedMatrix_NonEmpty()
    {
        var entries = ConstructCoverageCatalog.Instance.Entries;

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Entries_AllCarryANonEmptyGroup()
    {
        var ungrouped = ConstructCoverageCatalog.Instance.Entries
            .Where(e => string.IsNullOrWhiteSpace(e.Group))
            .Select(e => e.Construct)
            .ToList();

        Assert.True(ungrouped.Count == 0, $"Rows with no group: {string.Join(", ", ungrouped)}");
    }

    [Fact]
    public void Entries_NoDuplicateConstructNames()
    {
        var duplicates = ConstructCoverageCatalog.Instance.Entries
            .GroupBy(e => e.Construct, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void GapEntries_AllCarryARationale()
    {
        var unexplained = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.Status == ConstructCoverageStatus.Gap && string.IsNullOrWhiteSpace(e.Rationale))
            .Select(e => e.Construct)
            .ToList();

        Assert.True(unexplained.Count == 0, $"Gap rows with no rationale: {string.Join(", ", unexplained)}");
    }

    [Fact]
    public void LedgeredEntries_AllCarryARationale()
    {
        var unexplained = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.Status == ConstructCoverageStatus.Ledgered && string.IsNullOrWhiteSpace(e.Rationale))
            .Select(e => e.Construct)
            .ToList();

        Assert.True(unexplained.Count == 0, $"Ledgered rows with no rationale: {string.Join(", ", unexplained)}");
    }

    [Fact]
    public void HandledEntries_FixtureReferencesResolveToRealFiles()
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root from test output directory.");

        var missing = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.VerifiedBy is { } v && v.EndsWith(".sql", StringComparison.Ordinal))
            .Where(e => !File.Exists(Path.Combine(root, e.VerifiedBy!)))
            .Select(e => $"{e.Construct} -> {e.VerifiedBy}")
            .ToList();

        Assert.True(missing.Count == 0, $"Fixture references that do not exist on disk: {string.Join(", ", missing)}");
    }

    [Fact]
    public void HandledEntries_TestClassReferencesExistInThisAssembly()
    {
        var testAssembly = typeof(ConstructCoverageCatalogTests).Assembly;

        var missing = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.VerifiedBy is { } v && !v.EndsWith(".sql", StringComparison.Ordinal))
            .Where(e => testAssembly.GetType($"SilentScan.Tests.{e.VerifiedBy}", throwOnError: false) is null)
            .Select(e => $"{e.Construct} -> {e.VerifiedBy}")
            .ToList();

        Assert.True(missing.Count == 0, $"VerifiedBy test-class references that do not resolve: {string.Join(", ", missing)}");
    }

    [Fact]
    public void HandledEntries_AlwaysCarryAVerifiedByReference()
    {
        var unverified = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.Status == ConstructCoverageStatus.Handled && string.IsNullOrWhiteSpace(e.VerifiedBy))
            .Select(e => e.Construct)
            .ToList();

        Assert.True(unverified.Count == 0, $"Handled rows with no fixture/test reference: {string.Join(", ", unverified)}");
    }

[Fact]
    public void LedgeredEntries_AlwaysCarryAVerifiedByReference()
    {
        var unverified = ConstructCoverageCatalog.Instance.Entries
            .Where(e => e.Status == ConstructCoverageStatus.Ledgered && string.IsNullOrWhiteSpace(e.VerifiedBy))
            .Select(e => e.Construct)
            .ToList();

        Assert.True(unverified.Count == 0, $"Ledgered rows with no fixture/test reference: {string.Join(", ", unverified)}");
    }

private static readonly HashSet<string> DocumentedNonScriptDomConstructNames = new(StringComparer.Ordinal)
    {
        "MultiStatementTvfReturnVariable",
    };

    [Fact]
    public void BareTypeNameConstructs_ResolveToARealScriptDomType()
    {
        var scriptDomAssembly = typeof(TSqlFragment).Assembly;

        var phantoms = ConstructCoverageCatalog.Instance.Entries
            .Select(e => e.Construct)
            .Where(name => name.Length > 0 && char.IsUpper(name[0]) && !name.Contains(' ', StringComparison.Ordinal))
            .Where(name => !DocumentedNonScriptDomConstructNames.Contains(name))
            .Where(name => scriptDomAssembly.GetType($"Microsoft.SqlServer.TransactSql.ScriptDom.{name}") is null)
            .ToList();

        Assert.True(phantoms.Count == 0, $"Construct names with no matching ScriptDom type: {string.Join(", ", phantoms)}");
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
