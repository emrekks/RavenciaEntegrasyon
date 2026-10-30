using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The price/stock request rules the marketplaces' documents set (checked 30 Oct 2026).
public sealed class PriceInventoryDocComplianceTests
{
    private const string Barcode = "8680000000001";
    private const string PriceHash = "PRICE-V2";

    private static ChannelOffer Offer(string? lastPriceHash = "PRICE-V1", long? lastProjection = 1, string? lastCode = Barcode, decimal? lastQuantity = 5, decimal? lastSale = 120m, decimal? lastList = 150m) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        VariantId = Guid.NewGuid(),
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = "TRY",
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = "ACTIVE",
        LastPriceHash = lastPriceHash,
        LastStockProjectionVersion = lastProjection,
        LastSentCode = lastCode,
        LastSentQuantity = lastQuantity,
        LastSentSalePrice = lastSale,
        LastSentListPrice = lastList
    };

    private static PriceInventoryPushLine Line(decimal quantity = 5, decimal sale = 120m, decimal list = 150m, bool quantityOmitted = false, bool priceOmitted = false, string barcode = Barcode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), barcode, quantity, list, sale, "TRY", 2, 2, PriceHash, QuantityOmitted: quantityOmitted, PriceOmitted: priceOmitted);

    // Rule 1: a new version carrying the values Trendyol last confirmed sends nothing.
    [Fact]
    public void Trendyol_NewVersionsWithTheConfirmedValues_SendNothing()
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(), Barcode, 5, 120m, 150m, PriceHash, 2);

        Assert.False(fields.Any);
    }

    // Rule 1: only the changed field goes; the prices stay out of a stock change (30 price requests a minute per barcode).
    [Fact]
    public void Trendyol_AStockChangeSendsTheQuantityOnly()
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(), Barcode, 4, 120m, 150m, PriceHash, 2);

        Assert.True(fields.Quantity);
        Assert.False(fields.Price);
    }

    [Fact]
    public void Trendyol_APriceChangeSendsThePricesOnly()
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(), Barcode, 5, 110m, 150m, PriceHash, 2);

        Assert.False(fields.Quantity);
        Assert.True(fields.Price);
    }

    // A field whose version mark did not move is not sent, even when no value was confirmed yet.
    [Fact]
    public void Trendyol_AnUnmovedMarkSendsNothingForItsField()
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(lastPriceHash: PriceHash, lastCode: null), Barcode, 9, 120m, 150m, PriceHash, 2);

        Assert.True(fields.Quantity);
        Assert.False(fields.Price);
    }

    // A mark cleared on purpose (a reset, a new address), another barcode or a value never confirmed always sends.
    [Theory]
    [InlineData(null, null, Barcode)]
    [InlineData("PRICE-V1", 1L, "OTHER-BARCODE")]
    [InlineData("PRICE-V1", 1L, null)]
    public void Trendyol_ClearedMarksOrAnotherCodeSendEverythingMarked(string? lastPriceHash, long? lastProjection, string? lastCode)
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(lastPriceHash, lastProjection, lastCode), Barcode, 5, 120m, 150m, PriceHash, 2);

        Assert.True(fields.Quantity);
        Assert.True(fields.Price);
    }

    [Fact]
    public void Trendyol_AClearedStockMarkSendsTheSameQuantityAgain()
    {
        var fields = PriceInventorySendPolicy.TrendyolFields(Offer(lastProjection: null), Barcode, 5, 120m, 150m, PriceHash, 2);

        Assert.True(fields.Quantity);
        Assert.False(fields.Price);
    }

    [Fact]
    public void Trendyol_PayloadLeavesOutTheFieldsNotSent()
    {
        var json = PriceInventoryComposer.TrendyolPayload([Line(quantity: 4, priceOmitted: true), Line(sale: 99.9m, quantityOmitted: true, barcode: "B2"), Line(quantity: 7, barcode: "B3")]);

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(4, items[0].GetProperty("quantity").GetInt32());
        Assert.False(items[0].TryGetProperty("salePrice", out _));
        Assert.False(items[0].TryGetProperty("listPrice", out _));
        Assert.False(items[1].TryGetProperty("quantity", out _));
        Assert.Equal(99.9m, items[1].GetProperty("salePrice").GetDecimal());
        Assert.Equal(150m, items[1].GetProperty("listPrice").GetDecimal());
        Assert.Equal(["barcode", "quantity", "salePrice", "listPrice"], items[2].EnumerateObject().Select(x => x.Name).ToArray());
    }

    // Rule 1/5: a line the marketplace accepted becomes the confirmed value; a field left out keeps the one before.
    [Fact]
    public void RecordSent_KeepsTheConfirmedValueOfAFieldLeftOut()
    {
        var offer = Offer();
        var sentAt = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.FromHours(3));

        PriceInventorySendPolicy.RecordSent(offer, Line(quantity: 3, priceOmitted: true), sentAt);

        Assert.Equal((Barcode, 3m, 120m, 150m), (offer.LastSentCode, offer.LastSentQuantity, offer.LastSentSalePrice, offer.LastSentListPrice));
        Assert.Equal(TimeSpan.Zero, offer.LastSentAt!.Value.Offset);
    }

    [Fact]
    public void RecordSent_ForgetsTheValuesOfAnotherCode()
    {
        var offer = Offer();

        PriceInventorySendPolicy.RecordSent(offer, Line(quantity: 3, priceOmitted: true, barcode: "NEW-BARCODE"), DateTimeOffset.UtcNow);

        Assert.Equal(("NEW-BARCODE", 3m, (decimal?)null, (decimal?)null), (offer.LastSentCode, offer.LastSentQuantity, offer.LastSentSalePrice, offer.LastSentListPrice));
    }

    // Rule 1/5: a repeat refusal means "already held" only for the values last confirmed under the same code.
    [Fact]
    public void MatchesLastSent_ComparesOnlyTheFieldsSent()
    {
        var offer = Offer();

        Assert.True(PriceInventorySendPolicy.MatchesLastSent(offer, Line()));
        Assert.True(PriceInventorySendPolicy.MatchesLastSent(offer, Line(sale: 1m, priceOmitted: true)));
        Assert.False(PriceInventorySendPolicy.MatchesLastSent(offer, Line(quantity: 6)));
        Assert.False(PriceInventorySendPolicy.MatchesLastSent(offer, Line(barcode: "OTHER")));
        Assert.False(PriceInventorySendPolicy.MatchesLastSent(Offer(lastCode: null), Line()));
    }

    // Rule 1: the 15-minute repeat refusal is a wait for this request, not a refusal of the values.
    [Theory]
    [InlineData("""{"errors":[{"key":"x","message":"15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!"}]}""")]
    [InlineData("""{"errors":[{"message":"15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!"}]}""")]
    public void Trendyol_RepeatRefusalIsAWait(string body)
    {
        var error = TrendyolErrorMapper.RepeatRequest(HttpStatusCode.BadRequest, body, "req-1");

        Assert.NotNull(error);
        Assert.Equal(TrendyolStockRules.RepeatRequestWaitCode, error!.Code);
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
        var job = JobExecutionResult.FromAdapterError(error);
        Assert.Equal(JobCompletionKind.Retry, job.Kind);
        Assert.True(job.RefundAttempt);
        Assert.Equal(TimeSpan.FromMinutes(15), job.RetryAfter);
    }

    [Fact]
    public void Trendyol_OtherRefusalsAndAuthAnswersAreNotRepeats()
    {
        Assert.Null(TrendyolErrorMapper.RepeatRequest(HttpStatusCode.BadRequest, """{"errors":[{"message":"Barkod bulunamadı"}]}""", null));
        Assert.Null(TrendyolErrorMapper.RepeatRequest(HttpStatusCode.Unauthorized, "15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!", null));
        Assert.Null(TrendyolErrorMapper.RepeatRequest(HttpStatusCode.InternalServerError, "15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!", null));
    }

    // Rule 1: a price-and-inventory batch has no batch-level status; its item statuses decide.
    [Fact]
    public void Trendyol_PriceBatchWithoutStatusIsCompleteWhenEveryItemIsFinal()
    {
        var status = TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","itemCount":2,"failedItemCount":1,"sourceType":"API","items":[{"requestItem":{"barcode":"A","quantity":1},"status":"SUCCESS","failureReasons":[]},{"requestItem":{"barcode":"B","quantity":2},"status":"FAILED","failureReasons":["Ürün bulunamadı"]}]}""", "b-1");

        Assert.Equal("COMPLETED", status.Status);
        Assert.True(status.Lines.Single(x => x.ExternalKey == "A").Succeeded);
        Assert.False(status.Lines.Single(x => x.ExternalKey == "B").Succeeded);
    }

    [Theory]
    [InlineData("""{"batchRequestId":"b-1","itemCount":2,"items":[]}""")]
    [InlineData("""{"batchRequestId":"b-1","itemCount":2,"items":[{"requestItem":{"barcode":"A"},"status":"SUCCESS","failureReasons":[]}]}""")]
    [InlineData("""{"batchRequestId":"b-1","items":[{"requestItem":{"barcode":"A"},"status":"IN_PROGRESS","failureReasons":[]}]}""")]
    public void Trendyol_PriceBatchWithoutStatusIsRunningUntilEveryItemIsFinal(string json) =>
        Assert.Equal("IN_PROGRESS", TrendyolJsonMapper.Batch(json, "b-1").Status);

    [Fact]
    public void Trendyol_ABatchStatusTheAnswerGivesIsKept() =>
        Assert.Equal("REJECTED", TrendyolJsonMapper.Batch("""{"batchRequestId":"b-1","status":"REJECTED","items":[]}""", "b-1").Status);

    // Rule 2: n11depom SKUs (sender N11) take the price only.
    [Theory]
    [InlineData("N11", true)]
    [InlineData("n11", true)]
    [InlineData("SELLER", false)]
    [InlineData(null, null)]
    [InlineData("ALL", null)]
    public void N11_SenderTellsTheDepot(string? sender, bool? depot)
    {
        var senderJson = sender is null ? "" : $",\"sender\":\"{sender}\"";
        using var json = JsonDocument.Parse($$"""{"n11ProductId":1,"stockCode":"SC-1","productMainId":"M-1","title":"T","status":"Active","salePrice":10,"listPrice":12,"quantity":3{{senderJson}}}""");

        var product = N11ProductMapper.Product(json.RootElement)!;

        Assert.Equal(depot, product.Variants.Single().FulfilledByMarketplace);
    }

    [Fact]
    public void N11_ADepotSkuGoesWithoutQuantity()
    {
        var json = N11PriceInventoryRules.Payload([Line(quantityOmitted: true, barcode: "SC-1"), Line(quantity: 3, barcode: "SC-2")]);

        using var document = JsonDocument.Parse(json);
        var skus = document.RootElement.GetProperty("payload").GetProperty("skus");
        Assert.False(skus[0].TryGetProperty("quantity", out _));
        Assert.Equal(120.00m, skus[0].GetProperty("salePrice").GetDecimal());
        Assert.Equal(150.00m, skus[0].GetProperty("listPrice").GetDecimal());
        Assert.Equal(3, skus[1].GetProperty("quantity").GetInt32());
    }

    // Rule 3: a listing Hepsiburada fulfils (isFulfilledByHB) goes with its price and no stock.
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Hepsiburada_ListingTellsWhoFulfils(string value, bool expected)
    {
        using var json = JsonDocument.Parse($$"""{"hepsiburadaSku":"HBCV0001","merchantSku":"SKU-1","price":120,"availableStock":4,"isFulfilledByHB":{{value}}}""");

        Assert.Equal(expected, HepsiburadaJsonMapper.CatalogProduct(json.RootElement).Variants.Single().FulfilledByMarketplace);
    }

    [Fact]
    public void Hepsiburada_AListingWithoutTheFieldIsUnknown()
    {
        using var json = JsonDocument.Parse("""{"hepsiburadaSku":"HBCV0001","merchantSku":"SKU-1","price":120,"availableStock":4}""");

        Assert.Null(HepsiburadaJsonMapper.CatalogProduct(json.RootElement).Variants.Single().FulfilledByMarketplace);
    }

    [Fact]
    public void Hepsiburada_AFulfilledListingGoesWithoutAvailableStock()
    {
        var lines = new[]
        {
            Line(quantityOmitted: true) with { HepsiburadaSku = "HBCV0001", MerchantSku = "SKU-1" },
            Line(quantity: 4) with { HepsiburadaSku = "HBCV0002", MerchantSku = "SKU-2" }
        };

        var json = PriceInventoryComposer.HepsiburadaPayload("m-1", lines);

        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(json, "m-1"));
        var listings = XDocument.Parse(HepsiburadaHttpClient.BuildInventoryUploadXml(json)).Root!.Elements("listing").ToList();
        Assert.Null(listings[0].Element("AvailableStock"));
        Assert.Equal("120,00", listings[0].Element("Price")!.Value);
        Assert.Equal("4", listings[1].Element("AvailableStock")!.Value);
    }

    [Fact]
    public void Hepsiburada_ANegativeStockIsStillRefused() =>
        Assert.False(HepsiburadaHttpClient.ValidPriceInventoryPayload("""{"merchantId":"m-1","items":[{"hepsiburadaSku":"HBCV0001","merchantSku":"SKU-1","price":10,"availableStock":-1}]}""", "m-1"));

    // Rule 4: Pazarama may answer up to four hours late; its batch is waited for eight.
    [Theory]
    [InlineData("PAZARAMA", 8)]
    [InlineData("pazarama", 8)]
    [InlineData("TRENDYOL", 4)]
    [InlineData("PTTAVM", 4)]
    [InlineData(null, 4)]
    public void PollDeadlinePerMarketplace(string? platformCode, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), PriceInventorySendPolicy.PollDeadline(platformCode));

    // Rule 5: PTT AVM's five-minute repeat refusal of a price/stock request is a six-minute wait, not a refusal.
    [Fact]
    public void PttAvm_ARepeatedStockPriceRequestWaits()
    {
        var answer = XElement.Parse("""<UpdateProductsStockPriceResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><a:Message>Aynı isteği ancak 5 dakika sonra tekrardan gönderebilirsiniz.</a:Message><a:Success>false</a:Success><a:TrackingId i:nil="true"/></UpdateProductsStockPriceResult>""");

        var result = PttAvmHttpClient.StockPriceAnswer(answer, DateTimeOffset.UtcNow, null);

        Assert.False(result.IsSuccess);
        Assert.Equal(PttAvmProductPayloadRules.RepeatRequestWaitCode, result.Error!.Code);
        var job = JobExecutionResult.FromAdapterError(result.Error);
        Assert.Equal(JobCompletionKind.Retry, job.Kind);
        Assert.Equal(TimeSpan.FromMinutes(6), job.RetryAfter);
    }

    [Fact]
    public void PttAvm_AnotherRefusalStaysFinal()
    {
        var answer = XElement.Parse("""<UpdateProductsStockPriceResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService"><a:Message>Barkod bulunamadı</a:Message><a:Success>false</a:Success></UpdateProductsStockPriceResult>""");

        var result = PttAvmHttpClient.StockPriceAnswer(answer, DateTimeOffset.UtcNow, null);

        Assert.Equal("PTTAVM_PRICE_STOCK_REJECTED", result.Error!.Code);
        Assert.Equal(JobCompletionKind.Blocked, JobExecutionResult.FromAdapterError(result.Error).Kind);
    }

    [Fact]
    public void PttAvm_ATrackingIdIsTheOperation()
    {
        var answer = XElement.Parse("""<UpdateProductsStockPriceResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService"><a:Success>true</a:Success><a:TrackingId>trk-1</a:TrackingId></UpdateProductsStockPriceResult>""");

        var result = PttAvmHttpClient.StockPriceAnswer(answer, DateTimeOffset.UtcNow, null);

        Assert.Equal("t:trk-1", result.Value!.ExternalOperationId);
    }
}
