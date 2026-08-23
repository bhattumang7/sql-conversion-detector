namespace SilentScan.Core.Catalog;

public enum ReferentialAction
{
    NoAction = 0,
    Cascade = 1,
    SetNull = 2,
    SetDefault = 3,
}

public sealed record ForeignKeyRelationship(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ParentColumnName,
    string ReferencedTableQualifiedName,
    string ReferencedColumnName,
    bool IsNotTrusted = false,
    bool IsDisabled = false,
    ReferentialAction DeleteAction = ReferentialAction.NoAction,
    ReferentialAction UpdateAction = ReferentialAction.NoAction);
