using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-6: a job notification wakes one idle slot of each lane that can lease the job, not every slot of every lane.
public sealed class LaneWakeSignalTests
{
    private static JobWakeRouter Router() => new(WorkerLanes.All());

    [Fact]
    public void ANotificationForOneLaneDoesNotWakeAnotherLanesSlots()
    {
        var router = Router();
        var returns = router.SignalFor("returns-read");
        var pazarama = router.SignalFor("pazarama-orders");
        var returnSlot = returns.Register();
        var pazaramaSlot = pazarama.Register();

        router.Route(JobWakeRouter.Payload(MarketplaceJobTypes.ReturnSync, 5));

        Assert.True(returnSlot.Woken.IsCompleted);
        Assert.False(pazaramaSlot.Woken.IsCompleted);
    }

    [Fact]
    public void OnlyOneSlotOfTheLaneWakesAndAnUnusedWakeIsPassedOn()
    {
        var router = Router();
        var hot = router.SignalFor("hot");
        var first = hot.Register();
        var second = hot.Register();

        router.Route(JobWakeRouter.Payload(MarketplaceJobTypes.OrderSync, 1));

        Assert.True(first.Woken.IsCompleted);
        Assert.False(second.Woken.IsCompleted);
        // The woken slot found a job of its own in the meantime: the next idle slot gets the wake.
        hot.Leave(first, forward: true);
        Assert.True(second.Woken.IsCompleted);
    }

    [Fact]
    public void AJobSeveralLanesCanLeaseWakesOneSlotOfEach()
    {
        var router = Router();
        // A user action on a return read: the user lane (any type at that priority) and the returns lane take it.
        var user = router.SignalFor("user").Register();
        var returns = router.SignalFor("returns-read").Register();
        var hot = router.SignalFor("hot").Register();

        router.Route(JobWakeRouter.Payload(MarketplaceJobTypes.ReturnSync, JobPriorityPolicy.UserAction));

        Assert.True(user.Woken.IsCompleted);
        Assert.True(returns.Woken.IsCompleted);
        Assert.False(hot.Woken.IsCompleted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("job:")]
    [InlineData("job:ORDER_SYNC")]
    [InlineData("job:ORDER_SYNC:high")]
    [InlineData("something-else")]
    public void AnOldOrUnknownPayloadWakesEverySlot(string? payload)
    {
        var router = Router();
        var slots = WorkerLanes.All().SelectMany(lane => new[] { router.SignalFor(lane.Name).Register(), router.SignalFor(lane.Name).Register() }).ToList();

        router.Route(payload);

        Assert.All(slots, slot => Assert.True(slot.Woken.IsCompleted));
    }

    [Fact]
    public void AFreedLockGroupWakesOneSlotOfEveryLane()
    {
        var router = Router();
        var pairs = WorkerLanes.All().Select(lane => (First: router.SignalFor(lane.Name).Register(), Second: router.SignalFor(lane.Name).Register())).ToList();

        router.Route(JobWakeRouter.Freed);

        Assert.All(pairs, pair =>
        {
            Assert.True(pair.First.Woken.IsCompleted);
            Assert.False(pair.Second.Woken.IsCompleted);
        });
    }

    [Fact]
    public void TheRouterWakesExactlyTheLanesThatTakeTheJob()
    {
        var lanes = WorkerLanes.All();
        foreach (var jobType in new[] { MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.ShipmentAction })
            foreach (var priority in new[] { 0, 1, 2, 3, 5, 9, JobPriorityPolicy.UserAction, JobPriorityPolicy.BulkUserAction, JobPriorityPolicy.InitialDataSync })
            {
                var router = new JobWakeRouter(lanes);
                var tickets = lanes.ToDictionary(lane => lane.Name, lane => router.SignalFor(lane.Name).Register());
                router.Route(JobWakeRouter.Payload(jobType, priority));
                foreach (var lane in lanes)
                    Assert.Equal(lane.Takes(jobType, priority), tickets[lane.Name].Woken.IsCompleted);
            }
    }

    [Fact]
    public void ASlotThatLeftIsNotWokenAndTheWakeGoesToTheNext()
    {
        var signal = new LaneWakeSignal();
        var gone = signal.Register();
        var waiting = signal.Register();
        signal.Leave(gone, forward: false);

        signal.WakeOne();

        Assert.False(gone.Woken.IsCompleted);
        Assert.True(waiting.Woken.IsCompleted);
        Assert.Equal(0, signal.Waiting);
    }

    [Fact]
    public void ParseReadsTheTypeAndPriority()
    {
        Assert.Equal(("ORDER_SYNC", 2), JobWakeRouter.Parse("job:ORDER_SYNC:2"));
        Assert.Equal(("A:B", -1), JobWakeRouter.Parse("job:A:B:-1"));
        Assert.Null(JobWakeRouter.Parse("freed"));
    }
}

// KUYRUK-6: the trigger (migration 20261116000405) names the runnable job and says "freed" when a leased job ends.
[Collection("postgres-integration")]
public sealed class JobNotificationPayloadPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TheTriggerNamesTheRunnableJobAndAFreedLockGroup()
    {
        var tenant = ScheduledPolicyIsolationTests.NewTenant("notify-payload");
        var jobId = Guid.CreateVersion7();
        var payloads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var listener = new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
        await listener.OpenAsync();
        listener.Notification += (_, notification) => payloads.Enqueue(notification.Payload);
        await using (var listen = new Npgsql.NpgsqlCommand($"LISTEN {MarketplaceHub.Infrastructure.Persistence.IntegrationJobNotifications.Channel}", listener))
            await listen.ExecuteNonQueryAsync();
        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            JobEnqueuer.Add(db, new IntegrationJobRequest(tenant.Id, null, MarketplaceJobTypes.ReturnSync, "{}", $"notify-payload:{jobId:N}", "test", DateTimeOffset.UtcNow.AddMinutes(-1)) { Id = jobId, Priority = 4 });
            await db.SaveChangesAsync();
            Assert.Contains(JobWakeRouter.Payload(MarketplaceJobTypes.ReturnSync, 4), await NextAsync(listener, payloads));

            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE integration.jobs SET \"Status\" = 'LEASED' WHERE \"Id\" = {jobId}");
            await NextAsync(listener, payloads, TimeSpan.FromMilliseconds(300));
            payloads.Clear();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE integration.jobs SET \"Status\" = 'SUCCEEDED' WHERE \"Id\" = {jobId}");
            Assert.Equal([JobWakeRouter.Freed], await NextAsync(listener, payloads));
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"Id\" = {jobId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }

    private static async Task<List<string>> NextAsync(Npgsql.NpgsqlConnection listener, System.Collections.Concurrent.ConcurrentQueue<string> payloads, TimeSpan? patience = null)
    {
        var until = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(10));
        while (payloads.IsEmpty && DateTime.UtcNow < until)
            await listener.WaitAsync(TimeSpan.FromMilliseconds(100));
        return [.. payloads];
    }
}
