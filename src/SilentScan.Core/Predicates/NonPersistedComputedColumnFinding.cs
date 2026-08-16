namespace SilentScan.Core.Predicates;

/// <summary>
/// A computed column with <c>is_persisted = 0</c> (docs/detection-checklist.md "Schema-scan UDF
/// and computed-column findings" #2) - recomputed from <see cref="DefinitionText"/> on every read
/// that touches it, independent of whether that definition calls a UDF at all (the already-shipped
/// <see cref="ScalarUdfFinding"/> schema-dependency half only fires when a scalar UDF is actually
/// referenced; a non-persisted computed column made purely of arithmetic/string built-ins still
/// pays the per-row recompute cost, just without the additional per-row-call/serial-plan penalty a
/// UDF reference would add). Catalog-only structural fact - <c>sys.computed_columns.is_persisted</c>
/// in live mode, the column definition's own <c>PERSISTED</c> keyword in file mode
/// (<see cref="Catalog.CatalogColumn.IsPersisted"/>, already read by both paths for an unrelated
/// consumer before this stream existed) - no AST walk of query sites needed, matching
/// <see cref="MaxTypedColumnScanner"/>'s own "one structural fact per column" shape. Never fires on
/// a column whose <c>PERSISTED</c> keyword is present, regardless of whether it's also indexed - an
/// indexed persisted computed column has already paid its recompute cost once, at write time.
/// </summary>
public sealed record NonPersistedComputedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefinitionText,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
