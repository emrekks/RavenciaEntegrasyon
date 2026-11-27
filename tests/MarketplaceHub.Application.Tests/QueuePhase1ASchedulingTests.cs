using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Queue phase 1A (27 Nov 2026): when the scheduler creates jobs. Item 1: a connection's daily reference lists one after the
// other, one tenant read at a time. Item 2: each connection's daily reference read at its own stable slot; the background
// reads due at a worker start spread over a few minutes, order reads and invoice jobs at once. Item 3: no new background
// poll while the connection's marketplace is failing, queued at once after. Item 4: one bell alert per connection, job
// type, error code and day. Each behaviour has its switch; the switches off give the earlier behaviour.
public sealed class QueuePhase1ASchedulingTests
{
    private static readonly Guid A = Guid.Parse("0193a6e0-0000-7000-8000-0000000f1a01");
    private static readonly Guid B = Guid.Parse("0193a6e0-0000-7000-8000-0000000f1a02");

    [Fact]
    public void A_connection_keeps_its_reference_slot_on_every_day_and_process()
    {
        var window = ReferenceReadSchedulePolicy.DefaultWindowLength;
        var start = ReferenceReadSchedulePolicy.DefaultWindowStart;
        var offset = ReferenceReadSchedulePolicy.Offset(A, window);
        Assert.Equal(offset, ReferenceReadSchedulePolicy.Offset(A, window));
        Assert.InRange(offset, TimeSpan.Zero, window - TimeSpan.FromSeconds(1));
        Assert.NotEqual(offset, ReferenceReadSchedulePolicy.Offset(B, window));

        // The same slot whenever it is asked during the day, a day apart on the next day, inside 06:00-10:00 Istanbul.
        var day = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TurkeyClock.Offset);
        var slot = day + start + offset;
        Assert.Equal(slot.ToUniversalTime(), ReferenceReadSchedulePolicy.LatestSlot(slot, A, start, window));
        Assert.Equal(slot.ToUniversalTime(), ReferenceReadSchedulePolicy.LatestSlot(day.AddHours(23), A, start, window));
        Assert.Equal(slot.AddDays(-1).ToUniversalTime(), ReferenceReadSchedulePolicy.LatestSlot(slot.AddSeconds(-1), A, start, window));
        Assert.Equal(slot.AddDays(1).ToUniversalTime(), ReferenceReadSchedulePolicy.LatestSlot(slot.AddDays(1).AddMinutes(5), A, start, window));
        var local = slot.ToOffset(TurkeyClock.Offset).TimeOfDay;
        Assert.InRange(local, TimeSpan.FromHours(6), TimeSpan.FromHours(10));

        // Due once per slot; yesterday's read (a few minutes after yesterday's slot) is stale at today's slot.
        Assert.True(ReferenceReadSchedulePolicy.IsDue(null, slot, 86_400, slot));
        Assert.True(ReferenceReadSchedulePolicy.IsDue(slot.AddDays(-1).AddMinutes(1), slot, 86_400, slot.AddSeconds(1)));
        Assert.False(ReferenceReadSchedulePolicy.IsDue(slot.AddSeconds(1), slot, 86_400, slot.AddMinutes(1)));
        var fetched = new Dictionary<string, DateTimeOffset> { ["CATEGORIES"] = slot.AddDays(-1).AddMinutes(3), ["BRANDS"] = slot.AddHours(-2) };
        Assert.Equal(["CATEGORIES"], ReferenceReadSchedulePolicy.StaleResources(["CATEGORIES", "BRANDS"], fetched, slot, 86_400));
        Assert.Equal(ReferenceReadSchedulePolicy.Bucket(slot) + 1, ReferenceReadSchedulePolicy.Bucket(slot.AddDays(1)));
    }

    [Fact]
    public void The_startup_spread_leaves_order_reads_invoice_jobs_and_writes_at_once()
    {
        const int ceiling = 2;
        bool Spreads(string jobType) => StartupSpreadPolicy.Spreads(jobType, JobPriorityPolicy.For(jobType, userInitiated: false), ceiling,
            JobTypeRegistry.IsOrderReadSync(jobType), JobTypeRegistry.WritingJobTypes.Contains(jobType));
        foreach (var urgent in new[]
                 {
                     MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.HepsiburadaOrderRecoverySync, MarketplaceJobTypes.PazaramaOrderStatusSync,
                     MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.OrderInvoiceReconciliation, MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ReturnSync,
                     InvoicingJobTypes.InvoiceDueScan, InvoicingJobTypes.InvoiceSubmit, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.ProductUpdate
                 })
            Assert.False(Spreads(urgent), urgent);
        foreach (var background in new[] { MarketplaceJobTypes.QuestionSync, MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.ReturnReconciliation, MarketplaceJobTypes.ListingVerify })
            Assert.True(Spreads(background), background);
        var delay = StartupSpreadPolicy.Delay(A, MarketplaceJobTypes.QuestionSync, 180);
        Assert.Equal(delay, StartupSpreadPolicy.Delay(A, MarketplaceJobTypes.QuestionSync, 180));
        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(179));
    }

    [Fact]
    public void A_failing_marketplace_is_told_by_its_error_code()
    {
        foreach (var failing in new[] { "PTTAVM_NETWORK_ERROR", "PAZARAMA_TIMEOUT", "REMOTE_SERVER_ERROR", "HEPSIBURADA_REMOTE_ERROR", "N11_RATE_LIMITED", "TRENDYOL_GATEWAY_BLOCKED" })
            Assert.True(FailingConnectionPollPolicy.IsMarketplaceFailure(failing), failing);
        foreach (var notFailing in new[] { null, "HEPSIBURADA_PRODUCT_APPROVAL_PENDING", "PRODUCT_BATCH_PENDING", "PTTAVM_LOCAL_QUOTA_WAIT", "PAZARAMA_TOKEN_INVALID" })
            Assert.False(FailingConnectionPollPolicy.IsMarketplaceFailure(notFailing), notFailing ?? "null");
        Assert.False(FailingConnectionPollPolicy.Holds(isOrderRead: true, isWrite: false));
        Assert.False(FailingConnectionPollPolicy.Holds(isOrderRead: false, isWrite: true));
        Assert.True(FailingConnectionPollPolicy.Holds(isOrderRead: false, isWrite: false));
    }

    [Fact]
    public void A_job_alert_is_one_per_connection_job_type_error_code_and_day()
    {
        var at = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var key = JobFailureAlertPolicy.ClientKey(A, MarketplaceJobTypes.ReturnSync, "PTTAVM_NETWORK_ERROR", at);
        Assert.StartsWith(JobFailureAlertPolicy.KeyPrefix, key);
        Assert.True(key.Length <= PanelNotificationPolicy.MaximumClientKeyLength);
        Assert.True(PanelNotificationPolicy.IsServerKey(key));
        Assert.Equal(key, JobFailureAlertPolicy.ClientKey(A, MarketplaceJobTypes.ReturnSync, "PTTAVM_NETWORK_ERROR", at.AddHours(8)));
        // 20:59 UTC is 23:59 in Istanbul; 21:00 UTC is the next Istanbul day.
        Assert.NotEqual(key, JobFailureAlertPolicy.ClientKey(A, MarketplaceJobTypes.ReturnSync, "PTTAVM_NETWORK_ERROR", at.AddHours(11)));
        Assert.NotEqual(key, JobFailureAlertPolicy.ClientKey(A, MarketplaceJobTypes.ReturnSync, "PTTAVM_TIMEOUT", at));
        Assert.NotEqual(key, JobFailureAlertPolicy.ClientKey(B, MarketplaceJobTypes.ReturnSync, "PTTAVM_NETWORK_ERROR", at));
        Assert.NotEqual(key, JobFailureAlertPolicy.ClientKey(A, MarketplaceJobTypes.PttAvmOrderSync, "PTTAVM_NETWORK_ERROR", at));
        Assert.True(JobFailureAlertPolicy.IsSuperseded("PRODUCT_UPDATE_SUPERSEDED"));
        Assert.True(JobFailureAlertPolicy.IsSuperseded("SUPERSEDED_BY_LATER_RUN"));
        Assert.False(JobFailureAlertPolicy.IsSuperseded("PTTAVM_NETWORK_ERROR"));
        var message = JobFailureAlertPolicy.Message(JobFailureAlertPolicy.Kind.Dead, "Deneme Mağaza", "PttAVM", MarketplaceJobTypes.ReturnSync, "Pazaryeri · yanıt vermedi");
        Assert.Equal("Deneme Mağaza: Yeni İadeler işi denemeleri bittiği için durdu. Neden: Pazaryeri, yanıt vermedi", message);
        Assert.DoesNotContain(" · ", message);
        Assert.Equal("Kategori ve Marka Listesi", JobFailureAlertPolicy.JobName(MarketplaceJobTypes.PazaramaReferenceSync));
        Assert.Equal("Sipariş Geçmişi", JobFailureAlertPolicy.JobName(MarketplaceJobTypes.PttAvmOrderRecoverySync));
    }

    [Fact]
    public void Every_switch_is_on_by_default_and_each_can_be_turned_off()
    {
        var defaults = SchedulerTimingSettings.From(new ConfigurationBuilder().Build());
        Assert.True(defaults.ReferenceOneAtATime && defaults.ReferenceSpread && defaults.StartupSpread && defaults.HoldPollsWhileFailing);
        Assert.Equal(TimeSpan.FromHours(6), defaults.ReferenceWindowStart);
        Assert.Equal(TimeSpan.FromHours(4), defaults.ReferenceWindowLength);
        Assert.Equal(180, defaults.StartupSpreadSeconds);
        var off = SchedulerTimingSettings.From(QueuePhase1ASchedulingPostgreSqlTests.SwitchesOff());
        Assert.False(off.ReferenceOneAtATime || off.ReferenceSpread || off.StartupSpread || off.HoldPollsWhileFailing);
    }
}

[Collection("postgres-integration")]
public sealed class QueuePhase1ASchedulingPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Base = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ReferenceTypes = [MarketplaceJobTypes.ReferenceSync, MarketplaceJobTypes.PazaramaReferenceSync, MarketplaceJobTypes.PttAvmReferenceSync, MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.N11ReferenceSync];

    // The four phase 1A switches off: the scheduler as it was before (the snapshot test pins that behaviour with these).
    internal static IConfiguration SwitchesOff() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Worker:ReferenceReads:OneAtATime"] = "false",
        ["Worker:ReferenceReads:Spread"] = "false",
        ["Worker:StartupSpread:Enabled"] = "false",
        ["Worker:HoldPollsWhileFailing:Enabled"] = "false"
    }).Build();

    private static IConfiguration Defaults() => new ConfigurationBuilder().Build();

    [PostgreSqlFact]
    public async Task Item1_a_connections_reference_lists_are_queued_one_after_the_other()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenantId, ids) = await SeedAsync(db, ("TRENDYOL", Guid.CreateVersion7()));
        var connection = ids[0];
        var now = ReferenceReadSchedulePolicy.LatestSlot(Base, connection, ReferenceReadSchedulePolicy.DefaultWindowStart, ReferenceReadSchedulePolicy.DefaultWindowLength).AddMinutes(1);

        await Pass(db, now);
        var first = await ReferenceJobsAsync(db, tenantId);
        // Before the fix: the category tree and the brand list in the same second, the second waiting for the first.
        Assert.Single(first);
        Assert.DoesNotContain(":brands:", first[0].JobDedupKey);

        await Pass(db, now.AddSeconds(10));
        Assert.Single(await ReferenceJobsAsync(db, tenantId));

        await FinishAsync(db, first[0].Id, connection, tenantId, "CATEGORIES", now.AddSeconds(15));
        await Pass(db, now.AddSeconds(20));
        var second = await ReferenceJobsAsync(db, tenantId);
        Assert.Equal(2, second.Count);
        Assert.Contains(second, job => job.JobDedupKey.Contains(":brands:", StringComparison.Ordinal) && job.Status == JobStatus.Pending);

        await FinishAsync(db, second.Single(job => job.Status == JobStatus.Pending).Id, connection, tenantId, "BRANDS", now.AddSeconds(25));
        await Pass(db, now.AddSeconds(30));
        Assert.Equal(2, (await ReferenceJobsAsync(db, tenantId)).Count);
        var stamped = await db.ConnectionSyncPolicies.AsNoTracking().SingleAsync(x => x.ConnectionId == connection && x.ResourceType == "REFERENCE_DATA");
        // Marked as scheduled when the last list (the brand list) was queued; nothing more for this slot.
        Assert.Equal(now.AddSeconds(20), stamped.LastScheduledAt);

        // The next day: nothing before the connection's slot, the category tree again right after it.
        await Pass(db, now.AddDays(1).AddMinutes(-2));
        Assert.Equal(2, (await ReferenceJobsAsync(db, tenantId)).Count);
        await Pass(db, now.AddDays(1));
        var nextDay = await ReferenceJobsAsync(db, tenantId);
        Assert.Equal(3, nextDay.Count);
        Assert.Single(nextDay, job => job.Status == JobStatus.Pending && !job.JobDedupKey.Contains(":brands:", StringComparison.Ordinal));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Item1_a_tenant_runs_one_scheduled_reference_read_at_a_time_and_the_switch_restores_the_old_pass()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenantId, _) = await SeedAsync(db, ("TRENDYOL", Guid.CreateVersion7()), ("PAZARAMA", Guid.CreateVersion7()), ("PTTAVM", Guid.CreateVersion7()));
        await Pass(db, Base);
        Assert.Single(await ReferenceJobsAsync(db, tenantId));
        await transaction.RollbackAsync();

        await using var legacy = fixture.CreateStrictContext();
        await using var legacyTransaction = await legacy.Database.BeginTransactionAsync();
        var (legacyTenant, _) = await SeedAsync(legacy, ("TRENDYOL", Guid.CreateVersion7()), ("PAZARAMA", Guid.CreateVersion7()), ("PTTAVM", Guid.CreateVersion7()));
        await Pass(legacy, Base, SwitchesOff());
        // Off: Trendyol's and Pazarama's two lists and PttAVM's tree, all in the same pass (as before).
        Assert.Equal(5, (await ReferenceJobsAsync(legacy, legacyTenant)).Count);
        await legacyTransaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Item2_the_startup_pass_spreads_background_reads_and_starts_order_reads_at_once()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenantId, ids) = await SeedAsync(db, ("TRENDYOL", Guid.CreateVersion7()));
        await new ScheduledJobProducer(db, new FixedClock(Base), Defaults()).EnqueueDueAsync(atStartup: true, CancellationToken.None);
        var jobs = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();
        DateTimeOffset At(string jobType) => jobs.Single(x => x.JobType == jobType).AvailableAt;
        Assert.Equal(Base, At(MarketplaceJobTypes.OrderSync));
        Assert.Equal(Base, At(MarketplaceJobTypes.ReturnSync));
        Assert.Equal(Base, At(MarketplaceJobTypes.OrderInvoiceReconciliation));
        Assert.Equal(Base + StartupSpreadPolicy.Delay(ids[0], MarketplaceJobTypes.QuestionSync, 180), At(MarketplaceJobTypes.QuestionSync));
        Assert.Equal(Base + StartupSpreadPolicy.Delay(ids[0], MarketplaceJobTypes.TrendyolFinanceSync, 180), At(MarketplaceJobTypes.TrendyolFinanceSync));
        Assert.Contains(jobs, job => job.AvailableAt > Base);
        Assert.All(jobs, job => Assert.InRange(job.AvailableAt, Base, Base.AddSeconds(180)));
        await transaction.RollbackAsync();

        // A normal pass (and the startup pass with the switch off) starts everything at once.
        await using var plain = fixture.CreateStrictContext();
        await using var plainTransaction = await plain.Database.BeginTransactionAsync();
        var (plainTenant, _) = await SeedAsync(plain, ("TRENDYOL", Guid.CreateVersion7()));
        await new ScheduledJobProducer(plain, new FixedClock(Base), SwitchesOff()).EnqueueDueAsync(atStartup: true, CancellationToken.None);
        Assert.All(await plain.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == plainTenant).ToListAsync(), job => Assert.Equal(Base, job.AvailableAt));
        await plainTransaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Item3_no_new_background_poll_while_the_marketplace_fails_and_one_at_once_after()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenantId, ids) = await SeedAsync(db, ("PTTAVM", Guid.CreateVersion7()));
        var connection = ids[0];
        await Pass(db, Base);
        // Everything of the first pass ran; the order read of a day later failed on the marketplace and waits for its retry.
        await db.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, JobStatus.Succeeded).SetProperty(x => x.CompletedAt, Base.AddMinutes(1)));
        var failing = NewJob(tenantId, connection, MarketplaceJobTypes.PttAvmOrderSync, $"scheduled:orders:{connection:N}:failing", Base.AddDays(1));
        failing.Status = JobStatus.RetryScheduled;
        failing.AttemptCount = 2;
        failing.LastErrorCode = "PTTAVM_NETWORK_ERROR";
        failing.AvailableAt = Base.AddDays(1).AddMinutes(2);
        db.IntegrationJobs.Add(failing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await JobCountAsync(db, tenantId);

        var during = Base.AddDays(1).AddMinutes(1);
        var queuedDuring = await PassQueuedAsync(db, tenantId, during);
        Assert.NotEmpty(queuedDuring);
        // Before the fix: the return read (and the reference read) were queued on top of the retry chain.
        Assert.DoesNotContain(MarketplaceJobTypes.ReturnSync, queuedDuring);
        Assert.DoesNotContain(MarketplaceJobTypes.PttAvmReferenceSync, queuedDuring);
        // The new-order read is not doubled either (one queued per connection while it retries, the rule it had).
        Assert.DoesNotContain(MarketplaceJobTypes.PttAvmOrderSync, queuedDuring);
        Assert.All(queuedDuring, jobType => Assert.True(JobTypeRegistry.IsOrderReadSync(jobType), jobType));

        // The retry got through: the next pass queues the held return read at once.
        await db.IntegrationJobs.Where(x => x.Id == failing.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, JobStatus.Succeeded).SetProperty(x => x.CompletedAt, during.AddSeconds(5)));
        var queuedAfter = await PassQueuedAsync(db, tenantId, during.AddSeconds(10));
        Assert.Contains(MarketplaceJobTypes.ReturnSync, queuedAfter);
        Assert.True(await JobCountAsync(db, tenantId) > before);
        await transaction.RollbackAsync();

        // Off: the return read is queued during the retries, as before.
        await using var legacy = fixture.CreateStrictContext();
        await using var legacyTransaction = await legacy.Database.BeginTransactionAsync();
        var (legacyTenant, legacyIds) = await SeedAsync(legacy, ("PTTAVM", Guid.CreateVersion7()));
        await Pass(legacy, Base, SwitchesOff());
        await legacy.IntegrationJobs.Where(x => x.TenantId == legacyTenant).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, JobStatus.Succeeded).SetProperty(x => x.CompletedAt, Base.AddMinutes(1)));
        var legacyFailing = NewJob(legacyTenant, legacyIds[0], MarketplaceJobTypes.PttAvmOrderSync, $"scheduled:orders:{legacyIds[0]:N}:failing", Base.AddDays(1));
        legacyFailing.Status = JobStatus.RetryScheduled;
        legacyFailing.LastErrorCode = "PTTAVM_NETWORK_ERROR";
        legacy.IntegrationJobs.Add(legacyFailing);
        await legacy.SaveChangesAsync();
        legacy.ChangeTracker.Clear();
        Assert.Contains(MarketplaceJobTypes.ReturnSync, await PassQueuedAsync(legacy, legacyTenant, during, SwitchesOff()));
        await legacyTransaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task Item4_a_stopped_job_raises_one_bell_alert_a_day_and_a_recovered_or_superseded_one_none()
    {
        Assert.NotNull(fixture);
        await using var db = fixture.CreateStrictContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenantId, ids) = await SeedAsync(db, ("PTTAVM", Guid.CreateVersion7()), ("TRENDYOL", Guid.CreateVersion7()));
        var ptt = ids[0];
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        IntegrationJob Stopped(Guid connection, string jobType, JobStatus status, string? code, DateTimeOffset completedAt)
        {
            var job = NewJob(tenantId, connection, jobType, $"test:{Guid.NewGuid():N}", completedAt.AddMinutes(-5));
            job.Status = status;
            job.LastErrorCode = code;
            job.LastErrorSummary = code is null ? null : "Deneme özeti";
            job.CompletedAt = completedAt;
            job.AttemptCount = 6;
            return job;
        }
        db.IntegrationJobs.AddRange(
            Stopped(ptt, MarketplaceJobTypes.ReturnSync, JobStatus.Dead, "PTTAVM_NETWORK_ERROR", now.AddMinutes(-3)),
            // The same store, type and cause again: the same bell entry.
            Stopped(ptt, MarketplaceJobTypes.ReturnSync, JobStatus.Dead, "PTTAVM_NETWORK_ERROR", now.AddMinutes(-2)),
            // A retry that later succeeded: nothing.
            Stopped(ptt, MarketplaceJobTypes.PttAvmOrderSync, JobStatus.Succeeded, "PTTAVM_NETWORK_ERROR", now.AddMinutes(-1)),
            // In review: a warning.
            Stopped(ids[1], MarketplaceJobTypes.ReturnSync, JobStatus.ManualReview, "REMOTE_CONTRACT_INVALID", now.AddMinutes(-4)),
            // Blocked since 14 Nov because a newer update replaced it (the seven live PRODUCT_UPDATE jobs): never an alert.
            Stopped(ids[1], MarketplaceJobTypes.ProductUpdate, JobStatus.Blocked, "PRODUCT_UPDATE_SUPERSEDED", now.AddDays(-13)),
            Stopped(ids[1], MarketplaceJobTypes.ProductUpdate, JobStatus.Blocked, "PRODUCT_UPDATE_SUPERSEDED", now.AddHours(-25)),
            // Blocked before sending because writes are off (the owner's choice): never an alert.
            Stopped(ids[1], MarketplaceJobTypes.PriceInventorySync, JobStatus.Blocked, "EXTERNAL_WRITES_DISABLED", now.AddHours(-25)),
            // Blocked for a day for another reason: one warning; blocked for an hour: not yet.
            Stopped(ids[1], MarketplaceJobTypes.ProductCreate, JobStatus.Blocked, "PRODUCT_CREATE_CATEGORY_MISSING", now.AddHours(-25)),
            Stopped(ids[1], MarketplaceJobTypes.ProductArchive, JobStatus.Blocked, "PRODUCT_ARCHIVE_STATE_MISSING", now.AddHours(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(3, await JobFailureAlerts.RaiseAsync(db, now, TimeSpan.FromHours(6), CancellationToken.None));
        // The next pass writes nothing new.
        Assert.Equal(0, await JobFailureAlerts.RaiseAsync(db, now.AddMinutes(1), TimeSpan.FromHours(6), CancellationToken.None));
        var rows = await db.PanelNotifications.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Severity).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => { Assert.Null(row.UserId); Assert.StartsWith(JobFailureAlertPolicy.KeyPrefix, row.ClientKey); Assert.Equal("JOB", row.Category); Assert.StartsWith("/jobs?ara=", row.Href); });
        var dead = Assert.Single(rows, row => row.Severity == PanelNotificationSeverities.Error);
        Assert.Equal("Arka Plan İşi Durdu", dead.Title);
        Assert.Contains("Yeni İadeler işi denemeleri bittiği için durdu", dead.Message);
        Assert.Equal("PTTAVM", dead.PlatformCode);
        Assert.Contains(rows, row => row.Title == "İş İnceleme Bekliyor");
        Assert.Contains(rows, row => row.Title == "İş Bir Gündür Engelli" && row.Message.Contains("Ürün Oluşturma", StringComparison.Ordinal));

        // Removed from the bell, the same cause the same day stays away; the next day it is a new entry.
        await db.PanelNotifications.Where(x => x.Id == dead.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.DismissedAt, now));
        db.IntegrationJobs.Add(Stopped(ptt, MarketplaceJobTypes.ReturnSync, JobStatus.Dead, "PTTAVM_NETWORK_ERROR", now.AddMinutes(5)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await JobFailureAlerts.RaiseAsync(db, now.AddMinutes(6), TimeSpan.FromHours(6), CancellationToken.None));
        db.IntegrationJobs.Add(Stopped(ptt, MarketplaceJobTypes.ReturnSync, JobStatus.Dead, "PTTAVM_NETWORK_ERROR", now.AddDays(1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // The new day's dead read, and the archive blocked an hour before the first pass, now blocked for a day.
        Assert.Equal(2, await JobFailureAlerts.RaiseAsync(db, now.AddDays(1).AddMinutes(1), TimeSpan.FromHours(6), CancellationToken.None));
        Assert.Contains(await db.PanelNotifications.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Message).ToListAsync(), message => message.Contains("Ürünü Satıştan Kaldırma", StringComparison.Ordinal));

        // The bell's list (GET /notifications/new-orders) carries the alerts beside the order notices.
        var listed = await new PanelNotificationService(db, new FixedClock(now.AddDays(1).AddMinutes(2))).NewOrdersAsync(tenantId, Guid.NewGuid(), CancellationToken.None);
        Assert.Contains(listed, row => row.ClientKey.StartsWith(JobFailureAlertPolicy.KeyPrefix, StringComparison.Ordinal) && row.Severity == PanelNotificationSeverities.Error);
        await transaction.RollbackAsync();
    }

    private static async Task Pass(AppDbContext db, DateTimeOffset now, IConfiguration? configuration = null)
    {
        db.ChangeTracker.Clear();
        await new ScheduledJobProducer(db, new FixedClock(now), configuration ?? Defaults()).EnqueueDueAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    // One pass; the job types it queued (the rows that were not there before it).
    private static async Task<List<string>> PassQueuedAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, IConfiguration? configuration = null)
    {
        var existing = (await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Id).ToListAsync()).ToHashSet();
        await Pass(db, now, configuration);
        return (await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.JobType }).ToListAsync())
            .Where(x => !existing.Contains(x.Id)).Select(x => x.JobType).ToList();
    }

    private static Task<List<IntegrationJob>> ReferenceJobsAsync(AppDbContext db, Guid tenantId) =>
        db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && ReferenceTypes.Contains(x.JobType)).OrderBy(x => x.CreatedAt).ThenBy(x => x.JobDedupKey).ToListAsync();

    private static Task<int> JobCountAsync(AppDbContext db, Guid tenantId) => db.IntegrationJobs.CountAsync(x => x.TenantId == tenantId);

    // The reference job ran: it succeeded and its list is current from the given time.
    private static async Task FinishAsync(AppDbContext db, Guid jobId, Guid connectionId, Guid tenantId, string resource, DateTimeOffset at)
    {
        await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, JobStatus.Succeeded).SetProperty(x => x.CompletedAt, at));
        db.ReferenceSnapshots.Add(new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, SourceVersion = "test", ContentHash = Guid.NewGuid().ToString("N"), FetchedAt = at, IsCurrent = true, ItemCount = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static IntegrationJob NewJob(Guid tenantId, Guid connectionId, string jobType, string dedup, DateTimeOffset createdAt)
    {
        var job = IntegrationJobFactory.New(new IntegrationJobRequest(tenantId, connectionId, jobType, "{}", dedup, $"test-{Guid.NewGuid():N}", createdAt) { Priority = JobPriorityPolicy.For(jobType, userInitiated: false) });
        job.CreatedAt = createdAt;
        return job;
    }

    private static async Task<(Guid TenantId, Guid[] Connections)> SeedAsync(AppDbContext db, params (string Platform, Guid Id)[] connections)
    {
        var tenantId = Guid.CreateVersion7();
        db.Tenants.Add(new Tenant { Id = tenantId, Code = $"faz1a-{Guid.NewGuid():N}", DisplayName = "faz1a", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Base.AddDays(-60), UpdatedAt = Base.AddDays(-60), Version = 1 });
        foreach (var (platform, id) in connections)
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = id,
                TenantId = tenantId,
                PublicId = Guid.CreateVersion7(),
                PlatformCode = platform,
                Environment = "PRODUCTION",
                DisplayName = $"{platform} Deneme",
                ExternalStoreId = $"{platform}-{Guid.NewGuid():N}"[..30],
                Status = "ACTIVE",
                ApiVersion = "V1"
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenantId, [.. connections.Select(x => x.Id)]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
