using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Jobs;

namespace HopperJobQueue.Api.Admin;

public sealed record CreateKindRequest(
    string? Name, string? Description, bool? Enabled,
    int? DefaultTtlSeconds, int? DefaultMaxAttempts, int? DefaultLeaseSeconds, int? RetentionDays);

public sealed record PatchKindRequest(bool? Enabled);

public sealed record CreateKeyRequest(string? Name, string? Scope, string[]? AllowedKinds);

public sealed record PatchKeyRequest(string? Name, string[]? AllowedKinds);

/// <summary>
/// Admin API: feature parity with the admin dashboard — kind (queue) management and
/// API key management. Scope <c>admin</c> only.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/kinds", ListKindsAsync).RequireScope(ApiScope.Admin);
        api.MapPost("/kinds", CreateKindAsync).RequireScope(ApiScope.Admin);
        api.MapPatch("/kinds/{name}", PatchKindAsync).RequireScope(ApiScope.Admin);

        api.MapGet("/keys", ListKeysAsync).RequireScope(ApiScope.Admin);
        api.MapPost("/keys", CreateKeyAsync).RequireScope(ApiScope.Admin);
        api.MapPatch("/keys/{id:long}", PatchKeyAsync).RequireScope(ApiScope.Admin);
        api.MapPost("/keys/{id:long}/revoke", RevokeKeyAsync).RequireScope(ApiScope.Admin);
    }

    // ---------- kinds ----------

    private static async Task<IResult> ListKindsAsync(JobStore store, CancellationToken ct)
    {
        var kinds = await store.ListKindsAsync(ct);
        return Results.Ok(kinds.Select(KindView));
    }

    private static async Task<IResult> CreateKindAsync(
        CreateKindRequest? request, JobStore store, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("name is required.");
        }

        if (request.Name.Length > 200)
        {
            return BadRequest("name must be at most 200 characters.");
        }

        if (request.DefaultTtlSeconds is < 1 or > 604800)
        {
            return BadRequest("defaultTtlSeconds must be between 1 and 604800.");
        }

        if (request.DefaultMaxAttempts is < 1 or > 10)
        {
            return BadRequest("defaultMaxAttempts must be between 1 and 10.");
        }

        if (request.DefaultLeaseSeconds is < 1 or > 86400)
        {
            return BadRequest("defaultLeaseSeconds must be between 1 and 86400.");
        }

        if (request.RetentionDays is < 1 or > 3650)
        {
            return BadRequest("retentionDays must be between 1 and 3650.");
        }

        var name = request.Name.Trim();
        var created = await store.CreateKindAsync(
            new JobKind
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                Enabled = request.Enabled ?? true,
                DefaultTtlSeconds = request.DefaultTtlSeconds ?? 86400,
                DefaultMaxAttempts = request.DefaultMaxAttempts ?? 3,
                DefaultLeaseSeconds = request.DefaultLeaseSeconds ?? 1200,
                RetentionDays = request.RetentionDays ?? 90,
            },
            ct);

        if (!created)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Kind already exists",
                detail: $"A kind named '{name}' is already declared.");
        }

        var kind = await store.GetKindAsync(name, ct);
        return Results.Created($"/api/v1/kinds/{Uri.EscapeDataString(name)}", KindView(kind!));
    }

    private static async Task<IResult> PatchKindAsync(
        string name, PatchKindRequest? request, JobStore store, CancellationToken ct)
    {
        if (request?.Enabled is not { } enabled)
        {
            return BadRequest("enabled is required (the only mutable field of a kind).");
        }

        var updated = await store.SetKindEnabledAsync(name, enabled, ct);
        if (!updated)
        {
            return Results.NotFound();
        }

        var kind = await store.GetKindAsync(name, ct);
        return Results.Ok(KindView(kind!));
    }

    // ---------- keys ----------

    private static async Task<IResult> ListKeysAsync(ApiKeyStore store, CancellationToken ct)
    {
        var keys = await store.ListAsync(ct);
        return Results.Ok(keys.Select(KeyView));
    }

    private static async Task<IResult> CreateKeyAsync(
        CreateKeyRequest? request, ApiKeyStore keyStore, JobStore jobStore, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("name is required.");
        }

        if (request.Name.Length > 200)
        {
            return BadRequest("name must be at most 200 characters.");
        }

        if (request.Scope is not (ApiScope.Producer or ApiScope.Worker or ApiScope.Admin))
        {
            return BadRequest($"scope must be one of: {string.Join(", ", ApiScope.All)}.");
        }

        var allowedKinds = request.AllowedKinds?.Distinct().ToArray() ?? [];
        var unknown = await UnknownKindsAsync(allowedKinds, jobStore, ct);
        if (unknown.Length > 0)
        {
            return UnknownKindsProblem(unknown);
        }

        var (record, plaintext) = await keyStore.CreateAsync(request.Name.Trim(), request.Scope, allowedKinds, ct);

        // The clear-text key appears here and nowhere else — it is never retrievable again.
        return Results.Created($"/api/v1/keys/{record.Id}", new
        {
            key = plaintext,
            id = record.Id,
            name = record.Name,
            prefix = record.Prefix,
            scope = record.Scope,
            allowedKinds = record.AllowedKinds,
            createdAt = record.CreatedAt,
        });
    }

    private static async Task<IResult> PatchKeyAsync(
        long id, PatchKeyRequest? request, ApiKeyStore keyStore, JobStore jobStore, CancellationToken ct)
    {
        if (request is null || (request.Name is null && request.AllowedKinds is null))
        {
            return BadRequest("Provide at least one of: name, allowedKinds. The scope is immutable — create a new key instead.");
        }

        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200))
        {
            return BadRequest("name must be non-empty and at most 200 characters.");
        }

        var current = await keyStore.GetAsync(id, ct);
        if (current is null)
        {
            return Results.NotFound();
        }

        if (current.RevokedAt is not null)
        {
            return RevokedKeyProblem();
        }

        var allowedKinds = request.AllowedKinds?.Distinct().ToArray() ?? current.AllowedKinds;
        var unknown = await UnknownKindsAsync(allowedKinds, jobStore, ct);
        if (unknown.Length > 0)
        {
            return UnknownKindsProblem(unknown);
        }

        var updated = await keyStore.UpdateAsync(id, request.Name?.Trim() ?? current.Name, allowedKinds, ct);
        // Keys are never deleted, only revoked — so a null here can only mean the key
        // was revoked between the check above and the update: 409, not 404.
        return updated is null ? RevokedKeyProblem() : Results.Ok(KeyView(updated));
    }

    private static async Task<IResult> RevokeKeyAsync(long id, ApiKeyStore store, CancellationToken ct)
    {
        await store.RevokeAsync(id, ct);
        var key = await store.GetAsync(id, ct);
        // Idempotent: revoking an already-revoked key re-answers 200 with its state.
        return key is null ? Results.NotFound() : Results.Ok(KeyView(key));
    }

    // ---------- helpers ----------

    private static async Task<string[]> UnknownKindsAsync(
        string[] requested, JobStore store, CancellationToken ct)
    {
        if (requested.Length == 0)
        {
            return [];
        }

        var existing = (await store.ListKindsAsync(ct)).Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        return requested.Where(k => !existing.Contains(k)).ToArray();
    }

    private static IResult RevokedKeyProblem() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Key revoked",
            detail: "A revoked key cannot be edited.");

    private static IResult UnknownKindsProblem(string[] unknown) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Unknown kinds",
            detail: $"These kinds are not declared: {string.Join(", ", unknown)}.",
            extensions: new Dictionary<string, object?> { ["unknownKinds"] = unknown });

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);

    private static object KindView(JobKind kind) => new
    {
        name = kind.Name,
        description = kind.Description,
        enabled = kind.Enabled,
        defaultTtlSeconds = kind.DefaultTtlSeconds,
        defaultMaxAttempts = kind.DefaultMaxAttempts,
        defaultLeaseSeconds = kind.DefaultLeaseSeconds,
        retentionDays = kind.RetentionDays,
        createdAt = kind.CreatedAt,
    };

    // Never the hash, never the clear-text key — the prefix is the identifier.
    private static object KeyView(Domain.ApiKeyRecord key) => new
    {
        id = key.Id,
        name = key.Name,
        prefix = key.Prefix,
        scope = key.Scope,
        allowedKinds = key.AllowedKinds,
        createdAt = key.CreatedAt,
        lastUsedAt = key.LastUsedAt,
        revokedAt = key.RevokedAt,
    };
}
