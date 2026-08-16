using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds" - two independent <c>sys.sql_modules</c>
/// catalog flags, same shape as <see cref="SetOptionScanner"/>'s own module-level (catalog-flag)
/// half: no AST walk, no precision guard needed (the flag alone is the whole fact), <see
/// cref="ModuleCompileFlagFinding.Line"/>/<see cref="ModuleCompileFlagFinding.Column"/> point at
/// the module's own CREATE/ALTER statement. Live-mode only - <see
/// cref="DatabaseCatalog.TryGetModuleIsRecompiled"/>/<see
/// cref="DatabaseCatalog.TryGetModuleUsesDatabaseCollation"/> are populated only by
/// <c>LiveScanRunner</c>, so a file-mode scan never produces a finding here, exactly like <see
/// cref="SetOptionScanner"/>.
/// </summary>
public static class ModuleCompileFlagScanner
{
    public static IReadOnlyList<ModuleCompileFlagFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var moduleQualifiedName = parseResult.SourcePath;
        var findings = new List<ModuleCompileFlagFinding>();

        if (catalog.TryGetModuleIsRecompiled(moduleQualifiedName, out var isRecompiled) && isRecompiled)
        {
            findings.Add(new ModuleCompileFlagFinding(
                ModuleCompileFlagFindingKind.RecompilesEveryCall, moduleQualifiedName, parseResult.SourcePath,
                parseResult.Fragment.StartLine, parseResult.Fragment.StartColumn));
        }

        var schemaBound = catalog.TryGetModuleIsSchemaBound(moduleQualifiedName, out var isSchemaBound) && isSchemaBound;

        if (!schemaBound
            && catalog.TryGetModuleUsesDatabaseCollation(moduleQualifiedName, out var usesDatabaseCollation)
            && usesDatabaseCollation)
        {
            findings.Add(new ModuleCompileFlagFinding(
                ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation, moduleQualifiedName, parseResult.SourcePath,
                parseResult.Fragment.StartLine, parseResult.Fragment.StartColumn));
        }

        return findings;
    }
}
