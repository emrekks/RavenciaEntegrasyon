using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Jobs, sync health and notification fixes of the 6 Nov 2026 audit (Y2, O1, O2/O3, D7).
public sealed class JobsHealthFixesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T18:00:00Z");

    [Fact]
    public void AReadInReviewForAPassingCauseClosesOnTheNextSuccessfulRunOfTheSameRead()
    {
        var connection = Guid.NewGuid();
        var later = (connection, MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connection:N}:900", JobStatus.Succeeded, Now);
        var trendyolReturn = (connection, MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connection:N}:100", JobStatus.ManualReview, Now.AddDays(-1));
        Assert.True(ScheduledReadSupersession.Supersedes(trendyolReturn, later, "REMOTE_CONTRACT_INVALID"));

        var pazaramaOrders = (connection, MarketplaceJobTypes.PazaramaOrderSync, $"scheduled:orders:{connection:N}:100", JobStatus.ManualReview, Now.AddDays(-2));
        Assert.True(ScheduledReadSupersession.Supersedes(pazaramaOrders,
            (connection, MarketplaceJobTypes.PazaramaOrderSync, $"scheduled:orders:{connection:N}:900", JobStatus.Succeeded, Now), "PAZARAMA_TOKEN_INVALID"));
        var reconcile = (connection, MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-short:{connection:N}:1", JobStatus.ManualReview, Now.AddDays(-1));
        Assert.True(ScheduledReadSupersession.Supersedes(reconcile,
            (connection, MarketplaceJobTypes.ReturnReconciliation, $"scheduled:return-reconcile-short:{connection:N}:9", JobStatus.Succeeded, Now), "REMOTE_CONTRACT_INVALID"));
    }

    [Fact]
    public void AReviewAPersonMustSettleOrAWriteOrAnInvoiceIsNeverClosedByALaterRun()
    {
        var connection = Guid.NewGuid();
        var later = (connection, MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connection:N}:900", JobStatus.Succeeded, Now);
        var failed = (connection, MarketplaceJobTypes.ReturnSync, $"scheduled:returns:{connection:N}:100", JobStatus.ManualReview, Now.AddDays(-1));
        // Another review code, or none, stays for a person.
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later, "UNEXPECTED_JOB_STATE"));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later, "PRODUCT_APPROVAL_DEADLINE_EXPIRED"));
        // A write or an invoice job with the same code is never touched.
        foreach (var write in new[] { MarketplaceJobTypes.ProductApprovalReconcile, MarketplaceJobTypes.ShipmentAction, MarketplaceJobTypes.ReturnAction, InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.MarketplaceDelivery })
            Assert.False(ScheduledReadSupersession.Supersedes(failed with { Item2 = write }, later with { Item2 = write }, "REMOTE_CONTRACT_INVALID"));
        // An earlier success, or a later failure, does not close it.
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item5 = Now.AddDays(-2) }, "REMOTE_CONTRACT_INVALID"));
        Assert.False(ScheduledReadSupersession.Supersedes(failed, later with { Item4 = JobStatus.ManualReview }, "REMOTE_CONTRACT_INVALID"));
        Assert.True(ScheduledReadSupersession.IsClosableFailure(JobStatus.Blocked, null));
        Assert.False(ScheduledReadSupersession.IsClosableFailure(JobStatus.ManualReview, null));
    }

    [Fact]
    public void AnApprovalStillPendingIsAWaitNotAFailedRunOfTheCursor()
    {
        Assert.True(MarketplaceJobProcessor.IsRemoteWait(JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", "Onay bekleniyor.", TimeSpan.FromHours(2))));
        Assert.True(MarketplaceJobProcessor.IsRemoteWait(JobExecutionResult.Retry("HEPSIBURADA_PRODUCT_APPROVAL_PENDING", "Bekleniyor.", TimeSpan.FromMinutes(5))));
        Assert.False(MarketplaceJobProcessor.IsRemoteWait(JobExecutionResult.ManualReview("PRODUCT_APPROVAL_DEADLINE_EXPIRED", "Süre doldu.")));
        Assert.False(MarketplaceJobProcessor.IsRemoteWait(JobExecutionResult.Retry("REMOTE_5XX", "Hata.", TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void AProductImportLeavesTheJobsOwnCountsOnItsCursor()
    {
        // Live 6 Nov: the job reported 1309 received, 40 processed, 908 skipped; the cursor showed 7167 changed.
        var run = new MarketplaceJobProcessor.CursorCounts(7167, 7167, 0, 0);
        var import = new MarketplaceJobProcessor.CursorCounts(1309, 40, 908, 0);
        Assert.Equal(import, MarketplaceJobProcessor.CursorImportCounts(productImport: true, run, import));
        Assert.Equal(run, MarketplaceJobProcessor.CursorImportCounts(productImport: false, run, import));
    }

    [Fact]
    public void TheManualOnlyProductImportIsNeverMarkedStagnant()
    {
        var products = new SyncCursor { ResourceType = "PRODUCTS", LastModifiedWatermark = Now.AddDays(-6), LastReceivedCount = 7167, LastSuccessAt = Now, CursorStagnantSince = Now.AddDays(-5) };
        SyncCursorProgressTracker.TrackModification(products, null, products.LastModifiedWatermark, Now.AddDays(-1), Now);
        Assert.Null(products.CursorStagnantSince);

        // A scheduled read with the same history still is.
        var orders = new SyncCursor { ResourceType = "ORDERS_HOT", LastModifiedWatermark = Now.AddDays(-6), LastReceivedCount = 10, LastSuccessAt = Now };
        SyncCursorProgressTracker.TrackModification(orders, null, orders.LastModifiedWatermark, Now.AddDays(-1), Now);
        Assert.Equal(Now, orders.CursorStagnantSince);
    }

    [Fact]
    public void AReadFailingThreeTimesInARowIsRaisedOnceAndAWriteIsNot()
    {
        Assert.False(AttentionNoticePolicy.IsFailing(2, Now, Now));
        Assert.True(AttentionNoticePolicy.IsFailing(3, Now.AddMinutes(-5), Now));
        // A streak left from a read no longer scheduled is not raised.
        Assert.False(AttentionNoticePolicy.IsFailing(8, Now.AddDays(-30), Now));
        Assert.False(AttentionNoticePolicy.IsFailing(8, null, Now));
        Assert.True(AttentionNoticePolicy.IsReadResource("RETURNS"));
        Assert.True(AttentionNoticePolicy.IsReadResource("PRODUCT_APPROVAL"));
        Assert.False(AttentionNoticePolicy.IsReadResource("PRICE_INVENTORY"));
        Assert.False(AttentionNoticePolicy.IsReadResource("RETURN_ACTION"));
        // The expired approval has its own notice naming the products.
        Assert.True(AttentionNoticePolicy.CoveredByOwnNotice("PRODUCT_APPROVAL", "PRODUCT_APPROVAL_DEADLINE_EXPIRED"));
        Assert.False(AttentionNoticePolicy.CoveredByOwnNotice("PRODUCT_APPROVAL", "PAZARAMA_REQUEST_REJECTED"));
        Assert.Contains("art arda 4 kez", AttentionNoticePolicy.FailingMessage("RETURNS", 4, "REMOTE_CONTRACT_INVALID"));
        Assert.Contains("İade okuma", AttentionNoticePolicy.FailingMessage("RETURNS", 4, null));
    }

    [Fact]
    public void AnExpiredApprovalSaysItsOutcomeIsUnknownNeverApprovedOrRefused()
    {
        var message = AttentionNoticePolicy.ExpiredMessage("Pazarama", 2);
        Assert.Contains("bilinmiyor", message);
        Assert.Contains("2 ürünün", message);
        // A streak is one notice until a success starts a new one.
        Assert.Equal(AttentionNoticeService.FailingRevision(Now), AttentionNoticeService.FailingRevision(Now));
        Assert.NotEqual(AttentionNoticeService.FailingRevision(Now), AttentionNoticeService.FailingRevision(Now.AddHours(1)));
        Assert.Equal("never", AttentionNoticeService.FailingRevision(null));
    }

    [Fact]
    public void AContentGoesWhenOnlyItsVarianterDiffersAndIsRefusedWhenAnotherAttributeDoes()
    {
        static string Item(string barcode, int size, int material) =>
            $$"""{"barcode":"{{barcode}}","title":"T","attributes":[{"attributeId":338,"attributeName":"Beden","attributeValueId":{{size}}},{"attributeId":14,"attributeName":"Materyal","attributeValueId":{{material}}}]}""";
        var content = new TrendyolContentObservation("100", ["A1", "B2", "C3"]);
        var lookup = new Dictionary<string, TrendyolContentObservation> { ["A1"] = content, ["B2"] = content, ["C3"] = content };

        // Live RYP007: only the size (338 Beden) differs per variant; Trendyol took the content.
        var sizesOnly = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 2, 7)}},{{Item("C3", 3, 7)}}]}""";
        Assert.True(TrendyolContentGroups.Resolve(sizesOnly, lookup).Succeeded);

        // The material differs between variants of the same content: not sent, named in the reason.
        var materialDiffers = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 2, 7)}},{{Item("C3", 3, 8)}}]}""";
        var refused = TrendyolContentGroups.Resolve(materialDiffers, lookup);
        Assert.False(refused.Succeeded);
        Assert.Equal("PRODUCT_CONTENT_GROUP_MISMATCH", refused.Error!.Code);
        Assert.Contains("özellikleri farklı: Materyal", refused.Error.Message);

        // An attribute one variant lacks is a difference too.
        var missing = """{"items":[{"barcode":"A1","title":"T","attributes":[{"attributeId":14,"attributeValueId":7}]},{"barcode":"B2","title":"T","attributes":[]},{"barcode":"C3","title":"T","attributes":[{"attributeId":14,"attributeValueId":7}]}]}""";
        Assert.Equal(["özellik 14"], TrendyolContentGroups.DifferingAttributes(((System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(missing)!["items"]!).Select(x => x!.AsObject()).ToList()));
    }

    [Fact]
    public void ABarcodeWithNoApprovedContentIsRecordedAsNotSentUnlessAnotherPhaseSentIt()
    {
        var payload = """{"items":[{"barcode":"A1","title":"T"},{"barcode":"B2","title":"T"},{"barcode":"C3","title":"T"}]}""";
        var content = new TrendyolContentObservation("100", ["A1", "B2"]);
        var unresolved = TrendyolContentGroups.Unresolved(payload, new Dictionary<string, TrendyolContentObservation> { ["A1"] = content, ["b2"] = content });
        Assert.Equal(["C3"], unresolved);
        // Resolve leaves the same barcode out of the content request.
        var resolved = TrendyolContentGroups.Resolve(payload, new Dictionary<string, TrendyolContentObservation> { ["A1"] = content, ["B2"] = content });
        Assert.True(resolved.Succeeded);
        Assert.DoesNotContain("C3", resolved.Value);

        Assert.Equal(["C3"], JobVariantResultPolicy.NotSentBarcodes(unresolved, []).ToArray());
        Assert.Empty(JobVariantResultPolicy.NotSentBarcodes(unresolved, ["c3"]));
        Assert.Empty(JobVariantResultPolicy.NotSentBarcodes(null, []));
        Assert.Contains("Onaylı içerik bulunamadı", JobVariantResultPolicy.ApprovedContentNotFoundReason);
    }
}
