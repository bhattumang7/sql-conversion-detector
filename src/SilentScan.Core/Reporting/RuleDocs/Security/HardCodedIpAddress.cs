using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class HardCodedIpAddress
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.HardCodedIpAddress);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A string literal containing an IPv4-shaped address (four dot-separated octets, each
            0-255) embedded directly in T-SQL source - most often inside a connection string, a
            linked-server definition, or a firewall/allowlist check - couples the script to one
            specific environment's network topology. That coupling is a deployment smell on its own
            (the same script silently breaks, or silently points at the wrong server, the moment it
            runs somewhere the address doesn't apply) and, occasionally, a genuine hardcoded
            backdoor or debug endpoint left behind from development.

            This rule declines to fire on addresses that are provably benign rather than
            environment-specific: the loopback range (127.0.0.0/8), the all-zeros and all-ones
            addresses, and the IANA-documented TEST-NET-1/2/3 documentation ranges (RFC 5737, the
            192.0.2.x/198.51.100.x/203.0.113.x ranges reserved specifically for examples and
            documentation) - none of those names a real environment-specific host, so flagging them
            would be pure noise. A string that merely looks IP-shaped but has an octet over 255 is
            correctly never matched as an address at all.
            """,
        HowToFixIt: """
            Move the IP address into configuration instead of hard-coding it in source text.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A connection string with a hardcoded, real IPv4 address",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_GetLinkedConnectionString AS
                    BEGIN
                        SELECT 'Server=10.20.30.40;Port=1433' AS ConnStr;
                    END;
                    """,
                NoncompliantExplanation: "10.20.30.40 is a real, environment-specific address embedded in the script's own text - it silently breaks or points at the wrong server the moment this script runs against a different environment.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_GetLinkedConnectionString AS
                    BEGIN
                        SELECT CONVERT(VARCHAR(200), SERVERPROPERTY('MachineName')) + ';Port=1433' AS ConnStr;
                        -- or better: read the target host from an application configuration table/file.
                    END;
                    """,
                CompliantExplanation: "The address is no longer embedded as a literal - it's resolved from configuration or the environment at runtime instead."),
        ]);
}
