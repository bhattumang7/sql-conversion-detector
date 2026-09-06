using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class ColumnCollationDriftRule : ICatalogRule
{
    public string Id => "ColumnCollationDriftScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => ColumnCollationDriftScanner.Scan(context.Catalog);
}

internal sealed class AnsiPaddingOffColumnRule : ICatalogRule
{
    public string Id => "AnsiPaddingOffColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => AnsiPaddingOffColumnScanner.Scan(context.Catalog);
}

internal sealed class CrossTableTypeDriftRule : ICatalogRule
{
    public string Id => "CrossTableTypeDriftScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => CrossTableTypeDriftScanner.Scan(context.Catalog);
}

internal sealed class TriggerOrderRule : ICatalogRule
{
    public string Id => "TriggerOrderScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => TriggerOrderScanner.Scan(context.Catalog);
}

internal sealed class ProcCallArgumentMismatchRule : ICatalogRule
{
    public string Id => "ProcCallArgumentMismatchScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => ProcCallArgumentMismatchScanner.Scan(context.ProcCallGraph);
}

internal sealed class SpExecuteSqlParameterMismatchRule : ICatalogRule
{
    public string Id => "SpExecuteSqlParameterMismatchScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => SpExecuteSqlParameterMismatchScanner.Scan(context.ProcCallGraph);
}

internal sealed class MaxTypedColumnRule : ICatalogRule
{
    public string Id => "MaxTypedColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MaxTypedColumnScanner.Scan(context.Catalog);
}

internal sealed class ColumnstoreUnsupportedColumnTypeRule : ICatalogRule
{
    public string Id => "ColumnstoreUnsupportedColumnTypeScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => ColumnstoreUnsupportedColumnTypeScanner.Scan(context.Catalog);
}

internal sealed class SelectiveXmlIndexValueColumnRule : ICatalogRule
{
    public string Id => "SelectiveXmlIndexValueColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => SelectiveXmlIndexValueColumnScanner.Scan(context.Catalog);
}

internal sealed class MemoryOptimizedUnsupportedColumnTypeRule : ICatalogRule
{
    public string Id => "MemoryOptimizedUnsupportedColumnTypeScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MemoryOptimizedUnsupportedColumnTypeScanner.Scan(context.Catalog);
}

internal sealed class MemoryOptimizedUtf8CollationRule : ICatalogRule
{
    public string Id => "MemoryOptimizedUtf8CollationScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MemoryOptimizedUtf8CollationScanner.Scan(context.Catalog);
}

internal sealed class MemoryOptimizedUnsupportedIndexOptionRule : ICatalogRule
{
    public string Id => "MemoryOptimizedUnsupportedIndexOptionScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MemoryOptimizedUnsupportedIndexOptionScanner.Scan(context.Catalog);
}

internal sealed class MemoryOptimizedSchemaOnlyDurabilityRule : ICatalogRule
{
    public string Id => "MemoryOptimizedSchemaOnlyDurabilityScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MemoryOptimizedSchemaOnlyDurabilityScanner.Scan(context.Catalog);
}

internal sealed class AlwaysEncryptedKeyColumnRule : ICatalogRule
{
    public string Id => "AlwaysEncryptedKeyColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => AlwaysEncryptedKeyColumnScanner.Scan(context.Catalog);
}

internal sealed class AlwaysEncryptedUnsupportedColumnRule : ICatalogRule
{
    public string Id => "AlwaysEncryptedUnsupportedColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => AlwaysEncryptedUnsupportedColumnScanner.Scan(context.Catalog);
}

internal sealed class AlterColumnSafetyRule : ICatalogRule
{
    public string Id => "AlterColumnSafetyScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => AlterColumnSafetyScanner.Scan(context.Catalog);
}

internal sealed class DropProtectedObjectRule : ICatalogRule
{
    public string Id => "DropProtectedObjectScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => DropProtectedObjectScanner.Scan(context.Catalog);
}

internal sealed class OnlineRebuildLegacyLobRule : ICatalogRule
{
    public string Id => "OnlineRebuildLegacyLobScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => OnlineRebuildLegacyLobScanner.Scan(context.Catalog);
}

internal sealed class SparseColumnDisallowedTypeRule : ICatalogRule
{
    public string Id => "SparseColumnDisallowedTypeScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => SparseColumnDisallowedTypeScanner.Scan(context.Catalog);
}

internal sealed class LegacyLobUtf8CollationRule : ICatalogRule
{
    public string Id => "LegacyLobUtf8CollationScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => LegacyLobUtf8CollationScanner.Scan(context.Catalog);
}

internal sealed class FullTextIndexDdlRule : ICatalogRule
{
    public string Id => "FullTextIndexDdlScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => FullTextIndexDdlScanner.Scan(context.Catalog);
}

internal sealed class MemoryOptimizedForeignKeyRule : ICatalogRule
{
    public string Id => "MemoryOptimizedForeignKeyScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => MemoryOptimizedForeignKeyScanner.Scan(context.Catalog);
}

internal sealed class NonPersistedComputedColumnRule : ICatalogRule
{
    public string Id => "NonPersistedComputedColumnScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => NonPersistedComputedColumnScanner.Scan(context.Catalog);
}

internal sealed class ComputedColumnIndexKeyRule : ICatalogRule
{
    public string Id => "ComputedColumnIndexKeyScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => ComputedColumnIndexKeyScanner.Scan(context.Catalog);
}

internal sealed class UntrustedConstraintRule : ICatalogRule
{
    public string Id => "UntrustedConstraintScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => UntrustedConstraintScanner.Scan(context.Catalog);
}

internal sealed class CheckConstraintRule : ICatalogRule
{
    public string Id => "CheckConstraintScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => CheckConstraintScanner.Scan(context.Catalog);
}

internal sealed class SecurityPredicateIndexRule : ICatalogRule
{
    public string Id => "SecurityPredicateIndexScanner";
    public bool ApplyConfidenceFilter => false;
    public IReadOnlyList<IFinding> Scan(RuleContext context) => SecurityPredicateIndexScanner.Scan(context.Catalog);
}

internal sealed class DefaultNullableConstraintRule : ICatalogRule
{
    public string Id => "DefaultNullableConstraintScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => DefaultNullableConstraintScanner.Scan(context.Catalog);
}

internal sealed class CascadingForeignKeyRule : ICatalogRule
{
    public string Id => "CascadingForeignKeyScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => CascadingForeignKeyScanner.Scan(context.Catalog);
}

internal sealed class TemporalTableHistoryIndexGapRule : ICatalogRule
{
    public string Id => "TemporalTableHistoryIndexGapScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => TemporalTableHistoryIndexGapScanner.Scan(context.Catalog);
}

internal sealed class NestedViewDepthRule : ICatalogRule
{
    public string Id => "NestedViewDepthScanner";
    public IReadOnlyList<IFinding> Scan(RuleContext context) => NestedViewDepthScanner.Scan(context.ViewExpansionMap, context.ViewDefinitions, context.Catalog);
}
