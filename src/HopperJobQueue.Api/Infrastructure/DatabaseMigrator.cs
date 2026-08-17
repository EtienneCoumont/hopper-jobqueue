using DbUp;
using DbUp.Engine.Output;
using Npgsql;

namespace HopperJobQueue.Api.Infrastructure;

public static class DatabaseMigrator
{
    // Identifiant arbitraire mais fixe : deux instances qui migrent en même temps se sérialisent.
    private const long AdvisoryLockId = 727_001_942;

    public static void Run(string connectionString, ILogger logger)
    {
        using var lockConnection = new NpgsqlConnection(connectionString);
        lockConnection.Open();

        using (var cmd = new NpgsqlCommand($"select pg_advisory_lock({AdvisoryLockId})", lockConnection))
        {
            cmd.ExecuteNonQuery();
        }

        try
        {
            // Le journal DbUp vit dans le schéma jobqueue ; il doit exister avant la première migration.
            using (var cmd = new NpgsqlCommand("create schema if not exists jobqueue", lockConnection))
            {
                cmd.ExecuteNonQuery();
            }

            var upgrader = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(
                    typeof(DatabaseMigrator).Assembly,
                    name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .JournalToPostgresqlTable("jobqueue", "schemaversions")
                .WithTransactionPerScript()
                .LogTo(new UpgradeLogAdapter(logger))
                .Build();

            var result = upgrader.PerformUpgrade();
            if (!result.Successful)
            {
                throw new InvalidOperationException(
                    $"Database migration failed on script '{result.ErrorScript?.Name}'.", result.Error);
            }
        }
        finally
        {
            using var cmd = new NpgsqlCommand($"select pg_advisory_unlock({AdvisoryLockId})", lockConnection);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class UpgradeLogAdapter(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace("{Message}", string.Format(format, args));

        public void LogDebug(string format, params object[] args) => logger.LogDebug("{Message}", string.Format(format, args));

        public void LogInformation(string format, params object[] args) => logger.LogInformation("{Message}", string.Format(format, args));

        public void LogWarning(string format, params object[] args) => logger.LogWarning("{Message}", string.Format(format, args));

        public void LogError(string format, params object[] args) => logger.LogError("{Message}", string.Format(format, args));

        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, "{Message}", string.Format(format, args));
    }
}
