using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Xunit;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-6 review A: PostgreSQL delivers identical notifications of one transaction once, so one notification can stand
// for several queued jobs. A slot woken by it that finds a job wakes the next idle slot; the chain stops at the first slot
// that finds nothing. The timed poll is set far out (30 s) so only wakes can explain a prompt lease.
public sealed class LaneSlotWakeChainTests
{
    private static readonly TimeSpan FarPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OneNotificationForThreeQueuedJobsGetsAllThreeSlotsLeasing()
    {
        var signal = new LaneWakeSignal();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var running = 0;
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        Task<Func<Task>?> Lease(CancellationToken token) => Task.FromResult<Func<Task>?>(queue.TryDequeue(out _)
            ? async () =>
            {
                // Each job holds its slot until all three run at once: only three slots leasing in parallel finish.
                if (Interlocked.Increment(ref running) == 3) allRunning.TrySetResult();
                await allRunning.Task.WaitAsync(stopping.Token);
            }
        : null);
        var slots = Enumerable.Range(0, 3).Select(_ => WorkerService.RunSlotAsync(signal, Lease, _ => { }, stopping.Token, FarPoll, (_, _) => FarPoll)).ToArray();
        await WaitUntilAsync(() => signal.Waiting == 3);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        signal.WakeOne();

        await allRunning.Task.WaitAsync(Patience);
        stopping.Cancel();
        await Task.WhenAll(slots.Select(slot => slot.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    [Fact]
    public async Task TheChainStopsAtTheFirstSlotThatFindsNothing()
    {
        var signal = new LaneWakeSignal();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var polls = new int[4];
        // The jobs hold their slots until the end, so the two jobs go to two different slots.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        Func<CancellationToken, Task<Func<Task>?>> Lease(int slot) => token =>
        {
            Interlocked.Increment(ref polls[slot]);
            return Task.FromResult<Func<Task>?>(queue.TryDequeue(out _) ? () => release.Task.WaitAsync(stopping.Token) : null);
        };
        var slots = Enumerable.Range(0, 4).Select(slot => WorkerService.RunSlotAsync(signal, Lease(slot), _ => { }, stopping.Token, FarPoll, (_, _) => FarPoll)).ToArray();
        await WaitUntilAsync(() => signal.Waiting == 4);
        Assert.All(polls, count => Assert.Equal(1, count));

        queue.Enqueue(1);
        queue.Enqueue(2);
        signal.WakeOne();
        // Two slots woke and leased, the third woke and found nothing; the fourth was never woken.
        await WaitUntilAsync(() => polls.Sum() == 4 + 3);
        await Task.Delay(200);

        Assert.Equal(4 + 3, polls.Sum());
        Assert.Equal(3, polls.Count(count => count == 2));
        Assert.Equal(2, signal.Waiting);
        Assert.True(queue.IsEmpty);
        stopping.Cancel();
        await Task.WhenAll(slots.Select(slot => slot.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The slots did not reach the expected state.");
            await Task.Delay(10);
        }
    }
}

// The same against PostgreSQL: three jobs of one type and priority inserted in one transaction raise one notification,
// and three slots of the lane still lease all three promptly.
[Collection("postgres-integration")]
public sealed class LaneSlotWakeChainPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ThreeJobsInOneTransactionRaiseOneNotificationAndAreAllLeasedPromptly()
    {
        var jobType = $"TEST_WAKE_{Guid.NewGuid():N}"[..40];
        var tenant = ScheduledPolicyIsolationTests.NewTenant("wake-chain");
        var router = new JobWakeRouter([new WorkerLane("test", 3, null, null, [jobType], [])]);
        var signal = router.SignalFor("test");
        var payloads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION");
        await using var listener = new Npgsql.NpgsqlConnection(connectionString);
        await listener.OpenAsync();
        listener.Notification += (_, notification) =>
        {
            if (!notification.Payload.Contains(jobType, StringComparison.Ordinal)) return;
            payloads.Enqueue(notification.Payload);
            router.Route(notification.Payload);
        };
        await using (var listen = new Npgsql.NpgsqlCommand($"LISTEN {IntegrationJobNotifications.Channel}", listener))
            await listen.ExecuteNonQueryAsync();

        var running = 0;
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        async Task<Func<Task>?> Lease(CancellationToken token)
        {
            await using var db = fixture.CreateContext();
            var job = await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider)
                .TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, token, includedJobTypes: [jobType]);
            if (job is null) return null;
            return async () =>
            {
                if (Interlocked.Increment(ref running) == 3) allRunning.TrySetResult();
                await allRunning.Task.WaitAsync(stopping.Token);
            };
        }
        var far = TimeSpan.FromSeconds(30);
        var slots = Enumerable.Range(0, 3).Select(_ => WorkerService.RunSlotAsync(signal, Lease, _ => { }, stopping.Token, far, (_, _) => far)).ToArray();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (signal.Waiting < 3 && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Equal(3, signal.Waiting);

        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.Add(tenant);
            for (var i = 0; i < 3; i++)
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, null, jobType, "{}", $"wake-chain:{i}", "test", DateTimeOffset.UtcNow.AddMinutes(-1)) { Priority = 3 });
            await seed.SaveChangesAsync();

            var listenUntil = DateTime.UtcNow.AddSeconds(5);
            while (!allRunning.Task.IsCompleted && DateTime.UtcNow < listenUntil)
                await listener.WaitAsync(TimeSpan.FromMilliseconds(50));
            await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal([JobWakeRouter.Payload(jobType, 3)], payloads.ToArray());
        }
        finally
        {
            stopping.Cancel();
            await Task.WhenAll(slots.Select(slot => slot.ContinueWith(_ => { }, TaskScheduler.Default)));
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.job_attempts WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }
}
