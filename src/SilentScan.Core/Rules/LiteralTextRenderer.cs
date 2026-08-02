using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Rules;

/// <summary>
/// Renders a <see cref="Literal"/> back to valid, re-parseable T-SQL text - used to reconstruct
/// a literal-operand predicate faithfully for oracle probing (docs/audit-remediation-plan.md
/// Phase 5.2, audit finding C2) instead of substituting a typed variable, which the optimizer
/// can constant-fold differently than a literal (confirmed against the real engine: a bare
/// string literal like N'x' types as nvarchar(8000) rather than the parameterized probe's
/// content-length nvarchar(n), a real fidelity gap this exists to close). Covers exactly the
/// literal kinds <see cref="LiteralTypeResolver.Resolve"/> types - kept in sync deliberately, so
/// "the type resolved" and "the text rendered" can never diverge for a supported literal kind.
/// </summary>
public static class LiteralTextRenderer
{
    public static string? Render(Literal literal) => literal switch
    {
        StringLiteral { IsNational: true } s => $"N'{Escape(s.Value)}'{CollateSuffix(literal)}",
        StringLiteral s => $"'{Escape(s.Value)}'{CollateSuffix(literal)}",

        // These kinds' Value is the original token text verbatim (already valid SQL syntax,
        // including any prefix like BinaryLiteral's "0x" or MoneyLiteral's "$") - safe to reuse
        // directly, unlike StringLiteral whose Value has already had its quoting stripped.
        IntegerLiteral i => i.Value,
        NumericLiteral n => n.Value,
        RealLiteral r => r.Value,
        MoneyLiteral m => m.Value,
        BinaryLiteral b => b.Value,

        _ => null,
    };

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    // `'x' COLLATE X` carries its COLLATE clause on the Literal node itself (ScriptDOM's
    // Literal.Collation), not a separate wrapper expression - LiteralTypeResolver already reads
    // this to give the literal its explicit collation (VerdictClassifier's Rule 2: an explicit
    // COLLATE outranks everything and forces the COLUMN to convert). Found while oracle-
    // confirming ExplicitCollatePipelineTests: omitting this suffix here silently reconstructed
    // a probe with no COLLATE clause at all - not equivalent to the source predicate, and the
    // one case this renderer exists specifically to avoid (a probe that misrepresents fidelity).
    // Only string literals carry a COLLATE clause in T-SQL; other literal kinds have no such
    // syntax, so this is applied to the two StringLiteral arms only.
    private static string CollateSuffix(Literal literal) =>
        literal.Collation is { Value: { } name } ? $" COLLATE {name}" : string.Empty;
}
