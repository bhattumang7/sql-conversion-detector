namespace SilentScan.Verify;

public sealed record SqlServerOptions(string Host, int Port, string UserId, string Password)
{
    public static SqlServerOptions LocalDocker { get; } = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL_PORT"), out var port) ? port : 14330,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    public string BuildConnectionString(string? database = null) =>
        $"Server={Host},{Port};User Id={UserId};Password={Password};"
        + $"{(database is null ? string.Empty : $"Database={database};")}"
        + "TrustServerCertificate=True;";
}
