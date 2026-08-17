using Dapper;
using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Infrastructure;
using Npgsql;

namespace HopperJobQueue.Api.Auth;

public sealed class ApiKeyStore(NpgsqlDataSource dataSource)
{
    public async Task<ApiKeyRecord?> AuthenticateAsync(string presentedKey, CancellationToken ct = default)
    {
        if (!presentedKey.StartsWith("hjq_", StringComparison.Ordinal) || presentedKey.Length < ApiKeys.PrefixLength)
        {
            return null;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var record = await conn.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            "select * from jobqueue.api_keys where prefix = @Prefix",
            new { Prefix = ApiKeys.Prefix(presentedKey) });

        if (record is null || record.RevokedAt is not null || !ApiKeys.Verify(record.KeyHash, presentedKey))
        {
            return null;
        }

        return record;
    }

    public async Task<(ApiKeyRecord Record, string PlaintextKey)> CreateAsync(
        string name, string scope, string[] allowedKinds, CancellationToken ct = default)
    {
        if (!ApiScope.All.Contains(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var key = ApiKeys.Generate(scope);
            try
            {
                var record = await conn.QuerySingleAsync<ApiKeyRecord>(
                    """
                    insert into jobqueue.api_keys (name, prefix, key_hash, scope, allowed_kinds)
                    values (@Name, @Prefix, @KeyHash, @Scope, @AllowedKinds)
                    returning *
                    """,
                    new { Name = name, Prefix = ApiKeys.Prefix(key), KeyHash = ApiKeys.Hash(key), Scope = scope, AllowedKinds = allowedKinds });
                return (record, key);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt < 5)
            {
                // Prefix collision (vanishingly rare): draw another key.
            }
        }
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var count = await conn.ExecuteAsync(
            "update jobqueue.api_keys set revoked_at = now() where id = @Id and revoked_at is null",
            new { Id = id });
        return count > 0;
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ApiKeyRecord>(
            "select * from jobqueue.api_keys order by created_at desc");
        return rows.ToList();
    }

    public async Task<ApiKeyRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            "select * from jobqueue.api_keys where id = @Id", new { Id = id });
    }

    public async Task FlushUsageAsync(IReadOnlyDictionary<long, DateTimeOffset> usages, CancellationToken ct = default)
    {
        if (usages.Count == 0)
        {
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            """
            update jobqueue.api_keys k
            set last_used_at = greatest(coalesce(k.last_used_at, '-infinity'::timestamptz), u.at)
            from unnest(@Ids, @Ats) as u(id, at)
            where k.id = u.id
            """,
            new { Ids = usages.Keys.ToArray(), Ats = usages.Values.Select(v => v.UtcDateTime).ToArray() });
    }

    /// <summary>
    /// Bootstrap: if the table is empty at startup, creates an admin key — taken from
    /// <c>HOPPER_BOOTSTRAP_ADMIN_KEY</c>, otherwise generated and written once to the
    /// logs at Warning level. This is the only place in the code where a full key is logged.
    /// </summary>
    public async Task EnsureBootstrapKeyAsync(AppConfig config, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<long>("select count(*) from jobqueue.api_keys");
        if (count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.BootstrapAdminKey))
        {
            var provided = config.BootstrapAdminKey.Trim();
            if (!ApiKeys.HasValidShape(provided, ApiScope.Admin))
            {
                throw new InvalidOperationException(
                    "HOPPER_BOOTSTRAP_ADMIN_KEY must match the format hjq_admin_{32 base62 chars}.");
            }

            await conn.ExecuteAsync(
                """
                insert into jobqueue.api_keys (name, prefix, key_hash, scope, allowed_kinds)
                values ('bootstrap-admin', @Prefix, @KeyHash, 'admin', '{}')
                on conflict (prefix) do nothing
                """,
                new { Prefix = ApiKeys.Prefix(provided), KeyHash = ApiKeys.Hash(provided) });
            logger.LogInformation("Bootstrap admin key installed from HOPPER_BOOTSTRAP_ADMIN_KEY (prefix {Prefix})",
                ApiKeys.Prefix(provided));
            return;
        }

        var generated = ApiKeys.Generate(ApiScope.Admin);
        await conn.ExecuteAsync(
            """
            insert into jobqueue.api_keys (name, prefix, key_hash, scope, allowed_kinds)
            values ('bootstrap-admin', @Prefix, @KeyHash, 'admin', '{}')
            """,
            new { Prefix = ApiKeys.Prefix(generated), KeyHash = ApiKeys.Hash(generated) });

        logger.LogWarning(
            "API key table was empty. A bootstrap admin key has been created: {Key} — "
            + "store it now, it will never be shown again. Create your real keys from the "
            + "dashboard, then revoke this bootstrap key.",
            generated);
    }
}
