using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Backup;

internal static class DifferentialCopyOnly
{
    public static string RuleId => SarifRuleCatalog.BackupOptionConflictRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `COPY_ONLY` and `DIFFERENTIAL` are mutually exclusive on `BACKUP DATABASE`, decidable
            purely from the statement's own option list. A copy-only full backup deliberately does
            not update the differential base - it exists precisely so an ad hoc full backup doesn't
            disturb the differential chain a maintenance plan is relying on. Because of that, no
            copy-only backup ever counts as "the current database backup" a differential can diff
            against.

            Oracle-confirmed: `BACKUP DATABASE ... WITH DIFFERENTIAL, COPY_ONLY` always fails
            (Msg 3035, "cannot perform a differential backup ... no current database backup"). The
            engine surfaces the generic "no current backup" message rather than a dedicated
            "these two options conflict" one, but the combination itself can never succeed - it's
            not a runtime accident of missing a prior full backup.
            """,
        HowToFixIt: "Drop COPY_ONLY if a differential base is needed, or drop DIFFERENTIAL if the backup is genuinely meant to be copy-only.",
        Examples:
        [
            new RuleDocExample(
                Title: "DIFFERENTIAL combined with COPY_ONLY",
                NoncompliantSql: "BACKUP DATABASE dbo.SomeDatabase TO DISK = 'D:\\backups\\some.bak' WITH DIFFERENTIAL, COPY_ONLY;",
                NoncompliantExplanation: "COPY_ONLY backups never register as a differential base, so this statement always fails with Msg 3035 regardless of backup history."),
        ]);
}
