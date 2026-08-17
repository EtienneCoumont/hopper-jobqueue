using System.Net;
using System.Net.Http.Json;
using HopperJobQueue.Tests.Support;

namespace HopperJobQueue.Tests;

[Collection("integration")]
public sealed class ScopeIsolationTests(IntegrationFixture fixture) : IAsyncLifetime
{
    private const string Kind = "kind-scope";
    private string _producerKey = "";
    private string _workerKey = "";
    private string _adminKey = "";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.SeedKindAsync(Kind);
        _producerKey = await fixture.CreateKeyAsync("producer", Kind);
        _workerKey = await fixture.CreateKeyAsync("worker", Kind);
        _adminKey = await fixture.CreateKeyAsync("admin");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Test6_EachScope_Gets403_OnOtherScopesRoutes()
    {
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        // Un producteur sur les routes worker : 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await producer.ClaimAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await producer.HeartbeatAsync(1, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await producer.CompleteAsync(1, Guid.NewGuid(), "success")).StatusCode);

        // Un worker sur les routes producer : 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.EnqueueAsync("scope:1", Kind)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/jobs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/jobs/by-key/x")).StatusCode);

        // Producer et worker sur les routes admin : 403.
        foreach (var client in new[] { producer, worker })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/jobs?status=pending")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/stats")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/v1/jobs/1/requeue", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/v1/jobs/1/cancel", new { })).StatusCode);
        }
    }

    [Fact]
    public async Task AdminScope_HasFullAccess()
    {
        // « admin (tout + dashboard) » : le scope admin passe sur les routes des autres.
        using var admin = fixture.ClientWithKey(_adminKey);

        Assert.Equal(HttpStatusCode.Created, (await admin.EnqueueAsync("admin:1", Kind)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.ClaimAsync(workerId: "admin-probe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/jobs?status=leased")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/stats")).StatusCode);
    }

    [Fact]
    public async Task MissingOrInvalidKey_Gets401()
    {
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.ClaimAsync()).StatusCode);

        using var invalid = fixture.ClientWithKey("hjq_worker_0000000000000000000000000000nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.ClaimAsync()).StatusCode);
    }
}
