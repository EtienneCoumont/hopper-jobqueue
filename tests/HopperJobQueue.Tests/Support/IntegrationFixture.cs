using Dapper;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HopperJobQueue.Tests.Support;

/// <summary>
/// A real PostgreSQL database (Testcontainers) + the full application
/// (WebApplicationFactory), shared by the whole "integration" collection. Each test
/// resets the tables.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _postgres = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("hopper")
            .Build();
        await _postgres.StartAsync();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DB_CONNECTIONSTRING", ConnectionString);
            // The sweeper must only run when a test invokes it explicitly.
            builder.UseSetting("SWEEP_INTERVAL_SECONDS", "3600");
        });

        // Forces startup (migrations + bootstrap) before the first test.
        _ = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            truncate jobqueue.jobs, jobqueue.job_events, jobqueue.api_keys restart identity cascade;
            delete from jobqueue.job_kinds;
            """);
    }

    public async Task SeedKindAsync(
        string name, bool enabled = true, int defaultTtlSeconds = 86400,
        int defaultMaxAttempts = 3, int defaultLeaseSeconds = 1200, int retentionDays = 90)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            insert into jobqueue.job_kinds
              (name, enabled, default_ttl_seconds, default_max_attempts, default_lease_seconds, retention_days)
            values (@Name, @Enabled, @Ttl, @MaxAttempts, @Lease, @Retention)
            """,
            new { Name = name, Enabled = enabled, Ttl = defaultTtlSeconds, MaxAttempts = defaultMaxAttempts, Lease = defaultLeaseSeconds, Retention = retentionDays });
    }

    /// <summary>Creates an API key in the database and returns the clear-text secret for the test.</summary>
    public async Task<string> CreateKeyAsync(string scope, params string[] allowedKinds)
    {
        var key = ApiKeys.Generate(scope);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            insert into jobqueue.api_keys (name, prefix, key_hash, scope, allowed_kinds)
            values (@Name, @Prefix, @Hash, @Scope, @AllowedKinds)
            """,
            new
            {
                Name = $"test-{scope}-{Guid.NewGuid():N}",
                Prefix = ApiKeys.Prefix(key),
                Hash = ApiKeys.Hash(key),
                Scope = scope,
                AllowedKinds = allowedKinds,
            });
        return key;
    }

    public HttpClient ClientWithKey(string key)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return client;
    }

    public async Task<T> DbScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("null scalar");
    }

    public async Task<int> DbExecuteAsync(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteAsync(sql, args);
    }
}

[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>;
