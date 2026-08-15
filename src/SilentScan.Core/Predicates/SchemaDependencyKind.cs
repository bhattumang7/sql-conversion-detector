namespace SilentScan.Core.Predicates;

/// <summary>Which schema-level construct a <see cref="ScalarUdfFindingKind.SchemaDependency"/> finding came from. Null on every other finding kind.</summary>
public enum SchemaDependencyKind
{
    ComputedColumn,
    DefaultConstraint,
    CheckConstraint,
}
