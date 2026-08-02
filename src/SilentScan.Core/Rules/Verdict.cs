namespace SilentScan.Core.Rules;

/// <summary>
/// Pass 4 verdict (CLAUDE.md). This classifier implements SeekPreserved/RangeSeek/ScanForced/
/// Unknown/OperandClash - the type-precedence + collation driven outcomes. One verdict from
/// CLAUDE.md's full vocabulary is deliberately NOT produced here: NotSargableFunction is
/// Tier-1's concern (<see cref="Predicates.NonSargablePredicateScanner"/>, a purely syntactic
/// check reported as its own finding stream).
/// </summary>
public enum Verdict
{
    SeekPreserved,
    RangeSeek,
    ScanForced,
    Unknown,

    /// <summary>
    /// Roadmap Phase A3: the oracle-probed type matrix cell for this exact category pair is
    /// <c>CompileFailed</c> - a definitive, empirically-confirmed fact that the comparison does
    /// not compile at all (e.g. TIME vs a date-family type, or UNIQUEIDENTIFIER vs a string
    /// family), not merely an absence of probe data. Distinct from <see cref="Unknown"/>, which
    /// means "no answer" - this means "the answer is: it cannot run as written."
    /// </summary>
    OperandClash,
}
