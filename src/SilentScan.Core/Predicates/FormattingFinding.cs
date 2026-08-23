using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum FormattingFindingKind
{
TabCharacterUsed,

MultipleStatementsOnSameLine,

MultipleDeclarationsOnSameLine,

MissingBeginEndBlock,

SingleLineConditionalBody,

DanglingStatementAfterUnbracedBody,

IfImmediatelyFollowingPriorBlockEnd,

RedundantParentheses,

MissingFileHeaderComment,
}

public sealed record FormattingFinding(
    FormattingFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

