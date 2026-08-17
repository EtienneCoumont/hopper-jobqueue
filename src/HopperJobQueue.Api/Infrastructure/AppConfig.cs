namespace HopperJobQueue.Api.Infrastructure;

/// <summary>
/// Service configuration, read exclusively from <c>HOPPER_</c>-prefixed environment
/// variables (the prefix is stripped by the configuration provider).
/// </summary>
public sealed class AppConfig
{
    public required string ConnectionString { get; init; }
    public string? BootstrapAdminKey { get; init; }
    public int SweepIntervalSeconds { get; init; } = 60;

    public static AppConfig Load(IConfiguration configuration)
    {
        var connectionString = configuration["DB_CONNECTIONSTRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "HOPPER_DB_CONNECTIONSTRING is required (Npgsql connection string).");
        }

        var sweepInterval = 60;
        var rawSweep = configuration["SWEEP_INTERVAL_SECONDS"];
        if (!string.IsNullOrWhiteSpace(rawSweep))
        {
            if (!int.TryParse(rawSweep, out sweepInterval) || sweepInterval < 1)
            {
                throw new InvalidOperationException(
                    "HOPPER_SWEEP_INTERVAL_SECONDS must be a positive integer.");
            }
        }

        return new AppConfig
        {
            ConnectionString = connectionString,
            BootstrapAdminKey = configuration["BOOTSTRAP_ADMIN_KEY"],
            SweepIntervalSeconds = sweepInterval,
        };
    }
}
