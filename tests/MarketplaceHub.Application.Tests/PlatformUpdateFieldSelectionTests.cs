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
          {"barcode":"B2","stockCode":"S2","title":"Ad","description":"Açıklama","images":[{"url":"https://x/1.jpg"}],"attributes":[{"attributeId":1,"attributeValueId":3},{"attributeId":47,"attributeValueId":9}],"dimensionalWeight":2,"origin":"TR","vatRate":20,"quantity":4,"listPrice":150,"salePrice":120}
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
    [InlineData(new[] { "WARRANTY" }, false, false, false, true)]
    [InlineData(new[] { "warranty", "ORIGIN" }, false, false, false, true)]
    public void Fields_MapToTheRequestsThatCarryThem(string[] fields, bool content, bool variants, bool delivery, bool hepsiburada)
    {
        var sections = ProductUpdateFields.Sections(fields);
        Assert.True(sections.Succeeded, sections.Error?.Message);
        Assert.Equal(new ProductUpdateSections(content, variants, delivery, hepsiburada), sections.Value! with { ContentParts = null });
        Assert.Equal(content || variants || delivery, sections.Value!.Trendyol);
        Assert.Equal(content, sections.Value.ContentFields.Any);
    }

    [Fact]
    public void ContentPicks_AreKeptFieldByField()
    {
        Assert.Equal(new ProductContentParts(false, false, true, false), ProductUpdateFields.Sections(["IMAGES"]).Value!.ContentFields);
        Assert.Equal(new ProductContentParts(true, false, false, true), ProductUpdateFields.Sections(["TITLE", "ATTRIBUTES", "DESI"]).Value!.ContentFields);
        Assert.Equal(ProductContentParts.None, ProductUpdateFields.Sections(["DESI"]).Value!.ContentFields);
        // No field list (an older client) is every content field, as before.
        Assert.Equal(ProductContentParts.All, ProductUpdateFields.Sections(null).Value!.ContentFields);
    }

    // Owner, 6.11.2026: Görseller alone for RYP007 sent the title, the description and all 18 attributes too.
    [Fact]
    public void ApprovedTrendyol_ImagesOnly_SendsTheBarcodeAndImagesAlone()
    {
        var publication = Split("123", ProductUpdateFields.Sections(["IMAGES"]).Value!);
        var content = Items(publication.ApprovedContentPayloadJson);
        Assert.Equal(2, content.Count);
        Assert.All(content, item => Assert.Equal(["barcode", "images"], item!.AsObject().Select(property => property.Key)));
        Assert.Equal("https://x/1.jpg", content[0]!["images"]![0]!["url"]!.GetValue<string>());
        Assert.Empty(Items(publication.ApprovedVariantPayloadJson));
        Assert.Empty(Items(publication.ApprovedDeliveryPayloadJson));
        // Grouped per Trendyol content, the request is the contentId and the images: nothing else, no null field.
        var resolved = MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolContentGroups.Resolve(publication.ApprovedContentPayloadJson, new Dictionary<string, MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolContentObservation>
        {
            ["B1"] = new("42", ["B1", "B2"]),
            ["B2"] = new("42", ["B1", "B2"])
        });
        Assert.True(resolved.Succeeded, resolved.Error?.Message);
        var item = Assert.Single(Items(resolved.Value!))!.AsObject();
        Assert.Equal(["contentId", "images"], item.Select(property => property.Key));
        Assert.True(MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolContentGroups.IsResolved(resolved.Value!));
    }

    [Fact]
    public void ApprovedTrendyol_AttributesPicked_SendTheWholeAttributeList()
    {
        var publication = Split("123", ProductUpdateFields.Sections(["ATTRIBUTES"]).Value!);
        var content = Items(publication.ApprovedContentPayloadJson);
        Assert.All(content, item => Assert.Equal(["barcode", "attributes"], item!.AsObject().Select(property => property.Key)));
        Assert.Equal(3, content[1]!["attributes"]![0]!["attributeValueId"]!.GetValue<int>());
        Assert.Equal(2, content[1]!["attributes"]!.AsArray().Count);
    }

    [Fact]
    public void ApprovedTrendyol_TitleAndDescription_LeaveImagesAndAttributesOut()
    {
        var publication = Split("123", ProductUpdateFields.Sections(["TITLE", "DESCRIPTION"]).Value!);
        Assert.All(Items(publication.ApprovedContentPayloadJson), item => Assert.Equal(["barcode", "title", "description"], item!.AsObject().Select(property => property.Key)));
    }

    [Fact]
    public void ApprovedTrendyol_NoContentPick_MakesNoContentRequest()
    {
        var publication = Split("123", ProductUpdateFields.Sections(["DESI", "DELIVERY"]).Value!);
        Assert.Empty(Items(publication.ApprovedContentPayloadJson));
        Assert.Equal(2, Items(publication.ApprovedVariantPayloadJson).Count);
    }

    [Fact]
    public void ApprovedTrendyol_PickedFieldTheCardLacks_IsLeftOut_NotSentNull()
    {
        var withoutDescription = """{"items":[{"barcode":"B1","title":"Ad","images":[{"url":"https://x/1.jpg"}]}]}""";
        var picked = ProductUpdateComposer.Split(Guid.CreateVersion7(), "123", withoutDescription, ProductUpdateFields.Sections(["TITLE", "DESCRIPTION"]).Value!);
        Assert.True(picked.Succeeded, picked.Error?.Message);
        Assert.Equal(["barcode", "title"], Assert.Single(Items(picked.Value!.ApprovedContentPayloadJson))!.AsObject().Select(property => property.Key));
        // Only a picked field the card does not have: there is nothing to send.
        var nothing = ProductUpdateComposer.Split(Guid.CreateVersion7(), "123", withoutDescription, ProductUpdateFields.Sections(["DESCRIPTION"]).Value!);
        Assert.Equal("PRODUCT_UPDATE_NOTHING_TO_SEND", nothing.Error?.Code);
    }

    [Fact]
    public void Warranty_GoesOnlyInTheHepsiburadaPackage()
    {
        var warranty = ProductUpdateFields.Sections(["WARRANTY"]).Value!;
        Assert.True(warranty.Hepsiburada);
        Assert.False(warranty.Trendyol);
        Assert.True(warranty.Any);
        // Delivery alone: no Hepsiburada product package (its dispatch time goes apart), still a Trendyol request.
        var delivery = ProductUpdateFields.Sections(["DELIVERY"]).Value!;
        Assert.False(delivery.Hepsiburada);
        Assert.True(delivery.Delivery);
        Assert.True(delivery.Trendyol);
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
    [InlineData("HEPSIBURADA", "TITLE,DESCRIPTION,IMAGES,ATTRIBUTES,WARRANTY,DESI,VAT_RATE,DELIVERY")]
    [InlineData("N11", "DESCRIPTION,ATTRIBUTES,VAT_RATE,DELIVERY")]
    [InlineData(" n11", "DESCRIPTION,ATTRIBUTES,VAT_RATE,DELIVERY")]
    [InlineData("PAZARAMA", "VAT_RATE")]
    [InlineData("PTTAVM", "TITLE,DESCRIPTION,IMAGES,WARRANTY,DESI,VAT_RATE,DELIVERY")]
    [InlineData("SHOPIFY", "TITLE,DESCRIPTION,IMAGES")]
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
        // The content goes per barcode, without a contentId: the job groups it per Trendyol content before the send.
        var content = Items(publication.ApprovedContentPayloadJson);
        Assert.Equal(["B1", "B2"], content.Select(item => item!["barcode"]!.GetValue<string>()));
        Assert.All(content, item => Assert.False(item!.AsObject().ContainsKey("contentId")));
        Assert.Equal("Ad", content[0]!["title"]!.GetValue<string>());
        Assert.Equal(3, content[1]!["attributes"]![0]!["attributeValueId"]!.GetValue<int>());
        Assert.True(MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolContentGroups.NeedsResolution(publication.ApprovedContentPayloadJson));
        Assert.Equal(2, Items(publication.ApprovedVariantPayloadJson).Count);
        Assert.Single(Items(publication.ApprovedDeliveryPayloadJson));
    }

    [Fact]
    public void ApprovedTrendyol_ContentOnly_LeavesTheVariantAndDeliveryRequestsEmpty()
    {
        var publication = Split("123", new(true, false, false));
        Assert.Equal(2, Items(publication.ApprovedContentPayloadJson).Count);
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
    public void TrendyolColourSplit_SendsVariantsAsApproved_AndTheContentPerBarcode()
    {
        // Trendyol approved the panel product as one content per colour: no content is the product's, so it is not sent
        // as an unapproved card either. Its content goes per barcode, grouped per Trendyol content by the job (RYP006,
        // 6.11.2026: it was refused as PRODUCT_CONTENT_SPLIT before).
        var variantsOnly = ProductUpdateComposer.Split(Guid.CreateVersion7(), null, CreatePayload, new(false, true, false), splitContents: true);
        Assert.True(variantsOnly.Succeeded, variantsOnly.Error?.Message);
        Assert.Equal("APPROVED", variantsOnly.Value!.Mode);
        Assert.Empty(Items(variantsOnly.Value.ApprovedContentPayloadJson));
        Assert.Equal(["B1", "B2"], Items(variantsOnly.Value.ApprovedVariantPayloadJson).Select(item => item!["barcode"]!.GetValue<string>()));

        var withContent = ProductUpdateComposer.Split(Guid.CreateVersion7(), null, CreatePayload, ProductUpdateSections.All, splitContents: true);
        Assert.True(withContent.Succeeded, withContent.Error?.Message);
        Assert.Equal("APPROVED", withContent.Value!.Mode);
        Assert.Equal(["B1", "B2"], Items(withContent.Value.ApprovedContentPayloadJson).Select(item => item!["barcode"]!.GetValue<string>()));
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
