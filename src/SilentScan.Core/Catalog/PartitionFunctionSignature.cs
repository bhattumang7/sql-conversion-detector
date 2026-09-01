namespace SilentScan.Core.Catalog;

public sealed record PartitionFunctionSignature(
    string FunctionName,
    bool IsRangeRight,
    string ParameterTypeName,
    IReadOnlyList<string> BoundaryValues)
{
    public bool IsEquivalentTo(PartitionFunctionSignature other, StringComparer identifierComparer) =>
        IsRangeRight == other.IsRangeRight
        && identifierComparer.Equals(ParameterTypeName, other.ParameterTypeName)
        && BoundaryValues.SequenceEqual(other.BoundaryValues, StringComparer.Ordinal);
}
