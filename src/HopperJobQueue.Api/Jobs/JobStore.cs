using Dapper;
using HopperJobQueue.Api.Domain;
using Npgsql;

namespace HopperJobQueue.Api.Jobs;

public sealed record EnqueueResult(Job Job, bool Created);

public sealed record CompleteResult(string Outcome, Job? Job)
{
    public const string Applied = "applied";
    public const string Replayed = "replayed";
    public const string Conflict = "conflict";
    public const string NotFound = "not-found";
}

public sealed record JobPage(IReadOnlyList<Job> Items, int Page, int PageSize, long Total);

public sealed record QueueStats(
    Dictionary<string, long> CountsByStatus,
    double? OldestPendingAgeSeconds,
    long Enqueued24h,
    long Done24h,
    long Failed24h,
    IReadOnlyList<WorkerActivity> Workers);

public sealed record WorkerActivity(string Actor, DateTimeOffset LastSeenAt);

public sealed class JobStore(NpgsqlDataSource dataSource)
{
    // ---------- kinds ----------

    public async Task<JobKind?> GetKindAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<JobKind>(
            "select * from jobqueue.job_kinds where name = @Name", new { Name = name });
    }

    public async Task<IReadOnlyList<JobKind>> ListKindsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<JobKind>("select * from jobqueue.job_kinds order by name")).ToList();
    }

    public async Task<bool> CreateKindAsync(JobKind kind, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(
            """
            insert into jobqueue.job_kinds
              (name, description, enabled, default_ttl_seconds, default_max_attempts, default_lease_seconds, retention_days)
            values (@Name, @Description, @Enabled, @DefaultTtlSeconds, @DefaultMaxAttempts, @DefaultLeaseSeconds, @RetentionDays)
            on conflict (name) do nothing
            """,
            kind);
        return inserted > 0;
    }

    public async Task<bool> SetKindEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "update jobqueue.job_kinds set enabled = @Enabled where name = @Name",
            new { Name = name, Enabled = enabled }) > 0;
    }

    // ---------- enqueue ----------

    /// <summary>
    /// Idempotency in the database: <c>on conflict (idempotency_key) do nothing</c> then
    /// re-read — never a prior select, otherwise two simultaneous requests both get through.
    /// </summary>
    public async Task<EnqueueResult> EnqueueAsync(
        string idempotencyKey, string kind, string? project, string payloadJson,
        int ttlSeconds, int maxAttempts, string actor, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var created = await conn.QuerySingleOrDefaultAsync<Job>(
                """
                insert into jobqueue.jobs (idempotency_key, kind, project, payload, max_attempts, expires_at)
                values (@IdempotencyKey, @Kind, @Project, @Payload::jsonb, @MaxAttempts,
                        now() + make_interval(secs => @TtlSeconds))
                on conflict (idempotency_key) do nothing
                returning *
                """,
                new { IdempotencyKey = idempotencyKey, Kind = kind, Project = project, Payload = payloadJson, MaxAttempts = maxAttempts, TtlSeconds = (double)ttlSeconds },
                tx);

            if (created is not null)
            {
                await conn.ExecuteAsync(
                    """
                    insert into jobqueue.job_events (job_id, from_status, to_status, actor)
                    values (@JobId, null, 'pending', @Actor)
                    """,
                    new { JobId = created.Id, Actor = actor },
                    tx);
                await tx.CommitAsync(ct);
                return new EnqueueResult(created, true);
            }

            await tx.CommitAsync(ct);
        }

        // The key already exists: re-read outside the transaction (the winning row is committed).
        var existing = await conn.QuerySingleAsync<Job>(
            "select * from jobqueue.jobs where idempotency_key = @IdempotencyKey",
            new { IdempotencyKey = idempotencyKey });
        return new EnqueueResult(existing, false);
    }

    // ---------- claim ----------

    /// <summary>
    /// Fair reservation: one candidate per eligible queue (the oldest), random pick,
    /// <c>for update skip locked</c> locking — mandatory, without it two concurrent claims
    /// can get the same job. The eligibility predicates are repeated in the locking select:
    /// under READ COMMITTED, the re-check (EvalPlanQual) after a concurrent modification
    /// relies on them to discard an already-reserved job, whereas the sub-select's candidate
    /// list dates from the starting snapshot.
    /// </summary>
    private const string ClaimSql =
        """
        with candidate as (
          select id, status as old_status
          from jobqueue.jobs
          where id in (
            select distinct on (j.kind) j.id
            from jobqueue.jobs j
            join jobqueue.job_kinds k on k.name = j.kind
            where j.kind = any(@Kinds)
              and k.enabled
              and (j.status = 'pending' or (j.status = 'leased' and j.lease_until < now()))
              and j.expires_at > now()
              and j.attempts < j.max_attempts
            order by j.kind, j.created_at
          )
          and (status = 'pending' or (status = 'leased' and lease_until < now()))
          and expires_at > now()
          and attempts < max_attempts
          order by random()
          limit 1
          for update skip locked
        ),
        updated as (
          update jobqueue.jobs j
          set status      = 'leased',
              attempts    = j.attempts + 1,
              lease_token = gen_random_uuid(),
              lease_until = now() + make_interval(secs => coalesce(
                  @LeaseSeconds,
                  (select k.default_lease_seconds from jobqueue.job_kinds k where k.name = j.kind))),
              worker_id   = @WorkerId
          from candidate c
          where j.id = c.id
          returning j.*, c.old_status
        ),
        ev as (
          insert into jobqueue.job_events (job_id, from_status, to_status, actor)
          select u.id, u.old_status, 'leased', @WorkerId from updated u
        )
        select * from updated
        """;

    private const string AnyEligibleSql =
        """
        select exists (
          select 1
          from jobqueue.jobs j
          join jobqueue.job_kinds k on k.name = j.kind
          where j.kind = any(@Kinds)
            and k.enabled
            and (j.status = 'pending' or (j.status = 'leased' and j.lease_until < now()))
            and j.expires_at > now()
            and j.attempts < j.max_attempts
        )
        """;

    public async Task<Job?> ClaimAsync(
        string workerId, int? leaseSeconds, string[] kinds, CancellationToken ct = default)
    {
        if (kinds.Length == 0)
        {
            return null;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var parameters = new
        {
            Kinds = kinds,
            WorkerId = workerId,
            LeaseSeconds = (double?)leaseSeconds,
        };

        // Under contention, the designated candidate can be snatched between the snapshot and
        // the lock (skip locked or re-check): the statement then returns zero rows although
        // the queue is not empty. Retry as long as eligible jobs remain.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var job = await conn.QuerySingleOrDefaultAsync<Job>(ClaimSql, parameters);
            if (job is not null)
            {
                return job;
            }

            var anyLeft = await conn.ExecuteScalarAsync<bool>(AnyEligibleSql, new { Kinds = kinds });
            if (!anyLeft)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(2 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10)), ct);
        }

        return null;
    }

    // ---------- heartbeat ----------

    public async Task<DateTimeOffset?> HeartbeatAsync(
        long jobId, Guid leaseToken, int? leaseSeconds, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTimeOffset?>(
            """
            update jobqueue.jobs
            set lease_until = now() + make_interval(secs => coalesce(
                @LeaseSeconds,
                (select k.default_lease_seconds from jobqueue.job_kinds k where k.name = jobqueue.jobs.kind)))
            where id = @JobId and lease_token = @LeaseToken and status = 'leased'
            returning lease_until
            """,
            new { JobId = jobId, LeaseToken = leaseToken, LeaseSeconds = (double?)leaseSeconds });
    }

    // ---------- complete ----------

    public async Task<CompleteResult> CompleteAsync(
        long jobId, Guid leaseToken, bool success, string? resultJson, string? error,
        string actor, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var updated = await conn.QuerySingleOrDefaultAsync<Job>(
                """
                update jobqueue.jobs
                set status = case
                        when @Success then 'done'
                        when attempts >= max_attempts then 'failed'
                        else 'pending' end,
                    result = case when @Success then @Result::jsonb else result end,
                    last_error = case when @Success then null else @Error end,
                    finished_at = case
                        when @Success or attempts >= max_attempts then now()
                        else null end,
                    lease_until = null
                where id = @JobId and lease_token = @LeaseToken and status = 'leased'
                returning *
                """,
                new { JobId = jobId, LeaseToken = leaseToken, Success = success, Result = resultJson, Error = error },
                tx);

            if (updated is not null)
            {
                await conn.ExecuteAsync(
                    """
                    insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
                    values (@JobId, 'leased', @ToStatus, @Actor, @Note)
                    """,
                    new
                    {
                        JobId = jobId,
                        ToStatus = updated.Status,
                        Actor = actor,
                        Note = success ? null : Truncate(error, 500),
                    },
                    tx);
                await tx.CommitAsync(ct);
                return new CompleteResult(CompleteResult.Applied, updated);
            }

            await tx.CommitAsync(ct);
        }

        // No transition: either an idempotent replay of the same complete, or a lost lease.
        var current = await GetAsync(jobId, ct);
        if (current is null)
        {
            return new CompleteResult(CompleteResult.NotFound, null);
        }

        if (current.LeaseToken == leaseToken && current.Status != JobStatus.Leased)
        {
            return new CompleteResult(CompleteResult.Replayed, current);
        }

        return new CompleteResult(CompleteResult.Conflict, current);
    }

    // ---------- reads ----------

    public async Task<Job?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Job>(
            "select * from jobqueue.jobs where id = @Id", new { Id = id });
    }

    public async Task<Job?> GetByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Job>(
            "select * from jobqueue.jobs where idempotency_key = @Key", new { Key = key });
    }

    public async Task<IReadOnlyList<JobEvent>> GetEventsAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<JobEvent>(
            "select * from jobqueue.job_events where job_id = @JobId order by at, id",
            new { JobId = jobId })).ToList();
    }

    public async Task<JobPage> ListAsync(
        string? status, string? project, string? kind, string? search, int page, int pageSize,
        CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            filters.Add("status = @Status");
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            filters.Add("project = @Project");
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            filters.Add("kind = @Kind");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add("""
                (idempotency_key ilike @Search escape '\'
                 or coalesce(project, '') ilike @Search escape '\'
                 or coalesce(worker_id, '') ilike @Search escape '\'
                 or coalesce(last_error, '') ilike @Search escape '\'
                 or payload::text ilike @Search escape '\')
                """);
        }

        var where = filters.Count > 0 ? "where " + string.Join(" and ", filters) : "";
        var parameters = new
        {
            Status = status,
            Project = project,
            Kind = kind,
            Search = search is null ? null : "%" + EscapeLike(search) + "%",
            Offset = (page - 1) * pageSize,
            PageSize = pageSize,
        };

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>(
            $"select count(*) from jobqueue.jobs {where}", parameters);
        var items = (await conn.QueryAsync<Job>(
            $"""
            select * from jobqueue.jobs {where}
            order by created_at desc, id desc
            limit @PageSize offset @Offset
            """, parameters)).ToList();

        return new JobPage(items, page, pageSize, total);
    }

    // ---------- admin transitions ----------

    /// <summary>Requeue: allowed from failed / expired / cancelled — never from done.</summary>
    public async Task<(Job? Job, string? InvalidStatus)> RequeueAsync(long id, string actor, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<Job>(
            """
            with candidate as (
              select id, status as old_status from jobqueue.jobs
              where id = @Id and status in ('failed','expired','cancelled')
              for update
            ),
            updated as (
              update jobqueue.jobs j
              set status = 'pending', attempts = 0, lease_token = null, lease_until = null,
                  worker_id = null, finished_at = null,
                  expires_at = greatest(j.expires_at, now() + make_interval(secs =>
                      (select k.default_ttl_seconds from jobqueue.job_kinds k where k.name = j.kind)))
              from candidate c
              where j.id = c.id
              returning j.*, c.old_status
            ),
            ev as (
              insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
              select u.id, u.old_status, 'pending', @Actor, 'requeue' from updated u
            )
            select * from updated
            """,
            new { Id = id, Actor = actor },
            tx);
        await tx.CommitAsync(ct);

        if (updated is not null)
        {
            return (updated, null);
        }

        var current = await GetAsync(id, ct);
        return (null, current?.Status);
    }

    public async Task<(Job? Job, string? InvalidStatus)> CancelAsync(long id, string actor, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<Job>(
            """
            with candidate as (
              select id, status as old_status from jobqueue.jobs
              where id = @Id and status in ('pending','leased')
              for update
            ),
            updated as (
              update jobqueue.jobs j
              set status = 'cancelled', finished_at = now(), lease_token = null, lease_until = null
              from candidate c
              where j.id = c.id
              returning j.*, c.old_status
            ),
            ev as (
              insert into jobqueue.job_events (job_id, from_status, to_status, actor, note)
              select u.id, u.old_status, 'cancelled', @Actor, 'cancel' from updated u
            )
            select * from updated
            """,
            new { Id = id, Actor = actor },
            tx);
        await tx.CommitAsync(ct);

        if (updated is not null)
        {
            return (updated, null);
        }

        var current = await GetAsync(id, ct);
        return (null, current?.Status);
    }

    // ---------- stats ----------

    public async Task<QueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var counts = (await conn.QueryAsync<(string Status, long Count)>(
            "select status, count(*) from jobqueue.jobs group by status"))
            .ToDictionary(r => r.Status, r => r.Count);

        var oldestPending = await conn.ExecuteScalarAsync<double?>(
            "select extract(epoch from now() - min(created_at)) from jobqueue.jobs where status = 'pending'");

        var enqueued = await conn.ExecuteScalarAsync<long>(
            "select count(*) from jobqueue.jobs where created_at > now() - interval '24 hours'");
        var done = await conn.ExecuteScalarAsync<long>(
            "select count(*) from jobqueue.jobs where status = 'done' and finished_at > now() - interval '24 hours'");
        var failed = await conn.ExecuteScalarAsync<long>(
            "select count(*) from jobqueue.jobs where status = 'failed' and finished_at > now() - interval '24 hours'");

        var workers = (await conn.QueryAsync<WorkerActivity>(
            """
            select actor, max(at) as last_seen_at
            from jobqueue.job_events
            where actor <> 'system'
              and (to_status = 'leased'
                   or (from_status = 'leased' and to_status in ('done','failed','pending')))
            group by actor
            order by max(at) desc
            limit 20
            """)).ToList();

        return new QueueStats(counts, oldestPending, enqueued, done, failed, workers);
    }

    private static string EscapeLike(string input) =>
        input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static string? Truncate(string? input, int max) =>
        input is null || input.Length <= max ? input : input[..max];
}
