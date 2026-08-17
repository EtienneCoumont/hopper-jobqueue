using Dapper;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Infrastructure;
using Npgsql;

namespace HopperJobQueue.Api.Maintenance;

/// <summary>
/// Tâche de fond unique (§8 du brief), toutes les 60 secondes, dans une transaction :
/// 1. pending/leased dont expires_at est dépassé → expired ;
/// 2. leased dont le bail est expiré, tentatives épuisées → failed (les autres sont laissés
///    tels quels : la requête de claim les reprend naturellement) ;
/// 3. purge des jobs terminaux au-delà de la rétention de leur file.
/// Chaque transition écrit dans job_events avec actor = 'system'. Le tampon last_used_at
/// des clés API est vidé au même rythme.
/// </summary>
public sealed class SweeperService(
    NpgsqlDataSource dataSource,
    KeyUsageTracker usageTracker,
    ApiKeyStore apiKeyStore,
    AppConfig config,
    ILogger<SweeperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweep failed; will retry at next tick");
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var expired = await conn.ExecuteScalarAsync<int>(
                """
                with candidate as (
                  select id, status as old_status from jobqueue.jobs
                  where status in ('pending','leased') and expires_at < now()
                  for update skip locked
                ),
                updated as (
                  update jobqueue.jobs j
                  set status = 'expired', finished_at = now(), lease_token = null, lease_until = null
                  from candidate c
                  where j.id = c.id
                  returning j.id, c.old_status
                ),
                ev as (
                  insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
                  select id, old_status, 'expired', 'system', 'expires_at dépassé' from updated
                )
                select count(*) from updated
                """,
                transaction: tx);

            var failed = await conn.ExecuteScalarAsync<int>(
                """
                with candidate as (
                  select id from jobqueue.jobs
                  where status = 'leased' and lease_until < now() and attempts >= max_attempts
                  for update skip locked
                ),
                updated as (
                  update jobqueue.jobs j
                  set status = 'failed', finished_at = now(),
                      last_error = 'bail expiré, tentatives épuisées',
                      lease_token = null, lease_until = null
                  from candidate c
                  where j.id = c.id
                  returning j.id
                ),
                ev as (
                  insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
                  select id, 'leased', 'failed', 'system', 'bail expiré, tentatives épuisées' from updated
                )
                select count(*) from updated
                """,
                transaction: tx);

            var purged = await conn.ExecuteAsync(
                """
                delete from jobqueue.jobs j
                using jobqueue.job_kinds k
                where k.name = j.kind
                  and j.status in ('done','failed','expired','cancelled')
                  and coalesce(j.finished_at, j.created_at) < now() - make_interval(days => k.retention_days)
                """,
                transaction: tx);

            await tx.CommitAsync(ct);

            if (expired > 0 || failed > 0 || purged > 0)
            {
                logger.LogInformation(
                    "Sweep: {Expired} expired, {Failed} failed (lease exhausted), {Purged} purged",
                    expired, failed, purged);
            }
        }

        await apiKeyStore.FlushUsageAsync(usageTracker.Drain(), ct);
    }
}
