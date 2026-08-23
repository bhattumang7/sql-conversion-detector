namespace SilentScan.Core.Predicates.DynamicSqlValue;

public interface ILiveRowValueFetcher
{
    IReadOnlyList<string>? TryFetchDistinctValues(
        string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows);
}
