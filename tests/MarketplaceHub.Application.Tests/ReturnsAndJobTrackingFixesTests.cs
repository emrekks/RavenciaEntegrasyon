using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Returns and job tracking fixes of the 1 Nov 2026 review (İadeler Y1–Y5, Genel P2–P9).
public sealed class ReturnsAndJobTrackingFixesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    [Fact]
    public void ReturnReadsRunEveryTenMinutesWhileAClaimIsOpen()
    {
        foreach (var platform in new[] { "TRENDYOL", "N11", "PAZARAMA", "PTTAVM", "HEPSIBURADA" })
            Assert.Equal(600, ReturnCadencePolicy.IntervalSeconds(180, platform, notificationsHealthy: false, hasOpenReturns: true, Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void QuietReturnReadsStretchStepByStepToHalfAnHourAtMost()
    {
        Assert.Equal(600, ReturnCadencePolicy.IntervalSeconds(180, "N11", false, false, Now.AddHours(-3), Now));
        Assert.Equal(1200, ReturnCadencePolicy.IntervalSeconds(180, "N11", false, false, Now.AddDays(-2), Now));
        Assert.Equal(1800, ReturnCadencePolicy.IntervalSeconds(180, "PAZARAMA", false, false, Now.AddDays(-8), Now));
        Assert.Equal(1800, ReturnCadencePolicy.IntervalSeconds(180, "PTTAVM", false, false, null, Now));
        // A longer configured cadence stays.
        Assert.Equal(7200, ReturnCadencePolicy.IntervalSeconds(7200, "N11", false, true, Now, Now));
    }

    [Fact]
    public void OnlyHepsiburadasHealthyNotificationsMakeTheReturnReadASafetyNet()
    {
        Assert.Equal(1800, ReturnCadencePolicy.IntervalSeconds(180, "HEPSIBURADA", notificationsHealthy: true, hasOpenReturns: true, Now, Now));
        Assert.Equal(3600, ReturnCadencePolicy.IntervalSeconds(180, "HEPSIBURADA", notificationsHealthy: true, hasOpenReturns: false, Now, Now));
        // Trendyol's notifications carry order packages only: a healthy webhook does not stretch its return read.
        Assert.False(ReturnCadencePolicy.NotificationsCarryClaims("TRENDYOL"));
        Assert.Equal(600, ReturnCadencePolicy.IntervalSeconds(180, "TRENDYOL", notificationsHealthy: true, hasOpenReturns: true, Now, Now));
        // Broken notifications fall back to ten minutes.
        Assert.Equal(600, ReturnCadencePolicy.IntervalSeconds(180, "HEPSIBURADA", notificationsHealthy: false, hasOpenReturns: true, Now, Now));
        Assert.Equal(3600, ReturnCadencePolicy.MaximumSeconds("HEPSIBURADA", 180));
        Assert.Equal(1800, ReturnCadencePolicy.MaximumSeconds("TRENDYOL", 180));
    }

    [Fact]
    public void PttAvmReturnsReadTwoDaysAndTheWholeThreeMonthsEverySixHours()
    {
        Assert.True(PttAvmReturnReadPolicy.WideReadDue(full: true, Now, Now));
        Assert.True(PttAvmReturnReadPolicy.WideReadDue(full: false, null, Now));
        Assert.False(PttAvmReturnReadPolicy.WideReadDue(full: false, Now.AddHours(-2), Now));
        Assert.True(PttAvmReturnReadPolicy.WideReadDue(full: false, Now.AddHours(-6), Now));
        var narrow = PttAvmOrderHistoryPolicy.StartDay(false, Now.AddMinutes(-10), Now);
        Assert.Single(PttAvmOrderHistoryPolicy.Windows(narrow, Now));
        Assert.Equal(Now.ToOffset(TimeSpan.FromHours(3)).Date.AddDays(-2), narrow);
    }

    [Fact]
    public void TheOpenReturnScanFollowsOnlyClaimsStillMoving()
    {
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var claims = Enum.GetValues<ReturnClaimStatus>()
            .Select(status => Claim(tenantId, connectionId, status, status.ToString()))
            .Append(Claim(tenantId, Guid.NewGuid(), ReturnClaimStatus.Requested, "Created"))
            .ToArray();

        var followed = MarketplaceJobProcessor.ReturnLifecycleCandidates(claims.AsQueryable(), tenantId, connectionId, "TRENDYOL").Select(x => x.Status).ToArray();

        Assert.Equal([ReturnClaimStatus.Requested, ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Disputed], followed);
        Assert.All(Enum.GetValues<ReturnClaimStatus>(), status => Assert.Equal(followed.Contains(status), ReturnCadencePolicy.IsOpen(status)));
    }

    [Fact]
    public void HepsiburadasOpenReturnScanKeepsItsOwnClaimWords()
    {
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var claims = new[]
        {
            Claim(tenantId, connectionId, ReturnClaimStatus.ActionRequired, "AwaitingAction"),
            Claim(tenantId, connectionId, ReturnClaimStatus.Completed, "Refunded"),
            Claim(tenantId, connectionId, ReturnClaimStatus.Approved, "Accepted")
        };

        var followed = MarketplaceJobProcessor.ReturnLifecycleCandidates(claims.AsQueryable(), tenantId, connectionId, "HEPSIBURADA").Select(x => x.RawStatus).ToArray();

        Assert.Equal(["AwaitingAction"], followed);
        // No open claim: nothing for the scheduler to queue.
        Assert.Empty(MarketplaceJobProcessor.ReturnLifecycleCandidates(claims.Skip(1).AsQueryable(), tenantId, connectionId, "HEPSIBURADA"));
    }

    [Fact]
    public void AReadThatBringsTheSameStatusIsNotAnUpdate()
    {
        var modifiedAt = DateTimeOffset.Parse("2026-10-30T09:00:00Z");
        var claim = Claim(Guid.NewGuid(), Guid.NewGuid(), ReturnClaimStatus.Approved, "Accepted");
        claim.LastRemoteModifiedAt = modifiedAt;

        Assert.True(ReturnClaimStateMachine.CanTransition(ReturnClaimStatus.Approved, ReturnClaimStatus.Approved));
        Assert.False(MarketplaceJobProcessor.ReturnStatusObservationChanges(claim, ReturnClaimStatus.Approved, "Accepted", modifiedAt.ToOffset(TimeSpan.FromHours(3))));
        Assert.True(MarketplaceJobProcessor.ReturnStatusObservationChanges(claim, ReturnClaimStatus.Completed, "Accepted", modifiedAt));
        Assert.True(MarketplaceJobProcessor.ReturnStatusObservationChanges(claim, ReturnClaimStatus.Approved, "Refunded", modifiedAt));
        Assert.True(MarketplaceJobProcessor.ReturnStatusObservationChanges(claim, ReturnClaimStatus.Approved, "Accepted", modifiedAt.AddMinutes(1)));
    }

    [Fact]
    public void OpenClaimsAreAskedForSeveralInOneTrendyolRequest()
    {
        var ids = Enumerable.Range(1, 45).Select(i => $"claim-{i}").Append("claim-1").Append(" ").ToArray();

        var requests = ReturnLifecycleBatchPolicy.ClaimIdRequests(ids);

        Assert.Equal([20, 20, 5], requests.Select(x => x.Length));
        Assert.Equal("order/sellers/123/claims?claimIds=a-1,b%202", TrendyolHttpClient.ClaimsByIdsEndpoint("123", ["a-1", "b 2"]));
    }

    [Fact]
    public void AnUnexplainedPttAvmFaultIsRetriedOnReadsAndStaysARejectionOnWrites()
    {
        const string unknown = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Bilinmeyen bir hata oluştu.</faultstring></s:Fault></s:Body></s:Envelope>""";

        var (_, read) = PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError, unknown, "SiparisKontrolListesiV2");
        Assert.Equal(AdapterErrorClass.Remote5xx, read!.Class);
        Assert.Equal("PTTAVM_REMOTE_FAULT", read.Code);

        var (_, write) = PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError, unknown, "UpdateProductsStockPrice");
        Assert.Equal(AdapterErrorClass.Validation, write!.Class);
        Assert.Equal("PTTAVM_REQUEST_REJECTED", write.Code);

        var (_, invoice) = PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError, unknown, "SaveInvoince");
        Assert.Equal("PTTAVM_REQUEST_REJECTED", invoice!.Code);

        // A fault that names its cause stays a rejection, read or not.
        var (_, explained) = PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError,
            """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Bitiş tarihi başlangıç tarihinden önce olamaz.</faultstring></s:Fault></s:Body></s:Envelope>""",
            "SiparisKontrolListesiV2");
        Assert.Equal("PTTAVM_REQUEST_REJECTED", explained!.Code);
    }

    [Fact]
    public void ALaterSuccessfulScheduledReadClosesTheOldFailureOfTheSameRead()
    {
        var connection = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.PazaramaOrderSync, $"scheduled:orders:{connection:N}:100", JobStatus.Blocked, Now.AddDays(-12));
        var later = (connection, MarketplaceJobTypes.PazaramaOrderSync, $"scheduled:orders:{connection:N}:900", JobStatus.Succeeded, Now.AddDays(-1));

        Assert.True(ScheduledReadSupersession.Supersedes(failed, later));
        Assert.True(ScheduledReadSupersession.Supersedes(failed with { Item4 = JobStatus.Dead }, later));
    }

    [Fact]
    public void SupersessionNeverTouchesWritesTargetedReadsOrOtherSeries()
    {
        var connection = Guid.NewGuid();
        var later = (connection, MarketplaceJobTypes.OrderSync, $"scheduled:orders:{connection:N}:900", JobStatus.Succeeded, Now);
        var failed = (connection, MarketplaceJobTypes.OrderSync, $"scheduled:orders:{connection:N}:100", JobStatus.Blocked, Now.AddDays(-2));

        // A write, even with a scheduler key, is never closed.
        foreach (var write in new[] { MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.ShipmentAction, MarketplaceJobTypes.ReturnAction, InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.MarketplaceDelivery })
            Assert.False(ScheduledReadSupersession.Supersedes(failed with { Item2 = write }, later with { Item2 = write }));
        // A targeted or user read is not a scheduled read.
        Assert.False(ScheduledReadSupersession.Supersedes(failed with { Item3 = $"trendyol_order_sync:{connection}:116483573775" }, later));
        Assert.False(ScheduledReadSupersession.IsScheduledRead(MarketplaceJobTypes.OrderSync, $"trendyol_order_sync:{connection}"));
        // An earlier success, another connection, another type or another series (a daily reconciliation against an hourly one) do not count.
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item5 = Now.AddDays(-3) }));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item1 = Guid.NewGuid() }));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item2 = MarketplaceJobTypes.OrderRecoverySync }));
        Assert.False(ScheduledReadSupersession.Supersedes(
            (connection, MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-daily:{connection:N}:1", JobStatus.Dead, Now.AddDays(-2)),
            (connection, MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-short:{connection:N}:9", JobStatus.Succeeded, Now)));
        // Only blocked or dead jobs are closed, and only by a success.
        Assert.False(ScheduledReadSupersession.Supersedes(failed with { Item4 = JobStatus.ManualReview }, later));
        Assert.False(ScheduledReadSupersession.Supersedes(failed with { Item4 = JobStatus.RetryScheduled }, later));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item4 = JobStatus.Blocked }));
    }

    [Fact]
    public void AJobOpenForDaysIsShownInTheLastDayAndAnOldFinishedOneIsNot()
    {
        var since = Now.AddDays(-1);
        Assert.True(JobOperationsService.IsInWindow(Job(Now.AddDays(-4), null, JobStatus.RetryScheduled), since));
        Assert.True(JobOperationsService.IsInWindow(Job(Now.AddDays(-4), null, JobStatus.Leased), since));
        Assert.True(JobOperationsService.IsInWindow(Job(Now.AddDays(-4), Now.AddHours(-2), JobStatus.Succeeded), since));
        Assert.True(JobOperationsService.IsInWindow(Job(Now.AddHours(-1), null, JobStatus.Blocked), since));
        Assert.False(JobOperationsService.IsInWindow(Job(Now.AddDays(-4), Now.AddDays(-3), JobStatus.Blocked), since));
        Assert.False(JobOperationsService.IsInWindow(Job(Now.AddDays(-4), Now.AddDays(-3), JobStatus.Succeeded), since));
    }

    [Fact]
    public void JobSummariesSayWhatEachProductAndInvoiceJobDid()
    {
        foreach (var write in new[] { MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive })
            Assert.Contains("pazaryerine gönderildi", JobOperationsService.ProductOrInvoiceChange(write)!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gönderilmedi", JobOperationsService.ProductOrInvoiceChange(MarketplaceJobTypes.ProductSync)!.Detail);
        Assert.Equal("Fatura isteği pazaryerine gönderildi.", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.MarketplaceDelivery)!.Detail);
        Assert.Equal("Fatura isteği pazaryerine gönderildi.", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.ManualOrderInvoiceDelivery)!.Detail);
        Assert.StartsWith("Panel içi tarama", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.InvoiceDueScan)!.Detail);
        Assert.Contains("E-Faturam'dan okundu", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.InvoiceReconcile)!.Detail);
        Assert.Contains("E-Faturam'dan okundu", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.InvoiceDocumentFetch)!.Detail);
        Assert.DoesNotContain("pazaryerine", JobOperationsService.ProductOrInvoiceChange(InvoicingJobTypes.InvoiceSubmit)!.Detail);
        Assert.Null(JobOperationsService.ProductOrInvoiceChange("SOMETHING_WITH_INVOICE_IN_IT"));
    }

    [Fact]
    public void OrderAndReturnReadsWriteWhatTheyBrought()
    {
        Assert.True(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.ReturnSync));
        Assert.True(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.HepsiburadaReturnSync));
        Assert.True(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.PttAvmOrderSync));
        Assert.True(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.OrderStatusSync));
        Assert.False(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.ProductSync));
        Assert.False(MarketplaceJobProcessor.RecordsReadProgress(MarketplaceJobTypes.ShipmentAction));
        Assert.Equal("Pazaryerinden yeni kayıt gelmedi", MarketplaceJobProcessor.ReadProgressLabel(0, 0, 0));
        Assert.Equal("12 kayıt alındı · 2 yeni · 3 güncellendi", MarketplaceJobProcessor.ReadProgressLabel(12, 2, 3));
    }

    private static ReturnClaim Claim(Guid tenantId, Guid connectionId, ReturnClaimStatus status, string rawStatus) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = Guid.NewGuid(),
        ExternalClaimId = Guid.NewGuid().ToString("N"),
        Status = status,
        RawStatus = rawStatus
    };

    private static IntegrationJob Job(DateTimeOffset createdAt, DateTimeOffset? completedAt, JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        JobType = MarketplaceJobTypes.ProductApprovalReconcile,
        PayloadJson = "{}",
        PayloadHash = "x",
        JobDedupKey = "k",
        EffectIdempotencyKey = "e",
        CorrelationId = "c",
        Status = status,
        CreatedAt = createdAt,
        CompletedAt = completedAt
    };
}
