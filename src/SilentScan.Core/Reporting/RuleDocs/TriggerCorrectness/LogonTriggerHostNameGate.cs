using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class LogonTriggerHostNameGate
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessLogonTriggerHostNameGateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            HOST_NAME() returns the workstation name the client supplied when it opened the
            connection - literally the value of the Workstation ID connection-string property in
            ADO.NET/ODBC/OLE DB (or the equivalent -H flag for sqlcmd/bcp, or whatever the driver's
            default is when the caller doesn't set it explicitly). It is not derived from anything
            the server independently observes about the machine at the other end of the socket; it
            is a free-text field the client process chooses and sends as part of the login packet,
            exactly like the application name. There is nothing in the TDS protocol that
            authenticates or verifies it.

            That means any client capable of setting a connection-string property - which is every
            client - can set Workstation ID to whatever string it wants, including the exact
            hostname a logon trigger's allow-list expects. A logon trigger that does something like
            IF HOST_NAME() NOT IN ('APPSERVER01', 'APPSERVER02') ROLLBACK is not actually verifying
            that the connection originated from those machines; it's verifying that the connecting
            client claimed to be one of those machines, a claim it can make from anywhere - a
            developer's laptop, a compromised host, an entirely different network - by setting one
            property in a connection string. The trigger's ROLLBACK gives every appearance of
            enforcing an access boundary, including in a security review of the trigger's own logic,
            while actually enforcing nothing beyond "the client said the right words."

            This matters specifically because logon triggers are commonly reached for as a
            lightweight way to implement network- or host-based access restrictions without
            standing up firewall rules or certificate infrastructure - and HOST_NAME() looks, from
            the trigger author's perspective, exactly like the kind of value that should carry that
            information. The bug is not in the trigger's logic; it's in the premise that
            HOST_NAME() is a trustworthy signal at all.
            """,
        HowToFixIt: """
            Do not gate an access control decision on HOST_NAME() - it is client-supplied over the
            connection string and trivially spoofable by anything capable of setting a connection
            property, which is every client. For a real gate, use something the client cannot
            unilaterally choose: EVENTDATA() inside the logon trigger exposes the actual client net
            address (still spoofable at the IP level to a degree, but that requires network-level
            control rather than a one-line connection-string change, and it can be correlated
            against firewall/NSG rules that constrain it further), or - stronger still - rely on the
            login/authentication identity itself (Windows-authenticated login, or a certificate/
            Azure AD-backed login) rather than any claim about the origin machine.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A logon trigger trusts a client-supplied workstation name",
                NoncompliantSql: """
                    CREATE TRIGGER trg_RestrictByHost
                    ON ALL SERVER
                    FOR LOGON
                    AS
                    BEGIN
                        IF HOST_NAME() NOT IN ('APPSERVER01', 'APPSERVER02')
                        BEGIN
                            ROLLBACK;
                        END;
                    END;
                    """,
                NoncompliantExplanation: "HOST_NAME() returns the client-supplied Workstation ID connection-string property - any client can set Workstation ID=APPSERVER01 and pass this check regardless of where it is actually connecting from, so the trigger enforces nothing beyond what the client chooses to claim.",
                CompliantSql: """
                    CREATE TRIGGER trg_RestrictByNetAddress
                    ON ALL SERVER
                    FOR LOGON
                    AS
                    BEGIN
                        DECLARE @EventData XML = EVENTDATA();
                        DECLARE @ClientHost VARCHAR(64) = @EventData.value('(/EVENT_INSTANCE/ClientHost)[1]', 'VARCHAR(64)');

                        IF @ClientHost NOT IN ('10.0.1.10', '10.0.1.11')
                        BEGIN
                            ROLLBACK;
                        END;
                    END;
                    """,
                CompliantExplanation: "EVENTDATA() reports the actual connecting client address the server observed, not a value the client freely chose - it still requires network-level control to spoof, unlike a connection-string property."),
        ]);
}
