using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public enum AlterColumnSafetyKind
{
    PrecisionOrScaleNarrowing,
    IncompatibleFamilyConversion,
}

public sealed record AlterColumnSafetyFinding(
    string TableQualifiedName,
    string ColumnName,
    AlterColumnSafetyKind Kind,
    SqlType PreviousType,
    SqlType NewType,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
