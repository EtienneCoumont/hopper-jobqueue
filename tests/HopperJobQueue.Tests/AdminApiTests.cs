using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HopperJobQueue.Tests.Support;

namespace HopperJobQueue.Tests;

[Collection("integration")]
public sealed class AdminApiTests(IntegrationFixture fixture) : IAsyncLifetime
{
    private string _adminKey = "";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _adminKey = await fixture.CreateKeyAsync("admin");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Kinds_CreateListPatch_MirrorsDashboardFeatures()
    {
        using var admin = fixture.ClientWithKey(_adminKey);

        // Create with explicit defaults.
        var create = await admin.PostAsJsonAsync("/api/v1/kinds", new
        {
            name = "api-kind",
            description = "created through the admin API",
            defaultTtlSeconds = 3600,
            defaultMaxAttempts = 5,
            defaultLeaseSeconds = 600,
            retentionDays = 30,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.JsonAsync();
        Assert.True(created["enabled"]!.GetValue<bool>());
        Assert.Equal(5, created["defaultMaxAttempts"]!.GetValue<int>());

        // Duplicate name -> 409.
        var duplicate = await admin.PostAsJsonAsync("/api/v1/kinds", new { name = "api-kind" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Out-of-range default -> 400.
        var invalid = await admin.PostAsJsonAsync("/api/v1/kinds", new { name = "bad", defaultMaxAttempts = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Listed.
        var list = await (await admin.GetAsync("/api/v1/kinds")).JsonAsync();
        Assert.Contains(list.AsArray(), k => k!["name"]!.GetValue<string>() == "api-kind");

        // Pause via PATCH — same lever as the dashboard toggle.
        var patch = await admin.PatchAsJsonAsync("/api/v1/kinds/api-kind", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.False((await patch.JsonAsync())["enabled"]!.GetValue<bool>());

        // Unknown kind -> 404; missing body field -> 400.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync("/api/v1/kinds/nope", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PatchAsJsonAsync("/api/v1/kinds/api-kind", new { })).StatusCode);
    }

    [Fact]
    public async Task Keys_FullLifecycle_CreateUseEditRevoke()
    {
        using var admin = fixture.ClientWithKey(_adminKey);
        await fixture.SeedKindAsync("kind-a");
        await fixture.SeedKindAsync("kind-b");

        // Create a producer key through the API; the clear-text key appears once.
        var create = await admin.PostAsJsonAsync("/api/v1/keys", new
        {
            name = "api-producer",
            scope = "producer",
            allowedKinds = new[] { "kind-a" },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.JsonAsync();
        var plaintext = created["key"]!.GetValue<string>();
        var keyId = created["id"]!.GetValue<long>();
        Assert.StartsWith("hjq_producer_", plaintext);

        // The created key authenticates and respects its allowed kinds.
        using var producer = fixture.ClientWithKey(plaintext);
        Assert.Equal(HttpStatusCode.Created, (await producer.EnqueueAsync("api:1", "kind-a")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await producer.EnqueueAsync("api:2", "kind-b")).StatusCode);

        // The listing exposes the prefix, never the secret or its hash.
        var listBody = await (await admin.GetAsync("/api/v1/keys")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(plaintext, listBody);
        Assert.DoesNotContain("keyHash", listBody);
        Assert.Contains(plaintext[..16], listBody);

        // Edit: rename + widen allowed kinds. Immediately effective.
        var patch = await admin.PatchAsJsonAsync($"/api/v1/keys/{keyId}", new
        {
            name = "api-producer-renamed",
            allowedKinds = new[] { "kind-a", "kind-b" },
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.JsonAsync();
        Assert.Equal("api-producer-renamed", patched["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Created, (await producer.EnqueueAsync("api:3", "kind-b")).StatusCode);

        // Unknown kind in edit -> 400 with the offending names.
        var badPatch = await admin.PatchAsJsonAsync($"/api/v1/keys/{keyId}", new { allowedKinds = new[] { "ghost" } });
        Assert.Equal(HttpStatusCode.BadRequest, badPatch.StatusCode);
        Assert.Equal("ghost", (await badPatch.JsonAsync())["unknownKinds"]![0]!.GetValue<string>());

        // Revoke: idempotent 200, the key stops authenticating, editing it is refused.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/v1/keys/{keyId}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/v1/keys/{keyId}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await producer.EnqueueAsync("api:4", "kind-a")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PatchAsJsonAsync($"/api/v1/keys/{keyId}", new { name = "zombie" })).StatusCode);

        // Unknown id -> 404.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync("/api/v1/keys/999999", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task AdminManagementRoutes_Reject_ProducerAndWorkerScopes()
    {
        await fixture.SeedKindAsync("kind-scope2");
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-scope2");
        var workerKey = await fixture.CreateKeyAsync("worker", "kind-scope2");

        foreach (var key in new[] { producerKey, workerKey })
        {
            using var client = fixture.ClientWithKey(key);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/kinds")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/v1/kinds", new { name = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/keys")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/v1/keys", new { name = "x", scope = "admin" })).StatusCode);
        }
    }
}
