using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.TypeInference;

public static class LiteralTypeResolver
{
    public static SqlType? Resolve(Literal literal) => literal switch
    {
        StringLiteral { IsNational: true } s => new SqlType(SqlTypeCategory.NVarChar, Length: EmptyStringAwareLength(s.Value), Collation: ExplicitCollation(s)),

        StringLiteral s => new SqlType(SqlTypeCategory.VarChar, Length: EmptyStringAwareLength(s.Value), Collation: ExplicitCollation(s)),

        IntegerLiteral => new SqlType(SqlTypeCategory.Int),

        NumericLiteral n => ResolveNumeric(n),

        RealLiteral => new SqlType(SqlTypeCategory.Float, Precision: 53),

        MoneyLiteral => new SqlType(SqlTypeCategory.Money),

        BinaryLiteral b => new SqlType(SqlTypeCategory.Binary, Length: (b.Value.Length - 2) / 2),

        _ => null,
    };

    private static int EmptyStringAwareLength(string value) => value.Length == 0 ? 1 : value.Length;

    private static Collation? ExplicitCollation(Literal literal) =>
        literal.Collation is { Value: { } name } ? new Collation(name) : null;

    private static SqlType ResolveNumeric(NumericLiteral literal)
    {
        var value = literal.Value;
        var dotIndex = value.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex < 0)
        {
            return new SqlType(SqlTypeCategory.Decimal, Precision: value.Length, Scale: 0);
        }

        var integerDigits = dotIndex;
        var fractionalDigits = value.Length - dotIndex - 1;
        return new SqlType(SqlTypeCategory.Decimal, Precision: integerDigits + fractionalDigits, Scale: fractionalDigits);
    }
}
