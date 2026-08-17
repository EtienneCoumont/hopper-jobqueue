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
        // Un job dont expires_at est passé n'est pas distribué même en pending.
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

        // Le balayeur le range ensuite en expired, journalisé actor=system.
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
        // Une clé worker limitée à kind-a ne reçoit jamais un job kind-b, y compris
        // quand elle le demande explicitement et que kind-b est la seule file non vide.
        await fixture.SeedKindAsync("kind-a");
        await fixture.SeedKindAsync("kind-b");
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-a", "kind-b");
        var workerKeyA = await fixture.CreateKeyAsync("worker", "kind-a");

        using var producer = fixture.ClientWithKey(producerKey);
        using var workerA = fixture.ClientWithKey(workerKeyA);

        await producer.EnqueueAsync("b:1", "kind-b");

        // Demande explicite de kind-b : intersection vide avec allowed_kinds -> 403.
        var explicitB = await workerA.ClaimAsync(kinds: ["kind-b"]);
        Assert.Equal(HttpStatusCode.Forbidden, explicitB.StatusCode);

        // Demande par défaut (toutes les files de la clé) : rien, kind-a est vide.
        var defaultClaim = await workerA.ClaimAsync();
        Assert.Equal(HttpStatusCode.NoContent, defaultClaim.StatusCode);

        // Demande mixte : l'intersection ne garde que kind-a, toujours vide.
        var mixed = await workerA.ClaimAsync(kinds: ["kind-a", "kind-b"]);
        Assert.Equal(HttpStatusCode.NoContent, mixed.StatusCode);

        var status = await fixture.DbScalarAsync<string>(
            "select status from jobqueue.jobs where idempotency_key = 'b:1'");
        Assert.Equal("pending", status);
    }

    [Fact]
    public async Task Test9_PausedKind_EnqueueSucceeds_ClaimReturns204()
    {
        // enabled = false : l'enqueue réussit, le claim renvoie 204.
        await fixture.SeedKindAsync("kind-paused", enabled: false);
        var producerKey = await fixture.CreateKeyAsync("producer", "kind-paused");
        var workerKey = await fixture.CreateKeyAsync("worker", "kind-paused");

        using var producer = fixture.ClientWithKey(producerKey);
        using var worker = fixture.ClientWithKey(workerKey);

        var enqueue = await producer.EnqueueAsync("paused:1", "kind-paused");
        Assert.Equal(HttpStatusCode.Created, enqueue.StatusCode);

        var claim = await worker.ClaimAsync();
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        // File réactivée : le job part immédiatement.
        await fixture.DbExecuteAsync("update jobqueue.job_kinds set enabled = true where name = 'kind-paused'");
        var afterResume = await worker.ClaimAsync();
        Assert.Equal(HttpStatusCode.OK, afterResume.StatusCode);
    }

    [Fact]
    public async Task Test10_Fairness_SmallQueueNotStarvedByLargeOne()
    {
        // Deux files, 200 jobs dans la première et 3 dans la seconde. Un worker qui
        // réclame les deux obtient les 3 jobs de la petite file en un petit nombre de
        // claims (sélection aléatoire équilibrée : la borne à 20 rend le test
        // déterministe en pratique ; sans équité il en faudrait ~200).
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
        // Une faute de frappe côté producteur ne crée pas de file fantôme : 400 avec
        // la liste des kinds autorisés pour sa clé.
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
        // GET /jobs/{id} sur un job d'une autre file : 404, jamais 403 — pas d'énumération.
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

        // Le propriétaire, lui, relit état et résultat (c'est son canal de récupération).
        var owner = await producerTheirs.GetAsync($"/api/v1/jobs/{created.JobId()}");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var json = await owner.JsonAsync();
        Assert.Equal("pending", json["status"]!.GetValue<string>());
        Assert.Null(json["leaseToken"]);
    }
}
