using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// T-SQL literal typing rules (CLAUDE.md): N'x' = nvarchar, 'x' = varchar, integer literal =
/// int, 1.5 = numeric(p,s), date literals stay strings (varchar) until compared.
/// </summary>
public static class LiteralTypeResolver
{
    public static SqlType? Resolve(Literal literal) => literal switch
    {
        StringLiteral { IsNational: true } s => new SqlType(SqlTypeCategory.NVarChar, Length: EmptyStringAwareLength(s.Value), Collation: ExplicitCollation(s)),

        // Date/time literals are untyped strings until compared against a typed column -
        // that comparison-time typing is a Pass 3 concern, not this pass's.
        StringLiteral s => new SqlType(SqlTypeCategory.VarChar, Length: EmptyStringAwareLength(s.Value), Collation: ExplicitCollation(s)),

        IntegerLiteral => new SqlType(SqlTypeCategory.Int),

        NumericLiteral n => ResolveNumeric(n),

        // Scientific notation ("1.5e10") always types as float in T-SQL, never real or decimal -
        // oracle-verified (sys.dm_exec_describe_first_result_set: float(53), docs/audit-
        // remediation-plan.md Phase 5.3, audit finding C4).
        RealLiteral => new SqlType(SqlTypeCategory.Float, Precision: 53),

        MoneyLiteral => new SqlType(SqlTypeCategory.Money),

        // Value includes the "0x" prefix (e.g. "0x1A2B"); two hex digits per byte.
        BinaryLiteral b => new SqlType(SqlTypeCategory.Binary, Length: (b.Value.Length - 2) / 2),

        _ => null,
    };

    // Oracle-verified (sys.dm_exec_describe_first_result_set): an empty string literal ('' or
    // N'') types with length 1, not 0 - a zero-length varchar/nvarchar isn't a real T-SQL type
    // (docs/audit-remediation-plan.md Phase 5.3, audit finding C4).
    private static int EmptyStringAwareLength(string value) => value.Length == 0 ? 1 : value.Length;

    // `'x' COLLATE X` gives the literal an explicit collation that outranks every other source
    // (an explicit COLLATE clause has the highest coercibility precedence in T-SQL) - oracle-
    // verified directly: WHERE VarcharCol = 'x' COLLATE <different collation> puts
    // CONVERT_IMPLICIT on the COLUMN, not the literal, even though nothing about the column's
    // own syntax changed. A plain literal with no COLLATE clause carries no collation of its
    // own here (null) - it is "coercible default" and always yields to whatever the other side
    // needs, never forcing a conversion by itself.
    private static Collation? ExplicitCollation(Literal literal) =>
        literal.Collation is { Value: { } name } ? new Collation(name) : null;

    private static SqlType ResolveNumeric(NumericLiteral literal)
    {
        var value = literal.Value;
        var dotIndex = value.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex < 0)
        {
            // An integer-valued literal beyond int range (ScriptDOM itself reclassifies these
            // from IntegerLiteral to NumericLiteral the moment they exceed int range - see
            // LiteralTypeResolverTests) already lands here as decimal(precision,0), matching the
            // real engine (oracle-verified via sys.dm_exec_describe_first_result_set: neither
            // 2147483648 nor bigint.MaxValue promote to bigint - both type as decimal/numeric,
            // contrary to the commonly-cited "int -> bigint -> decimal" precedence folklore).
            // docs/audit-remediation-plan.md Phase 5.3, audit finding C4 described this as
            // needing a bigint-promotion fix; that claim did not survive checking against the
            // real engine, so this branch is intentionally unchanged.
            return new SqlType(SqlTypeCategory.Decimal, Precision: value.Length, Scale: 0);
        }

        var integerDigits = dotIndex;
        var fractionalDigits = value.Length - dotIndex - 1;
        return new SqlType(SqlTypeCategory.Decimal, Precision: integerDigits + fractionalDigits, Scale: fractionalDigits);
    }
}
