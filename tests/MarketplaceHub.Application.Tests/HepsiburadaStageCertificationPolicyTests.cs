using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class HepsiburadaStageCertificationPolicyTests
{
    [Theory]
    [InlineData("HEPSIBURADA", "STAGE", true)]
    [InlineData("HEPSIBURADA", "stage", true)]
    [InlineData("HEPSIBURADA", "PRODUCTION", false)]
    [InlineData("TRENDYOL", "STAGE", false)]
    [InlineData("N11", "STAGE", false)]
    public void OnlyAStageHepsiburadaConnectionIsTheSitScope(string platformCode, string environment, bool expected)
    {
        Assert.Equal(expected, HepsiburadaStageCertificationPolicy.IsStageHepsiburada(Connection(environment, platformCode)));
    }

    [Fact]
    public void StageHepsiburadaPublicationDoesNotRequireProductWriteEvidence()
    {
        var required = HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(Connection("STAGE"));

        Assert.Equal(new[] { MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite }, required);
    }

    [Fact]
    public void ProductionHepsiburadaPublicationRestsOnThePriceAndStockEvidenceTheConnectionCheckRecords()
    {
        var required = HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(Connection("PRODUCTION"));

        Assert.Equal(new[] { MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite }, required);
    }

    [Theory]
    [InlineData("HBV0000KSJL98", true)]
    [InlineData("hbv0000ksjl98", true)]
    [InlineData(" HBC00001ABCD ", true)]
    [InlineData("HB", false)]
    [InlineData("HB 0000KSJL98", false)]
    [InlineData("BS74297", false)]
    [InlineData("MERCHANT-HB-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognizesHepsiburadaSkusOnly(string? value, bool expected)
    {
        Assert.Equal(expected, HepsiburadaStageCertificationPolicy.LooksLikeHepsiburadaSku(value));
    }

    [Fact]
    public void PicksTheFirstImportedHbSkuAndSkipsMerchantSkuLinks()
    {
        var picked = HepsiburadaStageCertificationPolicy.FirstHepsiburadaSku(["MERCHANT-1", null, " HBV0000KSJL98 ", "HBC0000OTHER1"]);

        Assert.Equal("HBV0000KSJL98", picked);
        Assert.Null(HepsiburadaStageCertificationPolicy.FirstHepsiburadaSku(["MERCHANT-1", "BS74297"]));
    }

    [Theory]
    [InlineData("HEPSIBURADA", "STAGE", true, true)]
    [InlineData("HEPSIBURADA", "STAGE", false, false)]
    [InlineData("HEPSIBURADA", "PRODUCTION", true, false)]
    [InlineData("PAZARAMA", "STAGE", true, false)]
    public void OnlyAManualPayloadOnAStageHepsiburadaConnectionIsTheSitUpload(string platformCode, string environment, bool manual, bool expected)
    {
        Assert.Equal(expected, HepsiburadaStageCertificationPolicy.IsManualStagePriceInventory(Connection(environment, platformCode), manual));
    }

    [Fact]
    public void TheSitUploadRunsAsAManualStageWriteWhileTheAutomaticPathStaysClosed()
    {
        var connection = Connection("STAGE");

        Assert.True(IntegrationRuntimePolicy.AllowsExternalWrite(connection, Context(IntegrationOperation.Manual), globalWritesEnabled: true, connectionWritesEnabled: false));
        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, Context(IntegrationOperation.Automatic), globalWritesEnabled: true, connectionWritesEnabled: false));
        Assert.Empty(PriceInventoryOutboxPolicy.RequiredWritePolicies(manual: true));
    }

    [Fact]
    public void AManualPayloadOnProductionStillNeedsBothWriteSwitches()
    {
        var connection = Connection("PRODUCTION");

        Assert.False(HepsiburadaStageCertificationPolicy.IsManualStagePriceInventory(connection, manualPayload: true));
        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, Context(IntegrationOperation.Automatic), globalWritesEnabled: true, connectionWritesEnabled: false));
    }

    [Fact]
    public void ReadsTheInventoryUploadIdFromAPolledPriceInventoryPayload()
    {
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(Guid.NewGuid(), Guid.NewGuid(), "POLL", "hash", "{}", [], "LISTING_INVENTORY:upload-123", DateTimeOffset.UtcNow, Manual: true));

        Assert.Equal("upload-123", HepsiburadaStageCertificationPolicy.OperationIdForDisplay(payload));
    }

    [Fact]
    public void ReadsTheTrackingIdFromAPolledProductImportPayload()
    {
        var payload = JsonSerializer.Serialize(new ProductPublicationJobPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "POLL", "hash", "{}", "tracking-42", DateTimeOffset.UtcNow));

        Assert.Equal("tracking-42", HepsiburadaStageCertificationPolicy.OperationIdForDisplay(payload));
    }

    // Every marketplace's batch or tracking id shows in İşlem Takibi, without its internal marker.
    [Theory]
    [InlineData("{\"ExternalOperationId\":\"5f1c2d3e-trendyol-batch\"}", "5f1c2d3e-trendyol-batch")]
    [InlineData("{\"ExternalOperationId\":\"t:6ad9e17a2be2a800019acbc0\"}", "6ad9e17a2be2a800019acbc0")]
    [InlineData("{\"ExternalOperationId\":\"b:BATCH-9:12:eyJ4IjoxfQ==\"}", "BATCH-9")]
    [InlineData("{\"ExternalOperationId\":\"PRODUCT_UPDATE:tracking-7\"}", "tracking-7")]
    public void ReadsEachMarketplacesBatchId(string payloadJson, string expected)
    {
        Assert.Equal(expected, HepsiburadaStageCertificationPolicy.OperationIdForDisplay(payloadJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"ExternalOperationId\":null}")]
    [InlineData("{\"ExternalOperationId\":\"LISTING_INVENTORY:\"}")]
    [InlineData("{\"ExternalOperationId\":\"c:eyJ4IjoxfQ==\"}")]
    public void ReadsNothingWhenThePayloadCarriesNoOperationId(string? payloadJson)
    {
        Assert.Null(HepsiburadaStageCertificationPolicy.OperationIdForDisplay(payloadJson));
    }

    [Fact]
    public void StageTestOrderPayloadStaysCompatibleWithJobsQueuedBeforeTheHbSkuField()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var legacy = JsonSerializer.Deserialize<StageTestOrderJobPayload>("{\"JobId\":\"0190f3a0-0000-7000-8000-000000000001\",\"ActorUserId\":\"0190f3a0-0000-7000-8000-000000000002\",\"Barcode\":\"9900000000486\",\"StartedAt\":\"2026-10-21T10:00:00+00:00\"}", options);
        var sit = JsonSerializer.Deserialize<StageTestOrderJobPayload>(JsonSerializer.Serialize(new StageTestOrderJobPayload(Guid.NewGuid(), Guid.NewGuid(), "HBV0000KSJL98", DateTimeOffset.UtcNow, "HBV0000KSJL98")), options);

        Assert.NotNull(legacy);
        Assert.Null(legacy.HepsiburadaSku);
        Assert.Equal("HBV0000KSJL98", sit!.HepsiburadaSku);
    }

    [Fact]
    public void TheSitBatchShapeIsAcceptedByTheInventoryUploadValidator()
    {
        var payload = JsonSerializer.Serialize(new
        {
            merchantId = "merchant-17",
            items = new[]
            {
                new { hepsiburadaSku = "HBV0000KSJL98", merchantSku = "SKU-1", price = 129.90m, availableStock = 4 },
                new { hepsiburadaSku = "HBC0000OTHER1", merchantSku = "SKU-2", price = 10m, availableStock = 0 }
            }
        });

        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(payload, "merchant-17"));
    }

    [Fact]
    public void ListingSkuPairsReadTheHepsiburadaSkuFieldOfTheListingService()
    {
        using var document = JsonDocument.Parse("""
            {"totalCount":3,"listings":[
              {"hepsiburadaSku":"HBCV00000ABC1","merchantSku":"HBV000013JHLHL","price":0},
              {"HepsiburadaSku":"HBCV00000ABC2","MerchantSku":"SKU-2"},
              {"merchantSku":"NO-HBSKU"}]}
            """);

        var pairs = HepsiburadaJsonMapper.ListingSkuPairs(HepsiburadaJsonMapper.ListingPage(document.RootElement).Items);

        Assert.Equal(new[] { ("HBV000013JHLHL", "HBCV00000ABC1"), ("SKU-2", "HBCV00000ABC2") }, pairs);
    }

    [Fact]
    public void InventoryUploadTakesTheListingHbSkuForAKnownMerchantSkuAndKeepsTheRest()
    {
        const string payload = """{"merchantId":"merchant-17","items":[{"hepsiburadaSku":"HBV000013JHLHL","merchantSku":"HBV000013JHLHL","price":100.00,"availableStock":10},{"hepsiburadaSku":"HBV0000OTHER","merchantSku":"HBV0000OTHER","price":100.00,"availableStock":0}]}""";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["HBV000013JHLHL"] = "HBCV00000ABC1" };

        var rewritten = HepsiburadaHttpClient.WithHepsiburadaSkus(payload, map);

        using var document = JsonDocument.Parse(rewritten);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal("HBCV00000ABC1", items[0].GetProperty("hepsiburadaSku").GetString());
        Assert.Equal("HBV000013JHLHL", items[0].GetProperty("merchantSku").GetString());
        Assert.Equal("HBV0000OTHER", items[1].GetProperty("hepsiburadaSku").GetString());
        Assert.True(HepsiburadaHttpClient.ValidPriceInventoryPayload(rewritten, "merchant-17"));
        Assert.Same(payload, HepsiburadaHttpClient.WithHepsiburadaSkus(payload, new Dictionary<string, string>()));
    }

    private static PlatformConnection Connection(string environment, string platformCode = "HEPSIBURADA") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        PublicId = Guid.NewGuid(),
        PlatformCode = platformCode,
        Environment = environment,
        DisplayName = "SIT HEPSİBURADA",
        ExternalStoreId = "merchant-17",
        Status = "ACTIVE",
        ApiVersion = "v1",
        LastErrorCode = null
    };

    private static AdapterContext Context(IntegrationOperation operation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow.AddMinutes(1), Operation: operation);
}
