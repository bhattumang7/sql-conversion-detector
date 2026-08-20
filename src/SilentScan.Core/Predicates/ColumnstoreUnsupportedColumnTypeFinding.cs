using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A SQL_VARIANT column participates in a columnstore index on the same table - catalog-only
/// structural fact. Oracle-confirmed (real DDL execution): CREATE CLUSTERED COLUMNSTORE INDEX
/// covers every column on the table with no explicit list, and fails outright the moment any one
/// of them is SQL_VARIANT ("Msg 35343: The statement failed. Column '...' has a data type that
/// cannot participate in a columnstore index") - the same failure reproduces for ALTER TABLE ...
/// ADD (a SQL_VARIANT column) against a table that already carries a clustered columnstore index,
/// and for CREATE NONCLUSTERED COLUMNSTORE INDEX when its own explicit column list names a
/// SQL_VARIANT column (a nonclustered columnstore index that simply omits the SQL_VARIANT column
/// from its list succeeds - not flagged, since the column never participates in the index at
/// all). This is not a silent perf regression; it is a script that will not deploy. Reported once
/// per (table, column, index) triple.
/// </summary>
public sealed record ColumnstoreUnsupportedColumnTypeFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    string IndexName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
