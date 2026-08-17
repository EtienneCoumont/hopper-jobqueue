using System.Net;
using HopperJobQueue.Api.Maintenance;
using HopperJobQueue.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace HopperJobQueue.Tests;

[Collection("integration")]
public sealed class QueueBehaviorTests(IntegrationFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Test7_ExpiredJob_NeverDistributed_EvenPending()
    {
        // A job whose expires_at has passed is not distributed even when pending.
        await fixture.SeedKindAsync("kind-ttl");
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-ttl");
        var workerKey = await fixture.CreateKeyAsync("worker", "kind-ttl");

        using var producer = fixture.ClientWithKey(producerKey);
        using var worker = fixture.ClientWithKey(workerKey);

        await producer.EnqueueAsync("ttl:1", "kind-ttl");
        await fixture.DbExecuteAsync(
            "update jobqueue.jobs set expires_at = now() - interval '1 second' where idempotency_key = 'ttl:1'");

        var claim = await worker.ClaimAsync();
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'ttl:1'");
        Assert.Equal("pending", status);

        // The sweeper then files it as expired, journaled with actor=system.
        await fixture.Factory.Services.GetRequiredService<SweeperService>().RunOnceAsync();
        status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'ttl:1'");
        Assert.Equal("expired", status);
        var actor = await fixture.DbScalarAsync<string>(
            """
            select actor from jobqueue.job_events e
            join jobqueue.jobs j on j.id = e.job_id
            where j.idempotency_key = 'ttl:1' and e.to_status = 'expired'
            """);
        Assert.Equal("system", actor);
    }

    [Fact]
    public async Task Test8_WorkerLimitedToKindA_NeverReceivesKindB()
    {
        // A worker key limited to kind-a never receives a kind-b job, including
        // when it asks explicitly and kind-b is the only non-empty queue.
        await fixture.SeedKindAsync("kind-a");
        await fixture.SeedKindAsync("kind-b");
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-a", "kind-b");
        var workerKeyA = await fixture.CreateKeyAsync("worker", "kind-a");

        using var producer = fixture.ClientWithKey(producerKey);
        using var workerA = fixture.ClientWithKey(workerKeyA);

        await producer.EnqueueAsync("b:1", "kind-b");

        // Explicit request for kind-b: empty intersection with allowed_kinds -> 403.
        var explicitB = await workerA.ClaimAsync(kinds: ["kind-b"]);
        Assert.Equal(HttpStatusCode.Forbidden, explicitB.StatusCode);

        // Default request (all the key's queues): nothing, kind-a is empty.
        var defaultClaim = await workerA.ClaimAsync();
        Assert.Equal(HttpStatusCode.NoContent, defaultClaim.StatusCode);

        // Mixed request: the intersection keeps only kind-a, still empty.
        var mixed = await workerA.ClaimAsync(kinds: ["kind-a", "kind-b"]);
        Assert.Equal(HttpStatusCode.NoContent, mixed.StatusCode);

        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'b:1'");
        Assert.Equal("pending", status);
    }

    [Fact]
    public async Task Test9_PausedKind_EnqueueSucceeds_ClaimReturns204()
    {
        // enabled = false: the enqueue succeeds, the claim returns 204.
        await fixture.SeedKindAsync("kind-paused", enabled: false);
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-paused");
        var workerKey = await fixture.CreateKeyAsync("worker", "kind-paused");

        using var producer = fixture.ClientWithKey(producerKey);
        using var worker = fixture.ClientWithKey(workerKey);

        var enqueue = await producer.EnqueueAsync("paused:1", "kind-paused");
        Assert.Equal(HttpStatusCode.Created, enqueue.StatusCode);

        var claim = await worker.ClaimAsync();
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        // Queue re-enabled: the job leaves immediately.
        await fixture.DbExecuteAsync("update jobqueue.job_kinds set enabled = true where name = 'kind-paused'");
        var afterResume = await worker.ClaimAsync();
        Assert.Equal(HttpStatusCode.OK, afterResume.StatusCode);
    }

    [Fact]
    public async Task Test10_Fairness_SmallQueueNotStarvedByLargeOne()
    {
        // Two queues, 200 jobs in the first and 3 in the second. A worker claiming
        // both gets the 3 jobs of the small queue in a small number of claims
        // (balanced random selection: the bound of 20 makes the test deterministic
        // in practice; without fairness it would take ~200).
        await fixture.SeedKindAsync("kind-big");
        await fixture.SeedKindAsync("kind-small");
        var workerKey = await fixture.CreateKeyAsync("worker", "kind-big", "kind-small");

        await fixture.DbExecuteAsync(
            """
            insert into jobqueue.jobs (idempotency_key, kind, payload, expires_at)
            select 'big:' || g, 'kind-big', '{}'::jsonb, now() + interval '1 day'
            from generate_series(1, 200) g
            """);
        await fixture.DbExecuteAsync(
            """
            insert into jobqueue.jobs (idempotency_key, kind, payload, expires_at)
            select 'small:' || g, 'kind-small', '{}'::jsonb, now() + interval '1 day'
            from generate_series(1, 3) g
            """);

        using var worker = fixture.ClientWithKey(workerKey);
        var smallReceived = 0;
        var claims = 0;
        for (; claims < 20 && smallReceived < 3; claims++)
        {
            var claim = await worker.ClaimAsync(workerId: "fair-worker", leaseSeconds: 3600);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var kind = (await claim.JsonAsync())["kind"]!.GetValue<string>();
            if (kind == "kind-small")
            {
                smallReceived++;
            }
        }

        Assert.Equal(3, smallReceived);
        Assert.True(claims <= 20, $"took {claims} claims to drain the small queue");
    }

    [Fact]
    public async Task UnknownKind_Returns400_WithAllowedKindsList()
    {
        // A typo on the producer side does not create a ghost queue: 400 with the
        // list of kinds allowed for its key.
        await fixture.SeedKindAsync("kind-known");
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-known");
        using var producer = fixture.ClientWithKey(producerKey);

        var response = await producer.EnqueueAsync("typo:1", "kind-knwon");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.Equal("kind-known", json["allowedKinds"]![0]!.GetValue<string>());

        var total = await fixture.DbScalarAsync<long>("select count(*) from jobqueue.jobs");
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task ProducerRead_OtherKind_Returns404_NeverLeaks()
    {
        // GET /jobs/{id} on a job from another queue: 404, never 403 — no enumeration.
        await fixture.SeedKindAsync("kind-mine");
        await fixture.SeedKindAsync("kind-theirs");
        var mine = await fixture.CreateKeyAsync("producer", "kind-mine");
        var theirs = await fixture.CreateKeyAsync("producer", "kind-theirs");

        using var producerTheirs = fixture.ClientWithKey(theirs);
        var created = await (await producerTheirs.EnqueueAsync("theirs:1", "kind-theirs")).JsonAsync();

        using var producerMine = fixture.ClientWithKey(mine);
        var response = await producerMine.GetAsync($"/api/v1/jobs/{created.JobId()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var byKey = await producerMine.GetAsync("/api/v1/jobs/by-key/theirs:1");
        Assert.Equal(HttpStatusCode.NotFound, byKey.StatusCode);

        // The owner, though, re-reads state and result (its retrieval channel).
        var owner = await producerTheirs.GetAsync($"/api/v1/jobs/{created.JobId()}");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var json = await owner.JsonAsync();
        Assert.Equal("pending", json["status"]!.GetValue<string>());
        Assert.Null(json["leaseToken"]);
    }
}
