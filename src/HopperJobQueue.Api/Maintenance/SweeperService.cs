using Dapper;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Infrastructure;
using Npgsql;

namespace HopperJobQueue.Api.Maintenance;

/// <summary>
/// Single background task, every 60 seconds, in one transaction:
/// 1. pending/leased jobs whose expires_at has passed → expired;
/// 2. leased jobs whose lease expired with attempts exhausted → failed (the others are
///    left as-is: the claim query picks them up naturally);
/// 3. purge of terminal jobs beyond their queue's retention.
/// Every transition writes to job_events with actor = 'system'. The API keys'
/// last_used_at buffer is flushed at the same pace.
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
                  select id, old_status, 'expired', 'system', 'expires_at exceeded' from updated
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
                      last_error = 'lease expired, attempts exhausted',
                      lease_token = null, lease_until = null
                  from candidate c
                  where j.id = c.id
                  returning j.id
                ),
                ev as (
                  insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
                  select id, 'leased', 'failed', 'system', 'lease expired, attempts exhausted' from updated
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
