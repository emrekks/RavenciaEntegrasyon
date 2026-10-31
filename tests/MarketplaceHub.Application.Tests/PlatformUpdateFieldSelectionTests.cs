using System.Text.Json.Nodes;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Platformlarda Güncelle" field picks: which product information requests and which price/stock parts go.
public sealed class PlatformUpdateFieldSelectionTests
{
    private const string CreatePayload = """
        {"items":[
          {"barcode":"B1","stockCode":"S1","title":"Ad","description":"Açıklama","images":[{"url":"https://x/1.jpg"}],"attributes":[{"attributeId":1,"attributeValueId":2}],"dimensionalWeight":2,"origin":"TR","vatRate":20,"quantity":5,"listPrice":150,"salePrice":120,"deliveryOption":{"deliveryDuration":2}},
          {"barcode":"B2","stockCode":"S2","title":"Ad","description":"Açıklama","images":[{"url":"https://x/1.jpg"}],"attributes":[{"attributeId":1,"attributeValueId":3}],"dimensionalWeight":2,"origin":"TR","vatRate":20,"quantity":4,"listPrice":150,"salePrice":120}
        ]}
        """;

    [Fact]
    public void NoFieldList_IsEveryRequest_AsBefore()
    {
        var sections = ProductUpdateFields.Sections(null);
        Assert.True(sections.Succeeded);
        Assert.Equal(ProductUpdateSections.All, sections.Value);
    }

    [Theory]
    [InlineData(new[] { "TITLE" }, true, false, false, true)]
    [InlineData(new[] { "images", " attributes " }, true, false, false, true)]
    [InlineData(new[] { "DESI" }, false, true, false, true)]
    [InlineData(new[] { "DELIVERY" }, false, false, true, false)]
    // A page opened before Menşei left the dialog still sends ORIGIN beside the others: it is dropped, not refused.
    [InlineData(new[] { "DESI", "ORIGIN" }, false, true, false, true)]
    [InlineData(new[] { "DESCRIPTION", "VAT_RATE", "DELIVERY" }, true, true, true, true)]
    public void Fields_MapToTheRequestsThatCarryThem(string[] fields, bool content, bool variants, bool delivery, bool hepsiburada)
    {
        var sections = ProductUpdateFields.Sections(fields);
        Assert.True(sections.Succeeded, sections.Error?.Message);
        Assert.Equal(new ProductUpdateSections(content, variants, delivery, hepsiburada), sections.Value);
    }

    [Fact]
    public void EmptyOrUnknownFields_AreRefused()
    {
        Assert.Equal("PRODUCT_UPDATE_FIELDS_REQUIRED", ProductUpdateFields.Sections([]).Error?.Code);
        Assert.Equal("PRODUCT_UPDATE_FIELD_UNKNOWN", ProductUpdateFields.Sections(["TITLE", "PRICE"]).Error?.Code);
        Assert.Equal("PRODUCT_UPDATE_FIELDS_REQUIRED", ProductUpdateFields.Sections(["ORIGIN"]).Error?.Code);
    }

    // The table the dialog's field list follows (platform-update-fields.ts keeps the same one).
    [Theory]
    [InlineData("TRENDYOL", "TITLE,DESCRIPTION,IMAGES,ATTRIBUTES,DESI,VAT_RATE,DELIVERY")]
    [InlineData("trendyol ", "TITLE,DESCRIPTION,IMAGES,ATTRIBUTES,DESI,VAT_RATE,DELIVERY")]
    [InlineData("HEPSIBURADA", "TITLE,DESCRIPTION,IMAGES,ATTRIBUTES,DESI,VAT_RATE")]
    [InlineData("N11", "")]
    [InlineData("PAZARAMA", "")]
    [InlineData("PTTAVM", "")]
    [InlineData("SHOPIFY", "")]
    [InlineData(null, "")]
    public void EachMarketplace_UpdatesOnlyTheFieldsItsAdapterTakes(string? platformCode, string expected)
    {
        var supported = ProductUpdateFields.SupportedBy(platformCode);
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), supported.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("ORIGIN", supported);
    }

    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(null, 3, 3)]
    [InlineData(5, 1, 5)]
    [InlineData(0, 2, 2)]
    [InlineData(null, null, null)]
    public void DeliveryDays_AreTheListingOverrideElseTheProductCard(int? listingDays, int? productDays, int? expected) =>
        Assert.Equal(expected, ProductPublicationComposer.DeliveryDays(listingDays, productDays));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void ProductDeliveryDays_AreOneToThirty(int days, bool valid)
    {
        Assert.Equal(valid, ProductDeliveryDays.IsValid(days));
        Assert.Equal(1, ProductDeliveryDays.Default);
        Assert.Equal(1, new MarketplaceHub.Domain.Product { Title = "", Description = "" }.DeliveryDays);
    }

    [Fact]
    public void ApprovedTrendyol_DeliveryOnly_SendsTheCardDaysPerBarcode()
    {
        var publication = Split("123", new(false, false, true));
        var delivery = Assert.Single(Items(publication.ApprovedDeliveryPayloadJson));
        Assert.Equal("B1", delivery!["barcode"]!.GetValue<string>());
        Assert.Equal(2, delivery["deliveryOptions"]!["deliveryDuration"]!.GetValue<int>());
        Assert.Empty(Items(publication.ApprovedVariantPayloadJson));
    }

    [Fact]
    public void ApprovedTrendyol_AllSections_IsTheOldSplit()
    {
        var publication = Split("123", ProductUpdateSections.All);
        Assert.Equal("APPROVED", publication.Mode);
        var content = Assert.Single(Items(publication.ApprovedContentPayloadJson));
        Assert.Equal(123, content!["contentId"]!.GetValue<long>());
        Assert.Equal("Ad", content["title"]!.GetValue<string>());
        Assert.Equal(2, Items(publication.ApprovedVariantPayloadJson).Count);
        Assert.Single(Items(publication.ApprovedDeliveryPayloadJson));
    }

    [Fact]
    public void ApprovedTrendyol_ContentOnly_LeavesTheVariantAndDeliveryRequestsEmpty()
    {
        var publication = Split("123", new(true, false, false));
        Assert.Single(Items(publication.ApprovedContentPayloadJson));
        Assert.Empty(Items(publication.ApprovedVariantPayloadJson));
        Assert.Empty(Items(publication.ApprovedDeliveryPayloadJson));
    }

    [Fact]
    public void ApprovedTrendyol_VariantsOnly_SendsDesiVatAndOriginPerBarcode()
    {
        var publication = Split("123", new(false, true, false));
        Assert.Empty(Items(publication.ApprovedContentPayloadJson));
        var variants = Items(publication.ApprovedVariantPayloadJson);
        Assert.Equal(["B1", "B2"], variants.Select(item => item!["barcode"]!.GetValue<string>()));
        Assert.All(variants, item => Assert.Equal(2, item!["dimensionalWeight"]!.GetValue<int>()));
        // Menşei left the dialog, but the variant request still carries the listing's origin whole.
        Assert.All(variants, item => Assert.Equal("TR", item!["origin"]!.GetValue<string>()));
        Assert.Empty(Items(publication.ApprovedDeliveryPayloadJson));
    }

    [Fact]
    public void Picks_ChangeTheHash_SoEachPickIsAJobOfItsOwn()
    {
        Assert.NotEqual(Split("123", ProductUpdateSections.All).PayloadHash, Split("123", new(true, false, false)).PayloadHash);
        Assert.Equal(Split("123", new(true, false, false)).PayloadHash, Split("123", new(true, false, false)).PayloadHash);
    }

    [Fact]
    public void ApprovedTrendyol_DeliveryOnly_WithoutADeliveryTime_MakesNoJob()
    {
        var withoutDelivery = """{"items":[{"barcode":"B1","title":"Ad","dimensionalWeight":2}]}""";
        var result = ProductUpdateComposer.Split(Guid.CreateVersion7(), "123", withoutDelivery, new(false, false, true));
        Assert.Equal("PRODUCT_UPDATE_NOTHING_TO_SEND", result.Error?.Code);
    }

    [Fact]
    public void UnapprovedTrendyol_SendsTheWholeCard_WhateverWasPicked()
    {
        var publication = Split(null, new(true, false, false));
        Assert.Equal("UNAPPROVED", publication.Mode);
        Assert.Equal(CreatePayload, publication.UnapprovedPayloadJson);
        Assert.Empty(Items(publication.ApprovedContentPayloadJson));
    }

    [Fact]
    public void PriceOrStockHeld_IsLeftOutOfTheRequest()
    {
        var variantId = Guid.CreateVersion7();
        var offerId = Guid.CreateVersion7();
        var stockHeld = new PriceInventoryPushLine(variantId, offerId, "B1", 5m, 150m, 120m, "TRY", 2, 3, "h", "SKU-1", "HBV1", QuantityOmitted: true, StockHeld: true);
        var priceHeld = stockHeld with { QuantityOmitted = false, StockHeld = false, PriceOmitted = true, PriceHeld = true };

        var trendyolPrice = JsonNode.Parse(PriceInventoryComposer.TrendyolPayload([stockHeld]))!["items"]![0]!.AsObject();
        Assert.False(trendyolPrice.ContainsKey("quantity"));
        Assert.Equal(120m, trendyolPrice["salePrice"]!.GetValue<decimal>());
        var trendyolStock = JsonNode.Parse(PriceInventoryComposer.TrendyolPayload([priceHeld]))!["items"]![0]!.AsObject();
        Assert.False(trendyolStock.ContainsKey("salePrice"));
        Assert.Equal(5, trendyolStock["quantity"]!.GetValue<int>());

        var hepsiburadaPrice = JsonNode.Parse(PriceInventoryComposer.HepsiburadaPayload("m", [stockHeld]))!["items"]![0]!.AsObject();
        Assert.False(hepsiburadaPrice.ContainsKey("availableStock"));
        Assert.True(hepsiburadaPrice.ContainsKey("price"));
        var hepsiburadaStock = JsonNode.Parse(PriceInventoryComposer.HepsiburadaPayload("m", [priceHeld]))!["items"]![0]!.AsObject();
        Assert.False(hepsiburadaStock.ContainsKey("price"));
        Assert.True(hepsiburadaStock.ContainsKey("availableStock"));
    }

    private static ProductUpdatePublication Split(string? contentId, ProductUpdateSections sections)
    {
        var result = ProductUpdateComposer.Split(Guid.Parse("0192c0de-0000-7000-8000-000000000001"), contentId, CreatePayload, sections);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static JsonArray Items(string payloadJson) => JsonNode.Parse(payloadJson)!["items"]!.AsArray();
}
