namespace SilentScan.Core.Predicates;

/// <summary>
/// How much this finding's own claim can be trusted. A statically-derived finding is always
/// <see cref="High"/> - it rests on real source text, not an assumption. A finding derived from
/// a dynamic SQL fold that had to assume something (a symbolic placeholder standing in for a
/// value this scanner could not prove constant) is <see cref="Medium"/> or lower, and is excluded
/// from a report unless the caller opts in - CLAUDE.md's "one false positive in the published
/// study is worse than ten missed true positives" applies to every consumer of this field, not
/// just the study. Ordered so the numeric value doubles as a worst/best comparison
/// (<c>Math.Max</c>/<c>Math.Min</c> across the enum's underlying int), and so <c>default</c> is
/// <see cref="High"/> - every existing finding record defaults to this value precisely so adding
/// it changes no behavior for a caller that hasn't opted into anything.
/// </summary>
public enum FindingConfidence
{
    High = 0,
    Medium = 1,
    Low = 2,
}
