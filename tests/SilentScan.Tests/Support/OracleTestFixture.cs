using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Support;

/// <summary>
/// Base class for any test whose fixtures need the real SQL Server oracle. Consolidates the
/// per-class "provision a fresh database, deploy DDL, drop it unconditionally" boilerplate
/// every hand-rolled Integration/Oracle test class used to repeat on its own (Phase 0 of the
/// database-testing migration: CLAUDE.md's rule "verify the real thing instead of doing the
/// tests for the sake of it" applies to how the tests themselves are built, not only to what
/// they assert). Each subclass still gets its own disposable database - shared state between
/// unrelated test classes running in parallel is exactly what CLAUDE.md's "no flaky state
/// across runs" rule forbids - but no longer hand-writes the provisioning/teardown dance to
/// get it.
/// </summary>
public abstract class OracleTestFixture : IAsyncLifetime
{
    protected SqlServerOptions Options { get; } = SqlServerOptions.LocalDocker;

    /// <summary>
    /// A name unique to the concrete test class. Callers typically pass
    /// <c>nameof(MyTests)</c> - collisions across classes would otherwise let one class's
    /// teardown race another's still-running probes.
    /// </summary>
    protected abstract string DatabaseNameSeed { get; }

    /// <summary>
    /// The real, per-RUN-unique database name every subclass actually provisions against and
    /// must use for every connection it opens - <see cref="DatabaseNameSeed"/> alone is unique
    /// per CLASS, not per run, so a fixed literal name left behind by a crashed/killed test
    /// (InitializeAsync throwing skips xUnit's own DisposeAsync call entirely - a documented
    /// xUnit gap, not something this fixture can prevent) persists and gets silently reused -
    /// with stale contents, or an unrelated Query Store worker still attached to it - by the
    /// NEXT run of the same class, rather than that run getting a guaranteed-fresh database.
    /// Four exactly such leaked databases (from a single class, `TypedPredicateExtractorOracleTests`,
    /// which pioneered this suffixing pattern for its own fixtures) were found and cleaned up by
    /// hand while diagnosing full-suite flakiness - this rolls that same protection out to every
    /// other Oracle test fixture instead of leaving it as one class's own local pattern.
    /// </summary>
    protected string DatabaseName => _databaseName ??= $"{DatabaseNameSeed}_{Guid.NewGuid():N}";

    private string? _databaseName;

    /// <summary>The DDL (CREATE TABLE/INDEX/VIEW/etc, GO-separated) this test class's probes run against.</summary>
    protected abstract string Ddl { get; }

    public async Task InitializeAsync()
    {
        await new DatabaseProvisioner(Options).CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(Options).DeployAsync(Ddl, DatabaseName);
    }

    public async Task DisposeAsync() =>
        await new DatabaseProvisioner(Options).DropIfExistsAsync(DatabaseName);
}
