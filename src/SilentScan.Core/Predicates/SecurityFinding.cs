using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SecurityFindingKind
{
    /// <summary>A local variable or parameter whose own name suggests it holds a credential
    /// (matches a small, independently-chosen word list: password/passwd/pwd/secret) is assigned a
    /// literal string value directly in the module's own source text - the value belongs in a
    /// secrets store or external configuration, never embedded in a script where it survives in
    /// source control, backups, and every copy of the module text this tool itself reads.</summary>
    HardCodedCredential,

    /// <summary>A string literal contains an IPv4-shaped address (four dot-separated octets, each
    /// 0-255) that is not one of the standard, publicly-reserved benign addresses (loopback
    /// 127.0.0.0/8, the all-zeros/all-ones addresses, or the IANA-documented TEST-NET-1/2/3
    /// documentation ranges, RFC 5737) - an environment-specific detail embedded in code, a
    /// deployment-coupling smell and, occasionally, a genuine hardcoded backdoor/debug
    /// endpoint.</summary>
    HardCodedIpAddress,

    /// <summary>A <c>HASHBYTES</c> call names a cryptographically broken/deprecated algorithm
    /// (MD2, MD4, MD5, SHA, SHA1 - NIST and OWASP both independently publish this exact guidance).
    /// General-use flag: fires on every such call regardless of context, since using a weak hash for
    /// a non-security purpose (a checksum, a dedup key) is not itself wrong - purely
    /// informational.</summary>
    WeakHashAlgorithm,

    /// <summary>The same weak-algorithm <c>HASHBYTES</c> call as <see cref="WeakHashAlgorithm"/>,
    /// but in a context this pass can recognize as security-sensitive: the value being hashed is
    /// itself a credential-suggestive-named variable/column, or the call appears as an operand of a
    /// direct comparison (a predicate - WHERE/JOIN ON/HAVING), suggesting an authentication-flavored
    /// equality check. A sharper, higher-confidence claim than the general-use sibling.</summary>
    WeakHashAlgorithmInSensitiveContext,

    /// <summary>An <c>EXEC(string)</c>/<c>EXEC(@sql)</c>/<c>sp_executesql</c> call site whose
    /// assembled SQL text this tool's own dynamic-SQL constant-folding pass could NOT prove is fully
    /// constant (<see cref="DynamicSqlOutcome.Unanalyzable"/> - it depends on a variable, parameter,
    /// or expression whose own value this pass never guesses at). Distinct from the already-shipped,
    /// PERFORMANCE-framed <see cref="UnparameterizedDynamicSqlFinding"/> stream, which only fires on
    /// the opposite case - a value this pass COULD prove constant but that was still spliced into
    /// the SQL text via concatenation instead of a real parameter. This kind is the SECURITY framing
    /// of the exact cases the performance stream declines to analyze further: "this call site's
    /// assembled text cannot be shown, from the code alone, to be free of runtime/external
    /// influence" - a real, actionable SQL-injection-surface claim distinct from the plan-cache
    /// concern the sibling stream makes. Never claims the text IS actually influenced by unsanitized
    /// external input (this pass cannot see as far as an application boundary) - only that it cannot
    /// be proven safe from the code alone.</summary>
    UnprovableDynamicSqlText,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Security" (dynamic code execution, hard-coded credentials,
/// hard-coded IP addresses, weak hash algorithms in general and in sensitive contexts) - resolved
/// by CLAUDE.md's scope rule: security/compliance rules are in scope on the same basis as every
/// other detectable-from-code-and-schema rule in this project.
///
/// One finding type, one <see cref="Kind"/> discriminator - this codebase's established
/// shared-plumbing shape. Four of the five kinds are pure AST/text-pattern checks needing no
/// catalog and no oracle (none makes a plan-shape or runtime-behavior claim - each is a structural
/// fact about the source text). <see cref="SecurityFindingKind.UnprovableDynamicSqlText"/> is
/// derived from the already-computed, already-oracle-backed dynamic-SQL pipeline's own
/// <see cref="DynamicSqlOutcome.Unanalyzable"/> classification rather than duplicating that
/// machinery.
///
/// Confidence: <see cref="FindingConfidence.High"/> for the structurally-unambiguous, hard-fact
/// kinds (<see cref="SecurityFindingKind.HardCodedIpAddress"/>, <see
/// cref="SecurityFindingKind.WeakHashAlgorithm"/> - a HASHBYTES call naming a weak algorithm is an
/// unambiguous fact once matched). <see cref="FindingConfidence.Medium"/> for the sharper,
/// context-dependent kinds (<see cref="SecurityFindingKind.WeakHashAlgorithmInSensitiveContext"/>,
/// <see cref="SecurityFindingKind.UnprovableDynamicSqlText"/> - each names a real, actionable risk
/// but not a provable vulnerability in isolation, since neither this pass nor its host tool ever
/// traces as far as an actual external-input boundary). <see cref="FindingConfidence.Low"/> for
/// <see cref="SecurityFindingKind.HardCodedCredential"/> specifically - name-based matching always
/// carries real false-positive risk (a variable named <c>@passwordHash</c> assigned a literal
/// display placeholder, for instance), so this is reported as a lead worth checking, not a
/// confirmed finding.
///
/// Version-insensitive: string-literal shape matching, HASHBYTES's own supported-algorithm list,
/// and the dynamic-SQL pipeline's constant-folding classification are all stable, long-standing
/// T-SQL/engine facts unaffected by compat level or CE mode.
/// </summary>
public sealed record SecurityFinding(
    SecurityFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

