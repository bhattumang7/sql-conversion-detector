using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness;

public sealed record RuleContext(
    DatabaseCatalog Catalog,
    LineageCatalog Lineage,
    SkipLedger Ledger,
    ProcCallGraph ProcCallGraph,
    IReadOnlyDictionary<string, TvfFenceOrigin> TvfFenceMap,
    IReadOnlyDictionary<string, ScalarUdfOrigin> ScalarUdfMap,
    IReadOnlyDictionary<string, ViewExpansionOrigin> ViewExpansionMap,
    IReadOnlyList<ViewDefinition> ViewDefinitions,
    IReadOnlyDictionary<string, SelectStarViewCandidate> SelectStarViewCandidates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CallerScopeByCalleeScope);
