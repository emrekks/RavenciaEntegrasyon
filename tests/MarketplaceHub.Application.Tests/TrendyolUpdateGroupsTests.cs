using System.Text.Json.Nodes;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Who goes together in a grouped Trendyol send, the request they share, and which batch lines are whose.
public sealed class TrendyolUpdateGroupsTests
{
    private static ProductUpdateJobPayload Card(string phase, DateTimeOffset? readySince = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), phase, "APPROVED", "h", "{}", "{}", "{}", "{}", null, null, GroupReadySince: readySince);

    [Fact]
    public void ACardAtAnEarlierStep_OrNotReadyAtThisOne_IsWaitedFor()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(TrendyolUpdateGroups.IsBehind(Card("SUBMIT_CONTENT"), "SUBMIT_CONTENT"));
        Assert.False(TrendyolUpdateGroups.IsBehind(Card("SUBMIT_CONTENT", now), "SUBMIT_CONTENT"));
        Assert.True(TrendyolUpdateGroups.IsReady(Card("SUBMIT_CONTENT", now), "SUBMIT_CONTENT"));
        Assert.True(TrendyolUpdateGroups.IsBehind(Card("POLL_CONTENT"), "SUBMIT_VARIANTS"));
        Assert.True(TrendyolUpdateGroups.IsBehind(Card("SUBMIT_VARIANTS", now), "SUBMIT_DELIVERY"));
        // Past this step, sent in another request, or answered already: not waited for.
        Assert.False(TrendyolUpdateGroups.IsBehind(Card("POLL_CONTENT"), "SUBMIT_CONTENT"));
        Assert.False(TrendyolUpdateGroups.IsBehind(Card("SUBMIT_DELIVERY"), "SUBMIT_VARIANTS"));
        Assert.False(TrendyolUpdateGroups.IsBehind(Card(TrendyolUpdateGroups.DonePhase), "SUBMIT_DELIVERY"));
        Assert.False(TrendyolUpdateGroups.IsBehind(Card(TrendyolUpdateGroups.StoppedPhase), "SUBMIT_CONTENT"));
    }

    [Fact]
    public void Select_KeepsTheSender_AndLeavesOutACardNamingAContentAlreadyTaken()
    {
        var cards = new List<(string Card, string Json)>
        {
            ("a", Content(101)),
            ("b", Content(102)),
            ("c", Content(101)),
            ("d", Content(103))
        };

        Assert.Equal(["a", "b", "d"], TrendyolUpdateGroups.Select(cards));
    }

    [Fact]
    public void Select_StopsAtAThousandItems_AndAtARequestShapedOtherwise()
    {
        var big = new JsonObject { ["items"] = new JsonArray([.. Enumerable.Range(0, 999).Select(index => (JsonNode)new JsonObject { ["barcode"] = $"B{index}" })]) }.ToJsonString();
        var cards = new List<(string Card, string Json)>
        {
            ("a", big),
            ("b", """{"items":[{"barcode":"X1"},{"barcode":"X2"}]}"""),
            ("c", """{"items":[{"barcode":"Y1"}]}"""),
            ("d", """{"merchantId":"m","items":[{"barcode":"Z1"}]}""")
        };

        Assert.Equal(["a", "c"], TrendyolUpdateGroups.Select(cards));
    }

    [Fact]
    public void Merge_PutsEveryCardsItemsInOneRequest_InTheCardsOrder()
    {
        var merged = JsonNode.Parse(TrendyolUpdateGroups.Merge([Content(101), Content(102), Content(103)]))!;

        Assert.Equal([101L, 102L, 103L], merged["items"]!.AsArray().Select(item => item!["contentId"]!.GetValue<long>()));
        Assert.Equal(3, TrendyolUpdateGroups.ItemCount(merged.ToJsonString()));
    }

    [Fact]
    public void Assign_GivesEachLineToTheCardWhoseContentItNames_AndKeepsTheUnknownApart()
    {
        var keys = new List<IReadOnlyCollection<string>> { TrendyolUpdateGroups.Keys(Content(101)), TrendyolUpdateGroups.Keys(Content(102)) };
        var (byCard, unattributed) = TrendyolUpdateGroups.Assign(keys,
        [
            new RemoteOperationLine("101", true, null, null, false),
            new RemoteOperationLine("102", false, null, "Hata", false),
            new RemoteOperationLine("", false, null, "Hata", false),
            new RemoteOperationLine("999", false, null, "Hata", false)
        ]);

        Assert.Equal(["101"], byCard[0].Select(line => line.ExternalKey));
        Assert.Equal(["102"], byCard[1].Select(line => line.ExternalKey));
        Assert.Equal(2, unattributed.Count);
    }

    // The approved-product update answers nest the item (offline trendyol-api-son.md ~9472-9585): a content line is named
    // by updateRequest.contentId, a variant line by updateRequest.barcode, a delivery line by requestItem.barcode.
    [Fact]
    public void TheBatchAnswer_NamesEachLineByItsNestedContentOrBarcode()
    {
        var content = MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.Batch("""
            {"batchRequestId":"b1","status":"COMPLETED","batchRequestType":"ApprovedProductContentUpdate","items":[
              {"requestItem":{"supplierId":1,"updateRequest":{"contentId":101,"images":[]}},"status":"SUCCESS","failureReasons":[]},
              {"requestItem":{"supplierId":1,"updateRequest":{"contentId":102,"images":[]}},"status":"FAILED","failureReasons":["Görsel geçersiz"]}]}
            """, "b1");
        var variants = MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.Batch("""
            {"batchRequestId":"b2","status":"COMPLETED","items":[{"requestItem":{"supplierId":1,"updateRequest":{"barcode":"RYP003T18","vatRate":10}},"status":"FAILED","failureReasons":["KDV"]}]}
            """, "b2");
        var delivery = MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping.TrendyolJsonMapper.Batch("""
            {"batchRequestId":"b3","status":"COMPLETED","items":[{"requestItem":{"supplierId":1,"barcode":"RYP003T17","request":{"barcode":"RYP003T17"}},"status":"SUCCESS"}]}
            """, "b3");

        Assert.Equal([("101", true), ("102", false)], content.Lines.Select(line => (line.ExternalKey, line.Succeeded)));
        Assert.Equal("RYP003T18", Assert.Single(variants.Lines).ExternalKey);
        Assert.Equal("RYP003T17", Assert.Single(delivery.Lines).ExternalKey);
    }

    [Fact]
    public void AStoredOutcome_IsTheSameResultOnTheCardsOwnRun()
    {
        var outcome = TrendyolUpdateGroups.Outcome(JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "Süre doldu.", "batch-1"), "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED");
        var result = TrendyolUpdateGroups.Result(outcome);

        Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
        Assert.Equal(("PRODUCT_UPDATE_BATCH_EXPIRED", "Süre doldu.", "batch-1"), (result.ErrorCode, result.ErrorSummary, result.RemoteRequestId));
    }

    [Fact]
    public void TheDrawer_ShowsHowManyCardsAPartsRequestCarried()
    {
        var payload = Card("POLL_CONTENT") with
        {
            ApprovedContentPayloadJson = Content(101),
            ApprovedVariantPayloadJson = """{"items":[]}""",
            ApprovedDeliveryPayloadJson = """{"items":[]}""",
            SharedRequests = [new ProductUpdateSharedRequest("CONTENT", "batch-1", 6, 6, Guid.NewGuid())]
        };

        var write = JobWritePayloadReader.Read(MarketplaceJobTypes.ProductUpdate, System.Text.Json.JsonSerializer.Serialize(payload))!;

        var content = Assert.Single(write.Parts, part => part.Key == "CONTENT");
        Assert.Equal((6, 6), (content.SharedCards, content.SharedItems));
        Assert.All(write.Parts.Where(part => part.Key != "CONTENT"), part => Assert.Null(part.SharedCards));
    }

    private static string Content(long contentId) =>
        new JsonObject { ["items"] = new JsonArray(new JsonObject { ["contentId"] = contentId, ["images"] = new JsonArray(new JsonObject { ["url"] = $"https://img/{contentId}.jpg" }) }) }.ToJsonString();
}
