using System.Text.Json.Nodes;
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

    // Scan 23 Nov 2026: three Pazarama question reads asked for by hand waited in review on a refused token although every
    // later question read of the store succeeded. A read in review on refused credentials closes once a later run of the
    // same read type on the same connection succeeded; nothing else does.
    [Fact]
    public void AReadInReviewOnRefusedCredentialsClosesOnceALaterRunOfTheSameReadSucceeded()
    {
        var connection = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.QuestionSync, JobStatus.ManualReview, (string?)"PAZARAMA_TOKEN_INVALID", Now.AddDays(-6));
        var later = (connection, MarketplaceJobTypes.QuestionSync, JobStatus.Succeeded, Now);
        Assert.True(ScheduledReadSupersession.CredentialsRecovered(failed, later));

        // Another review code, an earlier or another success, another connection or read type, or a write: stays.
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed with { Item4 = "REMOTE_CONTRACT_INVALID" }, later));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed with { Item4 = null }, later));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed, later with { Item4 = Now.AddDays(-7) }));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed, later with { Item3 = JobStatus.ManualReview }));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed, later with { Item1 = Guid.NewGuid() }));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed, later with { Item2 = MarketplaceJobTypes.PazaramaOrderSync }));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed with { Item2 = MarketplaceJobTypes.ShipmentAction }, later with { Item2 = MarketplaceJobTypes.ShipmentAction }));
        Assert.False(ScheduledReadSupersession.CredentialsRecovered(failed with { Item3 = JobStatus.Dead }, later));
    }

    // Scan 23 Nov 2026: a product update stopped because a newer update of the product carries a newer payload stayed
    // BLOCKED; it now ends cancelled (the newer job is the product's), with its code kept. A first publish that stopped the
    // same way still waits for the owner.
    [Fact]
    public void ASupersededProductUpdateEndsCancelled_AFirstPublishStaysBlocked()
    {
        Assert.True(JobLeaseService.CancelsWhenSuperseded("PRODUCT_UPDATE_SUPERSEDED"));
        Assert.True(JobLeaseService.CancelsWhenSuperseded("PRODUCT_ARCHIVE_SUPERSEDED"));
        Assert.True(JobLeaseService.CancelsWhenSuperseded("PRODUCT_APPROVAL_SUPERSEDED"));
        Assert.False(JobLeaseService.CancelsWhenSuperseded("PRODUCT_PUBLICATION_SUPERSEDED"));
        Assert.False(JobLeaseService.CancelsWhenSuperseded("PRICE_INVENTORY_SUPERSEDED"));
        Assert.False(JobLeaseService.CancelsWhenSuperseded(null));
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
    public void AContentsAttributeListLeavesOutTheVarianterAndNeedsEveryOtherAttributeEqual()
    {
        static string Item(string barcode, int size, int material, string colour = "Siyah") =>
            $$"""{"barcode":"{{barcode}}","title":"T","attributes":[{"attributeId":338,"attributeName":"Beden","attributeValueId":{{size}}},{"attributeId":47,"attributeName":"Renk","customAttributeValue":"{{colour}}"},{"attributeId":14,"attributeName":"Materyal","attributeValueId":{{material}}}]}""";
        var content = new TrendyolContentObservation("100", ["A1", "B2", "C3"]);
        var lookup = new Dictionary<string, TrendyolContentObservation> { ["A1"] = content, ["B2"] = content, ["C3"] = content };
        // The category attribute list as Trendyol returns it: 338 Beden varianter, 47 Renk slicer.
        var roles = TrendyolAttributeRoles.FromCategoryAttributes([
            """{"attribute":{"id":338,"name":"Beden"},"varianter":true,"slicer":false}""",
            """{"attribute":{"id":47,"name":"Renk"},"varianter":false,"slicer":true}""",
            """{"attribute":{"id":14,"name":"Materyal"},"varianter":false,"slicer":false}"""]);
        Assert.True(roles.IsVarianter("338"));
        Assert.False(roles.IsVarianter("47"));
        static JsonArray AttributesOf(string json) => (JsonArray)JsonNode.Parse(json)!["items"]![0]!["attributes"]!;

        // Identical attributes: one content with the shared list, the varianter left out.
        var identical = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 1, 7)}},{{Item("C3", 1, 7)}}]}""";
        var one = TrendyolContentGroups.Resolve(identical, lookup, roles);
        Assert.True(one.Succeeded);
        Assert.Equal([47, 14], AttributesOf(one.Value!).Select(x => x!["attributeId"]!.GetValue<int>()));

        // Live RYP007: only the size differs per variant. The content goes without any variant's size (Trendyol: a
        // varianter value of an approved product cannot be updated), not with the first variant's.
        var sizesOnly = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 2, 7)}},{{Item("C3", 3, 7)}}]}""";
        var sized = TrendyolContentGroups.Resolve(sizesOnly, lookup, roles);
        Assert.True(sized.Succeeded);
        var sent = AttributesOf(sized.Value!);
        Assert.DoesNotContain(sent, x => x!["attributeId"]!.GetValue<int>() == 338);
        Assert.Equal(2, sent.Count);

        // The material differs between variants of the same content: stopped, named in the reason.
        var materialDiffers = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 2, 7)}},{{Item("C3", 3, 8)}}]}""";
        var refused = TrendyolContentGroups.Resolve(materialDiffers, lookup, roles);
        Assert.False(refused.Succeeded);
        Assert.Equal("PRODUCT_CONTENT_GROUP_MISMATCH", refused.Error!.Code);
        Assert.Contains("özellikleri farklı: Materyal", refused.Error.Message);
        // A slicer that differs inside one content is two contents' data: stopped too.
        var colourDiffers = $$"""{"items":[{{Item("A1", 1, 7)}},{{Item("B2", 2, 7, "Haki")}},{{Item("C3", 3, 7)}}]}""";
        Assert.Contains("Renk", TrendyolContentGroups.Resolve(colourDiffers, lookup, roles).Error!.Message);

        // Without the category's flags the panel does not guess the varianter: a differing size stops the content.
        var unknown = TrendyolContentGroups.Resolve(sizesOnly, lookup, null);
        Assert.False(unknown.Succeeded);
        Assert.Contains("Beden", unknown.Error!.Message);

        // An attribute one variant lacks is a difference too.
        var missing = """{"items":[{"barcode":"A1","title":"T","attributes":[{"attributeId":14,"attributeValueId":7}]},{"barcode":"B2","title":"T","attributes":[]},{"barcode":"C3","title":"T","attributes":[{"attributeId":14,"attributeValueId":7}]}]}""";
        Assert.Contains("özellik 14", TrendyolContentGroups.Resolve(missing, lookup, roles).Error!.Message);

        // A content update that picked no attributes sends none.
        var imagesOnly = """{"items":[{"barcode":"A1","images":[{"url":"u"}]},{"barcode":"B2","images":[{"url":"u"}]},{"barcode":"C3","images":[{"url":"u"}]}]}""";
        Assert.False(TrendyolContentGroups.CarriesAttributes(imagesOnly));
        Assert.False(JsonNode.Parse(TrendyolContentGroups.Resolve(imagesOnly, lookup).Value!)!["items"]![0]!.AsObject().ContainsKey("attributes"));
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
