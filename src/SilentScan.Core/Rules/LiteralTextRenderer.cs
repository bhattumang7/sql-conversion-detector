using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class LiteralTextRenderer
{
    public static string? Render(Literal literal) => literal switch
    {
        StringLiteral { IsNational: true } s => $"N'{Escape(s.Value)}'{CollateSuffix(literal)}",
        StringLiteral s => $"'{Escape(s.Value)}'{CollateSuffix(literal)}",

        IntegerLiteral i => i.Value,
        NumericLiteral n => n.Value,
        RealLiteral r => r.Value,
        MoneyLiteral m => m.Value,
        BinaryLiteral b => b.Value,

        _ => null,
    };

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CollateSuffix(Literal literal) =>
        literal.Collation is { Value: { } name } ? $" COLLATE {name}" : string.Empty;
}
