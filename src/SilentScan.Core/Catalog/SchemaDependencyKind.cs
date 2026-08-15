namespace SilentScan.Core.Catalog;

/// <summary>Which schema-level construct a <see cref="Predicates.ScalarUdfFindingKind.SchemaDependency"/> finding came from. Null on every other finding kind.</summary>
public enum SchemaDependencyKind
{
    ComputedColumn,
    DefaultConstraint,
    CheckConstraint,
}
