using System.Net;
using HopperJobQueue.Tests.Support;

namespace HopperJobQueue.Tests;

[Collection("integration")]
public sealed class ConcurrencyTests(IntegrationFixture fixture) : IAsyncLifetime
{
    private const string Kind = "kind-conc";
    private string _producerKey = "";
    private string _workerKey = "";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.SeedKindAsync(Kind);
        _producerKey = await fixture.CreateKeyAsync("producer", Kind);
        _workerKey = await fixture.CreateKeyAsync("worker", Kind);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Test1_ConcurrentClaims_NoJobDistributedTwice()
    {
        // 20 parallel claims over 5 jobs: exactly 5 succeed, no job distributed
        // twice, 15 responses are 204. The test that justifies the project.
        using var producer = fixture.ClientWithKey(_producerKey);
        for (var i = 0; i < 5; i++)
        {
            var enqueue = await producer.EnqueueAsync($"conc:{i}", Kind);
            Assert.Equal(HttpStatusCode.Created, enqueue.StatusCode);
        }

        using var worker = fixture.ClientWithKey(_workerKey);
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => worker.ClaimAsync(workerId: $"w{i}")));

        var claimed = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var empty = responses.Where(r => r.StatusCode == HttpStatusCode.NoContent).ToList();

        Assert.Equal(5, claimed.Count);
        Assert.Equal(15, empty.Count);

        var jobIds = new List<long>();
        foreach (var response in claimed)
        {
            jobIds.Add((await response.JsonAsync()).JobId());
        }

        Assert.Equal(5, jobIds.Distinct().Count());

        var leasedInDb = await fixture.DbScalarAsync<long>(
            "select count(*) from jobqueue.jobs where status = 'leased' and attempts = 1");
        Assert.Equal(5, leasedInDb);
    }

    [Fact]
    public async Task Test2_ConcurrentEnqueues_SingleJobCreated()
    {
        // 10 simultaneous POSTs with the same idempotency key: a single job
        // created, all 10 responses consistent.
        using var producer = fixture.ClientWithKey(_producerKey);
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => producer.EnqueueAsync("same-key", Kind, new { n = 1 })));

        Assert.All(responses, r =>
            Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));

        var ids = new List<long>();
        var createdFlags = new List<bool>();
        foreach (var response in responses)
        {
            var json = await response.JsonAsync();
            ids.Add(json.JobId());
            createdFlags.Add(json["created"]!.GetValue<bool>());
        }

        Assert.Single(ids.Distinct());
        Assert.Equal(1, createdFlags.Count(created => created));

        var total = await fixture.DbScalarAsync<long>("select count(*) from jobqueue.jobs");
        Assert.Equal(1, total);
    }
}
