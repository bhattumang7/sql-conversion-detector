using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ForcedParameterizationFindingKind
{
    LikePatternLiteral,

    TopOrPagingLiteral,

    SelectListLiteral,

    HavingLiteral,

    OrderByExpressionLiteral,

    DoubleColonCallArgumentLiteral,

    TableSampleSizeLiteral,

    DmlOutputListLiteral,

    ConvertStyleCodeLiteral,

    CheckSumArgumentLiteral,

    ConstantFoldableExpressionLiteral,

    GroupByExpressionLiteral,
}

public sealed record ForcedParameterizationFinding(
    ForcedParameterizationFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
