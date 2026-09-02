using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Restore;

internal static class RecoveryStateConflict
{
    public static string RuleId => SarifRuleCatalog.RestoreOptionConflictRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `RECOVERY`, `NORECOVERY`, and `STANDBY` each tell `RESTORE` what state to leave the
            database in once this restore step completes: `RECOVERY` rolls forward and opens the
            database for use, `NORECOVERY` leaves it in a restoring state so a later restore step can
            still be applied, and `STANDBY` leaves it read-only with an undo file so it can both be
            queried and still accept a later restore step. These are three different, mutually
            exclusive end states - a single `RESTORE` statement can only request one of them.

            Oracle-confirmed: every pairing among the three always fails with Msg 3031
            ("Option '...' conflicts with option(s) '...'. Remove the conflicting option and reissue
            the statement"), decidable purely from the statement's own `WITH` clause - no backup
            history or database state affects the outcome.
            """,
        HowToFixIt: "Keep exactly one of RECOVERY, NORECOVERY, or STANDBY in the RESTORE statement's WITH clause, matching whether this is the final step in the restore sequence (RECOVERY), an intermediate step with more to follow (NORECOVERY), or an intermediate step that also needs to be queryable in between (STANDBY).",
        Examples:
        [
            new RuleDocExample(
                Title: "NORECOVERY combined with RECOVERY",
                NoncompliantSql: "RESTORE DATABASE dbo.SomeDatabase FROM DISK = 'D:\\backups\\some.bak' WITH NORECOVERY, RECOVERY;",
                NoncompliantExplanation: "RECOVERY opens the database for use immediately; NORECOVERY leaves it awaiting a further restore step. Requesting both always fails with Msg 3031."),
            new RuleDocExample(
                Title: "STANDBY combined with RECOVERY",
                NoncompliantSql: "RESTORE LOG dbo.SomeDatabase FROM DISK = 'D:\\backups\\some.trn' WITH STANDBY = 'D:\\backups\\some.undo', RECOVERY;",
                NoncompliantExplanation: "STANDBY leaves the database read-only and still restorable; RECOVERY opens it fully. Requesting both always fails with Msg 3031."),
        ]);
}
