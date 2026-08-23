using System.Text.RegularExpressions;

namespace SilentScan.Tests.Architecture;

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
