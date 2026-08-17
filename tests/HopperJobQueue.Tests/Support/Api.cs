using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace HopperJobQueue.Tests.Support;

/// <summary>Petits raccourcis HTTP + JSON pour garder les tests lisibles.</summary>
public static class Api
{
    public static async Task<HttpResponseMessage> EnqueueAsync(
        this HttpClient client, string idempotencyKey, string kind, object? payload = null,
        string? project = null, int? ttlSeconds = null, int? maxAttempts = null)
    {
        return await client.PostAsJsonAsync("/api/v1/jobs", new
        {
            idempotencyKey,
            kind,
            project,
            payload = payload ?? new { },
            ttlSeconds,
            maxAttempts,
        });
    }

    public static async Task<HttpResponseMessage> ClaimAsync(
        this HttpClient client, string workerId = "test-worker", int? leaseSeconds = null, string[]? kinds = null)
    {
        return await client.PostAsJsonAsync("/api/v1/jobs/claim", new { workerId, leaseSeconds, kinds });
    }

    public static async Task<HttpResponseMessage> HeartbeatAsync(
        this HttpClient client, long jobId, Guid leaseToken, int? leaseSeconds = null)
    {
        return await client.PostAsJsonAsync($"/api/v1/jobs/{jobId}/heartbeat", new { leaseToken, leaseSeconds });
    }

    public static async Task<HttpResponseMessage> CompleteAsync(
        this HttpClient client, long jobId, Guid leaseToken, string outcome, object? result = null, string? error = null)
    {
        return await client.PostAsJsonAsync($"/api/v1/jobs/{jobId}/complete", new { leaseToken, outcome, result, error });
    }

    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"Empty body (status {(int)response.StatusCode})");
    }

    public static long JobId(this JsonNode node) => node["id"]!.GetValue<long>();

    public static Guid LeaseToken(this JsonNode node) => node["leaseToken"]!.GetValue<Guid>();
}
