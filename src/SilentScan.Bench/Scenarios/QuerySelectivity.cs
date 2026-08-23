namespace SilentScan.Bench.Scenarios;

public enum QuerySelectivity
{
SingleRow,

OnePercent,

TenPercent,
}

public static class QuerySelectivityExtensions
{
    public static double? Fraction(this QuerySelectivity selectivity) => selectivity switch
    {
        QuerySelectivity.OnePercent => 0.01,
        QuerySelectivity.TenPercent => 0.10,
        _ => null,
    };
}
