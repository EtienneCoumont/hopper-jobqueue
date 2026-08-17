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
        // Un job claim puis abandonné redevient claimable après expiration du bail,
        // avec attempts correctement incrémenté.
        using var producer = fixture.ClientWithKey(_producerKey);
        using var worker = fixture.ClientWithKey(_workerKey);

        await producer.EnqueueAsync("lease:1", Kind);

        var first = await worker.ClaimAsync(workerId: "worker-a");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.JsonAsync();
        var jobId = firstJson.JobId();
        Assert.Equal(1, firstJson["attempts"]!.GetValue<int>());

        // Bail encore actif : personne d'autre ne peut le prendre.
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
        // Worker A claim, son bail expire, worker B claim, puis A tente un complete :
        // 409, et le job de B n'est pas altéré.
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

        // Le job de B est intact : toujours leased pour worker-b, sans résultat écrit.
        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal("leased", status);
        var workerId = await fixture.DbScalarAsync<string>(
            "select worker_id from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal("worker-b", workerId);
        var hasResult = await fixture.DbScalarAsync<bool>(
            "select result is not null from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.False(hasResult);

        // Un heartbeat avec le token périmé est rejeté avec le même 409 explicite.
        var staleHeartbeat = await worker.HeartbeatAsync(jobId, tokenA);
        Assert.Equal(HttpStatusCode.Conflict, staleHeartbeat.StatusCode);

        // B, lui, termine normalement.
        var completeB = await worker.CompleteAsync(jobId, tokenB, "success", new { report = "ok" });
        Assert.Equal(HttpStatusCode.OK, completeB.StatusCode);
        Assert.Equal("done", (await completeB.JsonAsync())["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test5_PoisonMessage_EndsFailed_NeverDistributedAgain()
    {
        // Un job claim et abandonné max_attempts fois finit en failed et n'est
        // plus jamais distribué.
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

        // Tentatives épuisées : plus jamais distribué, même bail expiré.
        var exhausted = await worker.ClaimAsync(workerId: "crashy-worker");
        Assert.Equal(HttpStatusCode.NoContent, exhausted.StatusCode);

        // Le balayeur le passe en failed avec le message convenu.
        await fixture.Factory.Services.GetRequiredService<SweeperService>().RunOnceAsync();

        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'poison:1'");
        Assert.Equal("failed", status);
        var lastError = await fixture.DbScalarAsync<string>(
            "select last_error from jobqueue.jobs where idempotency_key = 'poison:1'");
        Assert.Equal("bail expiré, tentatives épuisées", lastError);

        var afterSweep = await worker.ClaimAsync(workerId: "crashy-worker");
        Assert.Equal(HttpStatusCode.NoContent, afterSweep.StatusCode);
    }

    [Fact]
    public async Task Complete_IsIdempotent_ReplayDoesNotRewrite()
    {
        // Rejouer le même complete avec le même token renvoie 200 sans réécrire.
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

        // Le résultat d'origine n'a pas été écrasé par le rejeu.
        var value = await fixture.DbScalarAsync<int>(
            "select (result->>'value')::int from jobqueue.jobs where id = @Id", new { Id = jobId });
        Assert.Equal(1, value);

        // Un seul évènement leased -> done dans la piste d'audit.
        var doneEvents = await fixture.DbScalarAsync<long>(
            "select count(*) from jobqueue.job_events where job_id = @Id and to_status = 'done'",
            new { Id = jobId });
        Assert.Equal(1, doneEvents);
    }

    [Fact]
    public async Task Complete_Failure_RetriesThenFails()
    {
        // complete(erreur) : retour en pending tant qu'il reste des tentatives,
        // failed quand elles sont épuisées.
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
