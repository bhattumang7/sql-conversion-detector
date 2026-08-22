using System.Text.RegularExpressions;

namespace SilentScan.Tests.Architecture;

/// <summary>
/// Enforces the CLAUDE.md pass order (Parsing -> Catalog -> Lineage -> Predicates/Rules ->
/// Reporting): an earlier pass's source may never name a later pass's namespace, because that is
/// exactly the shape of bug the order exists to rule out (e.g. Catalog resolving through
/// Lineage's view logic before the catalog it depends on is even complete). TypeInference,
/// Common, Diagnostics and Corpus sit outside the chain - shared plumbing any pass may call, the
/// same way every pass already calls Diagnostics - so they are excluded from both directions of
/// the check. This scans namespace usage (a later pass's folder name written as
/// <c>SilentScan.Core.&lt;Folder&gt;</c> or as the <c>&lt;Folder&gt;.</c> qualifier the enclosing-
/// namespace lookup this codebase relies on allows) rather than enumerating type names, so it
/// cannot be defeated by two unrelated passes happening to reuse the same identifier (this
/// codebase's per-rule finding kinds and their matching RuleDocs content classes do exactly
/// that).
///
/// One named exception: <see cref="AllowedForwardReferences"/>. Catalog's file-mode temp-table
/// discovery (<c>DynamicSqlTempTableDiscovery</c>) genuinely needs to look inside dynamic SQL to
/// find a <c>CREATE TABLE #x</c> hidden in an EXEC/sp_executesql body - that's structurally a
/// Predicates-stage capability (<c>DynamicSqlScannerV2</c>), but Catalog needs the temp table
/// registered before Predicates ever runs, so there is no later stage to defer to. This is not a
/// bug the pass order is supposed to catch (Catalog isn't reaching into Predicates' own
/// verdict/finding logic, only its dynamic-SQL folding); it is named here instead of silently
/// widening the folder-level check, so a NEW, unrelated Catalog-to-Predicates reference still
/// fails the test.
/// </summary>
public sealed partial class PassOrderTests
{
    private static readonly Dictionary<string, int> StageIndexByFolder = new()
    {
        ["Parsing"] = 0,
        ["Catalog"] = 1,
        ["Lineage"] = 2,
        ["Predicates"] = 3,
        ["Rules"] = 3,
        ["Reporting"] = 4,
    };

    /// <summary>(File, later-pass folder it's allowed to name) pairs - see the class remarks for why each one exists. Keep this list to genuinely structural exceptions, never a convenience.</summary>
    private static readonly (string File, string LaterFolder)[] AllowedForwardReferences =
    [
        (Path.Combine("Catalog", "DynamicSqlTempTableDiscovery.cs"), "Predicates"),
    ];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

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
    public void EarlierPassNeverNamesALaterPassNamespace()
    {
        var violations = new List<string>();

        foreach (var (folder, stageIndex) in StageIndexByFolder)
        {
            var folderPath = Path.Combine(CoreSourceRoot(), folder);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            var laterFolders = StageIndexByFolder
                .Where(kv => kv.Value > stageIndex)
                .Select(kv => kv.Key)
                .Distinct()
                .ToList();

            if (laterFolders.Count == 0)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories))
            {
                var text = BlockComment().Replace(File.ReadAllText(file), string.Empty);
                text = LineComment().Replace(text, string.Empty);
                var relativeToCoreRoot = Path.GetRelativePath(CoreSourceRoot(), file);

                foreach (var laterFolder in laterFolders)
                {
                    if (AllowedForwardReferences.Any(a => a.File == relativeToCoreRoot && a.LaterFolder == laterFolder))
                    {
                        continue;
                    }

                    // Either a "using SilentScan.Core.<laterFolder>[.Sub];" import, or the bare
                    // "<laterFolder>.Type" qualifier this codebase's sibling-namespace lookup allows.
                    var pattern = new Regex(@"\bSilentScan\.Core\." + laterFolder + @"\b|\b" + laterFolder + @"\.[A-Za-z_]", RegexOptions.Compiled);
                    if (pattern.IsMatch(text))
                    {
                        violations.Add($"{Path.GetRelativePath(RepoRoot(), file)} ({folder}, pass {stageIndex}) names the '{laterFolder}' namespace (pass {StageIndexByFolder[laterFolder]})");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Pass order violated - an earlier pass's source names a later pass's namespace:\n" + string.Join('\n', violations));
    }
}
