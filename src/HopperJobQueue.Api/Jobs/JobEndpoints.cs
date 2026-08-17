using System.Text;
using System.Text.Json;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;

namespace HopperJobQueue.Api.Jobs;

public sealed record EnqueueRequest(
    string? IdempotencyKey, string? Kind, string? Project, JsonElement? Payload,
    int? TtlSeconds, int? MaxAttempts);

public sealed record ClaimRequest(string? WorkerId, int? LeaseSeconds, string[]? Kinds);

public sealed record HeartbeatRequest(Guid? LeaseToken, int? LeaseSeconds);

public sealed record CompleteRequest(Guid? LeaseToken, string? Outcome, JsonElement? Result, string? Error);

public static class JobEndpoints
{
    private const int MaxIdempotencyKeyLength = 200;
    private const int MaxPayloadBytes = 32 * 1024;
    private const int MaxResultBytes = 256 * 1024;
    private const int MaxTtlSeconds = 604800;
    private const int MaxAttemptsCap = 10;
    private const int MaxLeaseSeconds = 86400;
    private const int PageSize = 50;

    private const string LeaseLostDetail =
        "Lease token does not match or the job is no longer leased. The lease is lost — "
        + "stop working on this job; it may have been handed to another worker.";

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/jobs", EnqueueAsync).RequireScope(ApiScope.Producer);
        api.MapGet("/jobs/by-key/{idempotencyKey}", GetByKeyAsync).RequireScope(ApiScope.Producer);
        api.MapGet("/jobs/{id:long}", GetByIdAsync).RequireScope(ApiScope.Producer);

        api.MapPost("/jobs/claim", ClaimAsync).RequireScope(ApiScope.Worker);
        api.MapPost("/jobs/{id:long}/heartbeat", HeartbeatAsync).RequireScope(ApiScope.Worker);
        api.MapPost("/jobs/{id:long}/complete", CompleteAsync).RequireScope(ApiScope.Worker);

        api.MapGet("/jobs", AdminListAsync).RequireScope(ApiScope.Admin);
        api.MapPost("/jobs/{id:long}/requeue", RequeueAsync).RequireScope(ApiScope.Admin);
        api.MapPost("/jobs/{id:long}/cancel", CancelAsync).RequireScope(ApiScope.Admin);
        api.MapGet("/stats", StatsAsync).RequireScope(ApiScope.Admin);
    }

    // ---------- producer ----------

    private static async Task<IResult> EnqueueAsync(
        EnqueueRequest? request, HttpContext context, JobStore store, CancellationToken ct)
    {
        var key = context.GetApiKey()!;
        if (request is null)
        {
            return BadRequest("A JSON body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return BadRequest("idempotencyKey is required.");
        }

        if (request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return BadRequest($"idempotencyKey must be at most {MaxIdempotencyKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            return BadRequest("kind is required.");
        }

        if (request.Payload is not { } payload || payload.ValueKind == JsonValueKind.Undefined)
        {
            return BadRequest("payload is required.");
        }

        var payloadJson = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            return BadRequest($"payload must serialize to at most {MaxPayloadBytes / 1024} KiB.");
        }

        if (request.TtlSeconds is < 1 or > MaxTtlSeconds)
        {
            return BadRequest($"ttlSeconds must be between 1 and {MaxTtlSeconds}.");
        }

        if (request.MaxAttempts is < 1 or > MaxAttemptsCap)
        {
            return BadRequest($"maxAttempts must be between 1 and {MaxAttemptsCap}.");
        }

        var allowedKinds = await AllowedKindsAsync(key, store, ct);
        var kindRecord = await store.GetKindAsync(request.Kind, ct);
        if (kindRecord is null || !allowedKinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unknown kind",
                detail: $"Kind '{request.Kind}' is not available for this key. "
                        + $"Allowed kinds: {(allowedKinds.Length == 0 ? "(none)" : string.Join(", ", allowedKinds))}.",
                extensions: new Dictionary<string, object?> { ["allowedKinds"] = allowedKinds });
        }

        var result = await store.EnqueueAsync(
            request.IdempotencyKey,
            request.Kind,
            string.IsNullOrWhiteSpace(request.Project) ? null : request.Project,
            payloadJson,
            request.TtlSeconds ?? kindRecord.DefaultTtlSeconds,
            request.MaxAttempts ?? kindRecord.DefaultMaxAttempts,
            actor: key.Name,
            ct);

        var body = new { id = result.Job.Id, status = result.Job.Status, created = result.Created };
        return result.Created
            ? Results.Created($"/api/v1/jobs/{result.Job.Id}", body)
            : Results.Ok(body);
    }

    private static async Task<IResult> GetByIdAsync(
        long id, HttpContext context, JobStore store, CancellationToken ct)
    {
        var job = await store.GetAsync(id, ct);
        return await JobReadResultAsync(job, context, store, ct);
    }

    private static async Task<IResult> GetByKeyAsync(
        string idempotencyKey, HttpContext context, JobStore store, CancellationToken ct)
    {
        var job = await store.GetByIdempotencyKeyAsync(idempotencyKey, ct);
        return await JobReadResultAsync(job, context, store, ct);
    }

    private static async Task<IResult> JobReadResultAsync(
        Job? job, HttpContext context, JobStore store, CancellationToken ct)
    {
        var key = context.GetApiKey()!;

        // 404 — jamais 403 : ne pas divulguer l'existence de jobs d'autres files.
        if (job is null
            || (key.Scope != ApiScope.Admin && !key.AllowedKinds.Contains(job.Kind, StringComparer.Ordinal)))
        {
            return Results.NotFound();
        }

        if (key.Scope == ApiScope.Admin)
        {
            var events = await store.GetEventsAsync(job.Id, ct);
            return Results.Ok(AdminJobView(job, events));
        }

        return Results.Ok(JobView(job));
    }

    // ---------- worker ----------

    private static async Task<IResult> ClaimAsync(
        ClaimRequest? request, HttpContext context, JobStore store, CancellationToken ct)
    {
        var key = context.GetApiKey()!;
        if (request is null || string.IsNullOrWhiteSpace(request.WorkerId))
        {
            return BadRequest("workerId is required.");
        }

        if (request.WorkerId.Length > 200)
        {
            return BadRequest("workerId must be at most 200 characters.");
        }

        if (request.LeaseSeconds is < 1 or > MaxLeaseSeconds)
        {
            return BadRequest($"leaseSeconds must be between 1 and {MaxLeaseSeconds}.");
        }

        var allowedKinds = await AllowedKindsAsync(key, store, ct);
        var kinds = request.Kinds is { Length: > 0 }
            ? request.Kinds.Where(k => allowedKinds.Contains(k, StringComparer.Ordinal)).Distinct().ToArray()
            : allowedKinds;

        // Un worker ne peut jamais réclamer une file qui ne lui est pas attribuée.
        if (kinds.Length == 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "None of the requested kinds are allowed for this key.");
        }

        var job = await store.ClaimAsync(request.WorkerId, request.LeaseSeconds, kinds, ct);
        return job is null ? Results.NoContent() : Results.Ok(ClaimedJobView(job));
    }

    private static async Task<IResult> HeartbeatAsync(
        long id, HeartbeatRequest? request, JobStore store, CancellationToken ct)
    {
        if (request is null || request.LeaseToken is not { } token || token == Guid.Empty)
        {
            return BadRequest("leaseToken is required.");
        }

        if (request.LeaseSeconds is < 1 or > MaxLeaseSeconds)
        {
            return BadRequest($"leaseSeconds must be between 1 and {MaxLeaseSeconds}.");
        }

        var leaseUntil = await store.HeartbeatAsync(id, token, request.LeaseSeconds, ct);
        return leaseUntil is null
            ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Lease lost", detail: LeaseLostDetail)
            : Results.Ok(new { id, leaseUntil });
    }

    private static async Task<IResult> CompleteAsync(
        long id, CompleteRequest? request, HttpContext context, JobStore store, CancellationToken ct)
    {
        if (request is null || request.LeaseToken is not { } token || token == Guid.Empty)
        {
            return BadRequest("leaseToken is required.");
        }

        if (request.Outcome is not ("success" or "failure"))
        {
            return BadRequest("outcome must be 'success' or 'failure'.");
        }

        var success = request.Outcome == "success";
        if (!success && string.IsNullOrWhiteSpace(request.Error))
        {
            return BadRequest("error is required when outcome is 'failure'.");
        }

        string? resultJson = null;
        if (request.Result is { } result && result.ValueKind != JsonValueKind.Undefined)
        {
            resultJson = result.GetRawText();
            if (Encoding.UTF8.GetByteCount(resultJson) > MaxResultBytes)
            {
                return BadRequest(
                    $"result must serialize to at most {MaxResultBytes / 1024} KiB. "
                    + "Store large deliverables elsewhere and pass a reference here.");
            }
        }

        var workerId = (await store.GetAsync(id, ct))?.WorkerId;
        var outcome = await store.CompleteAsync(
            id, token, success, resultJson, request.Error, actor: workerId ?? "worker", ct);

        return outcome.Outcome switch
        {
            CompleteResult.Applied or CompleteResult.Replayed => Results.Ok(new
            {
                id = outcome.Job!.Id,
                status = outcome.Job.Status,
                attempts = outcome.Job.Attempts,
                maxAttempts = outcome.Job.MaxAttempts,
            }),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Lease lost", detail: LeaseLostDetail),
        };
    }

    // ---------- admin ----------

    private static async Task<IResult> AdminListAsync(
        HttpContext context, JobStore store, CancellationToken ct,
        string? status, string? project, string? kind, string? q, int page = 1)
    {
        if (status is not null && !JobStatus.All.Contains(status))
        {
            return BadRequest($"status must be one of: {string.Join(", ", JobStatus.All)}.");
        }

        if (page < 1)
        {
            return BadRequest("page must be >= 1.");
        }

        var result = await store.ListAsync(status, project, kind, q, page, PageSize, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(SummaryView),
            page = result.Page,
            pageSize = result.PageSize,
            total = result.Total,
        });
    }

    private static async Task<IResult> RequeueAsync(
        long id, HttpContext context, JobStore store, CancellationToken ct)
    {
        var key = context.GetApiKey()!;
        var (job, invalidStatus) = await store.RequeueAsync(id, actor: key.Name, ct);
        if (job is not null)
        {
            return Results.Ok(new { id = job.Id, status = job.Status });
        }

        return invalidStatus is null
            ? Results.NotFound()
            : Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid transition",
                detail: $"A job in status '{invalidStatus}' cannot be requeued (only failed, expired or cancelled).");
    }

    private static async Task<IResult> CancelAsync(
        long id, HttpContext context, JobStore store, CancellationToken ct)
    {
        var key = context.GetApiKey()!;
        var (job, invalidStatus) = await store.CancelAsync(id, actor: key.Name, ct);
        if (job is not null)
        {
            return Results.Ok(new { id = job.Id, status = job.Status });
        }

        return invalidStatus is null
            ? Results.NotFound()
            : Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid transition",
                detail: $"A job in status '{invalidStatus}' cannot be cancelled (only pending or leased).");
    }

    private static async Task<IResult> StatsAsync(JobStore store, CancellationToken ct)
    {
        var stats = await store.GetStatsAsync(ct);
        return Results.Ok(new
        {
            countsByStatus = stats.CountsByStatus,
            oldestPendingAgeSeconds = stats.OldestPendingAgeSeconds,
            enqueued24h = stats.Enqueued24h,
            done24h = stats.Done24h,
            failed24h = stats.Failed24h,
            workers = stats.Workers.Select(w => new { workerId = w.Actor, lastSeenAt = w.LastSeenAt }),
        });
    }

    // ---------- helpers ----------

    private static async Task<string[]> AllowedKindsAsync(
        Domain.ApiKeyRecord key, JobStore store, CancellationToken ct)
    {
        if (key.Scope != ApiScope.Admin)
        {
            return key.AllowedKinds;
        }

        var kinds = await store.ListKindsAsync(ct);
        return kinds.Select(k => k.Name).ToArray();
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static object JobView(Job job) => new
    {
        id = job.Id,
        idempotencyKey = job.IdempotencyKey,
        kind = job.Kind,
        project = job.Project,
        status = job.Status,
        attempts = job.Attempts,
        maxAttempts = job.MaxAttempts,
        workerId = job.WorkerId,
        createdAt = job.CreatedAt,
        expiresAt = job.ExpiresAt,
        leaseUntil = job.LeaseUntil,
        finishedAt = job.FinishedAt,
        payload = ParseJson(job.Payload),
        result = job.Result is null ? (JsonElement?)null : ParseJson(job.Result),
        lastError = job.LastError,
    };

    // Le leaseToken n'apparaît que dans la réponse de claim — jamais dans les lectures.
    private static object ClaimedJobView(Job job) => new
    {
        id = job.Id,
        idempotencyKey = job.IdempotencyKey,
        kind = job.Kind,
        project = job.Project,
        status = job.Status,
        attempts = job.Attempts,
        maxAttempts = job.MaxAttempts,
        workerId = job.WorkerId,
        createdAt = job.CreatedAt,
        expiresAt = job.ExpiresAt,
        leaseToken = job.LeaseToken,
        leaseUntil = job.LeaseUntil,
        payload = ParseJson(job.Payload),
    };

    private static object SummaryView(Job job) => new
    {
        id = job.Id,
        idempotencyKey = job.IdempotencyKey,
        kind = job.Kind,
        project = job.Project,
        status = job.Status,
        attempts = job.Attempts,
        maxAttempts = job.MaxAttempts,
        workerId = job.WorkerId,
        createdAt = job.CreatedAt,
        expiresAt = job.ExpiresAt,
        finishedAt = job.FinishedAt,
        lastError = job.LastError,
    };

    private static object AdminJobView(Job job, IReadOnlyList<JobEvent> events) => new
    {
        id = job.Id,
        idempotencyKey = job.IdempotencyKey,
        kind = job.Kind,
        project = job.Project,
        status = job.Status,
        attempts = job.Attempts,
        maxAttempts = job.MaxAttempts,
        workerId = job.WorkerId,
        createdAt = job.CreatedAt,
        expiresAt = job.ExpiresAt,
        leaseUntil = job.LeaseUntil,
        finishedAt = job.FinishedAt,
        payload = ParseJson(job.Payload),
        result = job.Result is null ? (JsonElement?)null : ParseJson(job.Result),
        lastError = job.LastError,
        events = events.Select(e => new
        {
            at = e.At,
            fromStatus = e.FromStatus,
            toStatus = e.ToStatus,
            actor = e.Actor,
            note = e.Note,
        }),
    };
}
