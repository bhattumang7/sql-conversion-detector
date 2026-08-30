using System.Reflection;
using System.Text.RegularExpressions;

namespace SilentScan.Tests.Architecture;

public sealed partial class TypeInferenceConventionTests
{
    private static readonly string[] AllowedExpressionTypeInferencerCallers =
    [
        Path.Combine("Lineage", "ScalarExpressionResolver.cs"),
        Path.Combine("Catalog", "ComputedColumnTypeResolver.cs"),
        Path.Combine("Predicates", "TypedPredicateExtractor.cs"),
    ];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\bExpressionTypeInferencer\.Resolve\s*\(")]
    private static partial Regex ExpressionTypeInferencerResolveCall();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string CoreSourceRoot() => Path.Combine(RepoRoot(), "src", "SilentScan.Core");

    [Fact]
    public void OnlyTheSanctionedResolversCallExpressionTypeInferencerDirectly()
    {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(CoreSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var relativeToCoreRoot = Path.GetRelativePath(CoreSourceRoot(), file);
            if (AllowedExpressionTypeInferencerCallers.Contains(relativeToCoreRoot))
            {
                continue;
            }

            var text = BlockComment().Replace(File.ReadAllText(file), string.Empty);
            text = LineComment().Replace(text, string.Empty);

            if (ExpressionTypeInferencerResolveCall().IsMatch(text))
            {
                violations.Add(relativeToCoreRoot);
            }
        }

        Assert.True(violations.Count == 0,
            "A new caller wires ExpressionTypeInferencer.Resolve directly instead of going through " +
            "ScalarExpressionResolver.ResolveScalarType (or WriteLossClassifier/NumericFamilyNarrowing for " +
            "narrowing checks) - this is exactly the hand-rolled resolveLeaf pattern that caused repeated " +
            "'generalize and share' fixes:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void ScopedSqlVisitorBaseIsTheOnlySourceOfScopeAwareColumnResolution()
    {
        var assembly = typeof(SilentScan.Core.Reporting.ScanReport).Assembly;
        var baseType = assembly.GetType("SilentScan.Core.Predicates.ScopedSqlVisitorBase")
            ?? throw new InvalidOperationException("SilentScan.Core.Predicates.ScopedSqlVisitorBase was not found - update this test if it was renamed or moved.");
        var moduleWalkerType = assembly.GetType("SilentScan.Core.Predicates.ModuleWalker")
            ?? throw new InvalidOperationException("SilentScan.Core.Predicates.ModuleWalker was not found - update this test if it was renamed or moved.");

        const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var guardedMethodNames = new HashSet<string> { "ResolveColumnFacts", "CurrentResolutionContext", "InspectJoinOnClauses" };
        var sanctionedTypes = new HashSet<Type> { baseType, moduleWalkerType };

        var offenders = assembly.GetTypes()
            .Where(t => !sanctionedTypes.Contains(t))
            .SelectMany(t => t.GetMethods(AllDeclared).Select(m => (Type: t, Method: m)))
            .Where(tm => guardedMethodNames.Contains(tm.Method.Name))
            .Select(tm => $"{tm.Type.FullName}.{tm.Method.Name}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A second implementation of scope-aware column/CTE resolution has appeared outside " +
            "ScopedSqlVisitorBase/ModuleWalker - this is the PredicateVisitorSupport-shaped duplication " +
            "that Phase 1 removed; scanners must consume ScopedSqlVisitorBase (not yet migrated) or " +
            "ModuleWalker (migrated) rather than reintroducing a parallel helper:\n" + string.Join('\n', offenders));
    }
}
