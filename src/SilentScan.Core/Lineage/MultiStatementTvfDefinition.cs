using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

public sealed record MultiStatementTvfDefinition(
    string QualifiedName,
    IReadOnlyList<CatalogColumn> Columns,
    string SourcePath,
    int SourceLine);
