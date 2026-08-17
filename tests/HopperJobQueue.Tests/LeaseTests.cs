using System.Net;
using HopperJobQueue.Api.Maintenance;
using HopperJobQueue.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace HopperJobQueue.Tests;

[Collection("integration")]
public sealed class LeaseTests(IntegrationFixture fixture) : IAsyncLifetime
{
    private const string Kind = "kind-lease";
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

    private async Task ExpireLeaseAsync(long jobId) =>
        await fixture.DbExecuteAsync(
            "update jobqueue.jobs set lease_until = now() - interval '1 second' where id = @Id",
            new { Id = jobId });

    [Fact]
    public async Task Test3_ExpiredLease_JobClaimableAgain_AttemptsIncremented()
    {
        // A claimed then abandoned job becomes claimable again after the lease
        // expires, with attempts correctly incremented.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("lease:1", Kind);

        var first = await worker.ClaimAsync(workerId: "worker-a");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.JsonAsync();
        var jobId = firstJson.JobId();
        Assert.Equal(1, firstJson["attempts"]!.GetValue<int>());

        // Lease still active: nobody else can take it.
        var whileLeased = await worker.ClaimAsync(workerId: "worker-b");
        Assert.Equal(HttpStatusCode.NoContent, whileLeased.StatusCode);

        await ExpireLeaseAsync(jobId);

        var second = await worker.ClaimAsync(workerId: "worker-b");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondJson = await second.JsonAsync();
        Assert.Equal(jobId, secondJson.JobId());
        Assert.Equal(2, secondJson["attempts"]!.GetValue<int>());
        Assert.Equal("worker-b", secondJson["workerId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test4_StaleLeaseToken_CompleteRejected_OtherWorkerUnaffected()
    {
        // Worker A claims, its lease expires, worker B claims, then A attempts a
        // complete: 409, and B's job is not altered.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("stale:1", Kind);

        var claimA = await (await worker.ClaimAsync(workerId: "worker-a")).JsonAsync();
        var jobId = claimA.JobId();
        var tokenA = claimA.LeaseToken();

        await ExpireLeaseAsync(jobId);

        var claimB = await (await worker.ClaimAsync(workerId: "worker-b")).JsonAsync();
        Assert.Equal(jobId, claimB.JobId());
        var tokenB = claimB.LeaseToken();
        Assert.NotEqual(tokenA, tokenB);

        var staleComplete = await worker.CompleteAsync(jobId, tokenA, "success", new { report = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, staleComplete.StatusCode);

        // B's job is intact: still leased by worker-b, no result written.
        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal("leased", status);
        var workerId = await fixture.DbScalarAsync<string>(
            "select worker_id from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal("worker-b", workerId);
        var hasResult = await fixture.DbScalarAsync<bool>(
            "select result is not null from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.False(hasResult);

        // A heartbeat with the stale token is rejected with the same explicit 409.
        var staleHeartbeat = await worker.HeartbeatAsync(jobId, tokenA);
        Assert.Equal(HttpStatusCode.Conflict, staleHeartbeat.StatusCode);

        // B, on the other hand, completes normally.
        var completeB = await worker.CompleteAsync(jobId, tokenB, "success", new { report = "ok" });
        Assert.Equal(HttpStatusCode.OK, completeB.StatusCode);
        Assert.Equal("done", (await completeB.JsonAsync())["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test5_PoisonMessage_EndsFailed_NeverDistributedAgain()
    {
        // A job claimed and abandoned max_attempts times ends in failed and is
        // never distributed again.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("poison:1", Kind, maxAttempts: 2);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var claim = await worker.ClaimAsync(workerId: "crashy-worker");
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var json = await claim.JsonAsync();
            Assert.Equal(attempt, json["attempts"]!.GetValue<int>());
            await ExpireLeaseAsync(json.JobId());
        }

        // Attempts exhausted: never distributed again, even with an expired lease.
        var exhausted = await worker.ClaimAsync(workerId: "crashy-worker");
        Assert.Equal(HttpStatusCode.NoContent, exhausted.StatusCode);

        // The sweeper moves it to failed with the agreed message.
        await fixture.Factory.Services.GetRequiredService<SweeperService>().RunOnceAsync();

        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'poison:1'");
        Assert.Equal("failed", status);
        var lastError = await fixture.DbScalarAsync<string>(
            "select last_error from jobqueue.jobs where idempotency_key = 'poison:1'");
        Assert.Equal("lease expired, attempts exhausted", lastError);

        var afterSweep = await worker.ClaimAsync(workerId: "crashy-worker");
        Assert.Equal(HttpStatusCode.NoContent, afterSweep.StatusCode);
    }

    [Fact]
    public async Task Complete_IsIdempotent_ReplayDoesNotRewrite()
    {
        // Replaying the same complete with the same token returns 200 without rewriting.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("replay:1", Kind);
        var claim = await (await worker.ClaimAsync()).JsonAsync();
        var jobId = claim.JobId();
        var token = claim.LeaseToken();

        var first = await worker.CompleteAsync(jobId, token, "success", new { value = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await worker.CompleteAsync(jobId, token, "success", new { value = 999 });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("done", (await replay.JsonAsync())["status"]!.GetValue<string>());

        // The original result was not overwritten by the replay.
        var value = await fixture.DbScalarAsync<int>(
            "select (result->>'value')::int from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal(1, value);

        // A single leased -> done event in the audit trail.
        var doneEvents = await fixture.DbScalarAsync<long>(
            "select count(*) from jobqueue.job_events where job_id = @Id and to_status = 'done'",
            new { Id = jobId });
        Assert.Equal(1, doneEvents);
    }

    [Fact]
    public async Task Complete_Failure_RetriesThenFails()
    {
        // complete(error): back to pending while attempts remain, failed once
        // they are exhausted.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("retry:1", Kind, maxAttempts: 2);

        var claim1 = await (await worker.ClaimAsync()).JsonAsync();
        var fail1 = await worker.CompleteAsync(claim1.JobId(), claim1.LeaseToken(), "failure", error: "boom 1");
        Assert.Equal("pending", (await fail1.JsonAsync())["status"]!.GetValue<string>());

        var claim2 = await (await worker.ClaimAsync()).JsonAsync();
        Assert.Equal(claim1.JobId(), claim2.JobId());
        var fail2 = await worker.CompleteAsync(claim2.JobId(), claim2.LeaseToken(), "failure", error: "boom 2");
        Assert.Equal("failed", (await fail2.JsonAsync())["status"]!.GetValue<string>());

        var lastError = await fixture.DbScalarAsync<string>(
            "select last_error from jobqueue.jobs where id = @Id", new { Id = claim1.JobId() });
        Assert.Equal("boom 2", lastError);
    }
}
