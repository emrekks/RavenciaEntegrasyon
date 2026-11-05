using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// T2-D30: one sale state for every marketplace, read beside the publication state. A listing the marketplace closed to
// sale is OFF_SALE; one whose answer does not say is unknown (null), never inferred.
public sealed class ListingSaleStateTests
{
    // Trendyol: variants[].onSale of the approved v2 list (Ürün Filtreleme - Onaylı Ürün v2).
    [Fact]
    public void Trendyol_ApprovedVariantsCarryOnSaleAndTheReasonTheSameAnswerGives()
    {
        const string json = """
            {"page":0,"size":50,"totalPages":1,"totalElements":1,"content":[{"contentId":1,"productMainId":"M1","title":"T","variants":[
              {"variantId":11,"barcode":"B11","stockCode":"S","onSale":true,"stock":{"quantity":4},"price":{"salePrice":10,"listPrice":10}},
              {"variantId":12,"barcode":"B12","stockCode":"S","onSale":false,"stock":{"quantity":0},"price":{"salePrice":10,"listPrice":10}},
              {"variantId":13,"barcode":"B13","stockCode":"S","onSale":false,"stock":{"quantity":5},"price":{"salePrice":10,"listPrice":10}},
              {"variantId":14,"barcode":"B14","stockCode":"S","onSale":false,"locked":true,"stock":{"quantity":5},"price":{"salePrice":10,"listPrice":10}},
              {"variantId":15,"barcode":"B15","stockCode":"S","stock":{"quantity":5},"price":{"salePrice":10,"listPrice":10}}]}]}
            """;

        var variants = Assert.Single(TrendyolJsonMapper.CatalogProducts(json).Items).Variants.ToDictionary(x => x.ExternalVariantId);

        Assert.Equal((ListingSaleState.OnSale, (string?)null), (variants["11"].SaleState, variants["11"].SaleStateReason));
        Assert.Equal((ListingSaleState.OffSale, ListingSaleState.StockZero), (variants["12"].SaleState, variants["12"].SaleStateReason));
        Assert.Equal((ListingSaleState.OffSale, (string?)null), (variants["13"].SaleState, variants["13"].SaleStateReason));
        Assert.Equal((ListingSaleState.OffSale, ListingSaleState.Locked), (variants["14"].SaleState, variants["14"].SaleStateReason));
        Assert.Equal(((string?)null, (string?)null), (variants["15"].SaleState, variants["15"].SaleStateReason));
    }

    [Fact]
    public void Trendyol_AProductWaitingForApprovalHasNoSaleState()
    {
        const string json = """{"page":0,"size":50,"totalPages":1,"content":[{"contentId":1,"title":"T","variants":[{"variantId":11,"barcode":"B11","stockCode":"S","onSale":false}]}]}""";

        Assert.Null(Assert.Single(Assert.Single(TrendyolJsonMapper.PendingApprovalCatalogProducts(json).Items).Variants).SaleState);
    }

    // Hepsiburada: isSalable of Listing Bilgilerini Çekme, the reason from deactivationReasons and the flags.
    [Theory]
    [InlineData("\"isSalable\":true,\"deactivationReasons\":[]", ListingSaleState.OnSale, null)]
    [InlineData("\"isSalable\":false,\"deactivationReasons\":[\"StockIsLessThanOrEqualToZero\"]", ListingSaleState.OffSale, ListingSaleState.StockZero)]
    [InlineData("\"isSalable\":false,\"deactivationReasons\":[\"PriceIsLessThanOrEqualToZero\",\"StockIsLessThanOrEqualToZero\"]", ListingSaleState.OffSale, ListingSaleState.PriceZero)]
    [InlineData("\"isSalable\":false,\"deactivationReasons\":[\"StockIsLessThanOrEqualToZero\",\"ByMerchant\"]", ListingSaleState.OffSale, ListingSaleState.BySeller)]
    [InlineData("\"isSalable\":false,\"isSuspended\":true", ListingSaleState.OffSale, ListingSaleState.Suspended)]
    [InlineData("\"isSalable\":false", ListingSaleState.OffSale, null)]
    [InlineData("\"availableStock\":3", null, null)]
    public void Hepsiburada_IsSalableIsTheSaleState(string fields, string? state, string? reason)
    {
        using var json = JsonDocument.Parse($$"""{"hepsiburadaSku":"HBCV0001","merchantSku":"SKU-1","price":120,{{fields}}}""");

        var variant = HepsiburadaJsonMapper.CatalogProduct(json.RootElement).Variants.Single();

        Assert.Equal((state, reason), (variant.SaleState, variant.SaleStateReason));
    }

    // n11: saleStatus of the product query; Suspended closes it; approval states have none.
    [Theory]
    [InlineData("Active", "On_Sale", ListingSaleState.OnSale, null)]
    [InlineData("Active", "Out_Of_Stock", ListingSaleState.OffSale, ListingSaleState.StockZero)]
    [InlineData("Active", "Sale_Closed", ListingSaleState.OffSale, null)]
    [InlineData("Active", "Before_Sale", ListingSaleState.OffSale, ListingSaleState.BeforeSale)]
    [InlineData("Suspended", "On_Sale", ListingSaleState.OffSale, ListingSaleState.Suspended)]
    [InlineData("InApproval", "Sale_Closed", null, null)]
    [InlineData("Active", null, null, null)]
    [InlineData("Active", "Something_New", null, null)]
    public void N11_SaleStatusIsTheSaleState(string status, string? saleStatus, string? state, string? reason)
    {
        var sale = saleStatus is null ? "" : $",\"saleStatus\":\"{saleStatus}\"";
        using var json = JsonDocument.Parse($$"""{"content":[{"n11ProductId":1,"stockCode":"S1","productMainId":"M","title":"T","status":"{{status}}"{{sale}},"salePrice":10,"listPrice":10,"quantity":2}],"number":0,"totalPages":1,"totalElements":1}""");

        var variant = Assert.Single(Assert.Single(N11ProductMapper.Page(json.RootElement).Items).Variants);

        Assert.Equal((state, reason), (variant.SaleState, variant.SaleStateReason));
    }

    // Pazarama: productStatus 1 open, 10 closed (Kataloglu Ürünleri Satışa Kapatıp-Açma); 0 or none unknown.
    [Theory]
    [InlineData(1, false, ListingSaleState.OnSale, null)]
    [InlineData(10, false, ListingSaleState.OffSale, ListingSaleState.BySeller)]
    [InlineData(0, false, null, null)]
    [InlineData(10, true, null, null)]
    public void Pazarama_ProductStatusIsTheSaleState(int productStatus, bool pending, string? state, string? reason)
    {
        using var json = JsonDocument.Parse($$"""{"name":"n","code":"C1","salePrice":1,"stockCount":2,"productStatus":{{productStatus}}}""");

        var variant = Assert.Single(PazaramaProductMapper.Product(json.RootElement, pending)!.Variants);

        Assert.Equal((state, reason), (variant.SaleState, variant.SaleStateReason));
    }

    // PTT AVM: the product row's Aktif and a variant's own Aktif.
    [Theory]
    [InlineData(true, null, ListingSaleState.OnSale)]
    [InlineData(true, false, ListingSaleState.OffSale)]
    [InlineData(false, null, ListingSaleState.OffSale)]
    [InlineData(false, true, ListingSaleState.OffSale)]
    [InlineData(null, null, null)]
    [InlineData(null, true, null)]
    public void PttAvm_AktifIsTheSaleState(bool? product, bool? variant, string? expected) =>
        Assert.Equal(expected, PttAvmProductMapper.SaleState(product, variant));

    [Fact]
    public void PttAvm_APassiveProductRowIsClosed()
    {
        var row = XElement.Parse("<StokKontrolDetay><UrunId>7</UrunId><Barkod>B7</Barkod><Aktif>false</Aktif><Miktar>3</Miktar></StokKontrolDetay>");

        var product = PttAvmProductMapper.Product(row, []);

        Assert.Equal(ListingSaleState.OffSale, Assert.Single(product!.Variants).SaleState);
    }

    // Shopify: DRAFT and ARCHIVED are closed; ACTIVE does not prove a channel publishes it, so it stays unknown.
    [Theory]
    [InlineData("ACTIVE", null, null)]
    [InlineData("DRAFT", ListingSaleState.OffSale, ListingSaleState.Draft)]
    [InlineData("ARCHIVED", ListingSaleState.OffSale, ListingSaleState.Archived)]
    public void Shopify_ProductStatusClosesDraftsAndArchives(string status, string? state, string? reason)
    {
        using var json = JsonDocument.Parse($$"""
            {"node":{"id":"gid://shopify/Product/1","title":"T","descriptionHtml":"","vendor":"V","productType":"P","status":"{{status}}",
              "variants":{"nodes":[{"id":"gid://shopify/ProductVariant/2","sku":"S2","price":"10.00","selectedOptions":[{"name":"Beden","value":"M"}]}]} } }
            """);

        var variant = Assert.Single(ShopifyHttpClient.MapProduct(json.RootElement).Variants);

        Assert.Equal((state, reason), (variant.SaleState, variant.SaleStateReason));
    }

    private static RemoteCatalogVariant Remote(string id, string? barcode, string sku, string? state, string? reason = null) =>
        new(id, sku, barcode, null, new Dictionary<string, string>(), false, null, null, null, null, null, "{}", SaleState: state, SaleStateReason: reason);

    [Fact]
    public void ALinkTakesTheStateOfItsOwnVariantFirst()
    {
        // Trendyol gives several variants one stockCode: the id and the barcode decide before it.
        var page = new[] { Remote("11", "B11", "S", ListingSaleState.OnSale), Remote("12", "B12", "S", ListingSaleState.OffSale, ListingSaleState.StockZero) };

        Assert.Equal(new ListingSaleStateRead(ListingSaleState.OffSale, ListingSaleState.StockZero), ListingSaleStateReads.For("12", "B12", "S", page));
        Assert.Equal(new ListingSaleStateRead(ListingSaleState.OnSale, null), ListingSaleStateReads.For("panel-id", "B11", "S", page));
        // A stock code naming two variants of different states is unknown.
        Assert.Equal(new ListingSaleStateRead(null, null), ListingSaleStateReads.For("panel-id", null, "S", page));
        // A link the page does not name is left as it is.
        Assert.Null(ListingSaleStateReads.For("99", "B99", "S99", page));
    }

    [Fact]
    public void AnAnswerThatDoesNotSayMakesTheStateUnknown() =>
        Assert.Equal(new ListingSaleStateRead(null, null), ListingSaleStateReads.For("11", null, null, [Remote("11", null, "S", null)]));

    [Fact]
    public void TheReasonOnlyRidesOnAClosedState() =>
        Assert.Equal(new ListingSaleStateRead(ListingSaleState.OnSale, null), ListingSaleStateReads.For("11", null, null, [Remote("11", null, "S", ListingSaleState.OnSale, ListingSaleState.StockZero)]));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void TheSatisaKapaliFilterFindsAClosedVariant(int offSale, bool matches) =>
        Assert.Equal(matches, ProductListingHealth.MatchesFilter(ProductListingHealth.OffSale, 3, 3, 0, offSale));

    [Fact]
    public void AClosureOnlyForZeroStockIsLeftToTheStockFilter()
    {
        Assert.True(ListingSaleState.ClosedBeyondStock(ListingSaleState.OffSale, ListingSaleState.BySeller));
        Assert.True(ListingSaleState.ClosedBeyondStock(ListingSaleState.OffSale, null));
        Assert.False(ListingSaleState.ClosedBeyondStock(ListingSaleState.OffSale, ListingSaleState.StockZero));
        Assert.False(ListingSaleState.ClosedBeyondStock(ListingSaleState.OnSale, null));
        Assert.False(ListingSaleState.ClosedBeyondStock(null, null));
    }

    [Fact]
    public void TheFilterAcceptsOffSaleOnEveryMarketplace()
    {
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY" })
        {
            Assert.True(CatalogService.TryProductPlatformFilter($"{platform}:OFF_SALE", out var code, out var state));
            Assert.Equal((platform, "OFF_SALE"), (code, state));
        }
    }

    // The sale state never changes the publication status the price/stock sends check.
    [Fact]
    public void ThePublicationStatusIgnoresTheSaleState() =>
        Assert.Equal("LIVE", ProductListingHealth.Status("LIVE", 3, 0, 3));
}
