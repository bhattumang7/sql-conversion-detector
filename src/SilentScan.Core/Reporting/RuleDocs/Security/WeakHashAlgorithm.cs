using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class WeakHashAlgorithm
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.WeakHashAlgorithm);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's `HASHBYTES` function accepts a fixed set of algorithm names, and several of
            them - MD2, MD4, MD5, SHA, and SHA1 - are cryptographically broken or deprecated; NIST
            and OWASP both independently publish this exact guidance. This is a general-use flag: it
            fires on every `HASHBYTES` call naming one of those algorithms regardless of context,
            since using a weak hash for a non-security purpose (a checksum, a dedup key, a
            change-detection fingerprint) isn't itself wrong - the algorithm's collision resistance
            doesn't matter for those uses the way it does for anything security-sensitive.

            This is the general-purpose sibling of `weak-hash-algorithm-sensitive-context`, which
            fires the SAME algorithm choice at higher confidence specifically when the context looks
            security-relevant (hashing a credential-named value, or comparing the hash directly in a
            predicate). Reported at High confidence here - a HASHBYTES call naming a weak algorithm
            is an unambiguous fact once matched, purely informational about the algorithm choice
            itself.
            """,
        HowToFixIt: """
            Use HASHBYTES with SHA2_256 or SHA2_512 instead of a broken/deprecated algorithm.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "HASHBYTES with MD5 for a general-purpose checksum",
                NoncompliantSql: """
                    SELECT HASHBYTES('MD5', Payload) AS Checksum
                    FROM dbo.T;
                    """,
                NoncompliantExplanation: "MD5 is cryptographically broken - even for a non-security checksum use, SHA2_256 costs nothing extra here and avoids naming a deprecated algorithm in the codebase at all.",
                CompliantSql: """
                    SELECT HASHBYTES('SHA2_256', Payload) AS Checksum
                    FROM dbo.T;
                    """,
                CompliantExplanation: "SHA2_256 is not deprecated and carries no algorithm-choice risk, whatever the hash is later used for."),
        ]);
}
