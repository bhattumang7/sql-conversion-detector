using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public enum IndexDesignFindingKind
{
    HeapWithNonclusteredIndexes,

    HeapWithNonclusteredPrimaryKey,

    NonUniqueClusteredIndex,

    WideClusteredKey,

    RandomClusteredKeyGuidDefault,

    DuplicateIndex,

    SubsumedIndex,

    UnindexedForeignKey,

    DisabledIndex,

    HypotheticalIndex,

    ManyNonclusteredIndexes,

    ManyKeyColumnsIndex,

    WideTable,

    HighNullableColumnRatio,

    HighStringColumnRatio,

    FilterColumnNotInIndex,

    DeprecatedLobColumnType,

    TimestampColumnNaming,

    FloatOrRealIndexKeyColumn,

    NoRecomputeStatistics,

    VariableLengthKeyColumnExceedsKeyLimit,

    MergeableIndexesDifferingIncludeOnly,

    ColumnstoreIndexOnDmlTargetTable,

    MonotonicClusteredKeyMissingSequentialOptimization,

    NonAlignedPartitionedIndex,
}

public sealed record IndexDesignFinding(
    IndexDesignFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    string DetailText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

