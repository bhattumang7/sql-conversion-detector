using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Support;

public abstract class OracleTestFixture : IAsyncLifetime
{
    protected SqlServerOptions Options { get; } = SqlServerOptions.LocalDocker;

protected abstract string DatabaseNameSeed { get; }

protected string DatabaseName => _databaseName ??= $"{DatabaseNameSeed}_{Guid.NewGuid():N}";

    private string? _databaseName;

protected abstract string Ddl { get; }

    public virtual async Task InitializeAsync()
    {
        await new DatabaseProvisioner(Options).CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(Options).DeployAsync(Ddl, DatabaseName);
    }

    public virtual async Task DisposeAsync() =>
        await new DatabaseProvisioner(Options).DropIfExistsAsync(DatabaseName);
}
