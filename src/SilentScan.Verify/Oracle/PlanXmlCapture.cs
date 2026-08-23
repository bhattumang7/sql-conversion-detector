using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Oracle;

public sealed class PlanXmlCapture
{
    private readonly SqlServerOptions _options;

    public PlanXmlCapture(SqlServerOptions options)
    {
        _options = options;
    }

    public Task<string> CaptureAsync(string database, string probeStatement, CancellationToken cancellationToken = default) =>
        CaptureCoreAsync(database, probeStatement, sessionSetStatements: null, cancellationToken);

    public Task<string> CaptureAsync(
        string database, string probeStatement, IReadOnlyList<string> sessionSetStatements, CancellationToken cancellationToken = default) =>
        CaptureCoreAsync(database, probeStatement, sessionSetStatements, cancellationToken);

    private async Task<string> CaptureCoreAsync(
        string database, string probeStatement, IReadOnlyList<string>? sessionSetStatements, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        if (sessionSetStatements is not null)
        {
            foreach (var setStatement in sessionSetStatements)
            {
                await using var setCommand = connection.CreateCommand();
                setCommand.CommandText = setStatement;
                await setCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var onCommand = connection.CreateCommand())
        {
            onCommand.CommandText = "SET SHOWPLAN_XML ON;";
            await onCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        string planXml;
        try
        {
            await using var probeCommand = connection.CreateCommand();
            probeCommand.CommandText = probeStatement;
            probeCommand.CommandTimeout = 60;

            await using var reader = await probeCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("SHOWPLAN_XML produced no plan row for the probe statement.");
            }

            planXml = reader.GetString(0);
        }
        finally
        {
            await using var offCommand = connection.CreateCommand();
            offCommand.CommandText = "SET SHOWPLAN_XML OFF;";
            await offCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return planXml;
    }
}
