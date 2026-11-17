using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// What the known-values store takes from a product read and from an update answer (MarketplaceKnownListingValueStore):
// only what the marketplace itself gave, and from a send only what its batch line accepted.
public sealed class MarketplaceKnownListingValuesTests
{
    private static RemoteCatalogVariant Variant(string id, decimal? desi = null, string raw = "{}") =>
        new(id, id, id, "MZ1", new Dictionary<string, string>(), false, null, null, null, null, "TRY", raw, Desi: desi);

    [Fact]
    public void ATrendyolReadGivesItsContentTextsAndEachVariantsDesi_AndNothingItDidNotCarry()
    {
        var snapshot = new RemoteCatalogProduct("c1", "MZ1", "Bluz", "Uzun", null, null, null, null, [], [Variant("869001", 2.5m), Variant("869002", 0m)],
            """{"contentId":1,"title":"Bluz","description":"Uzun"}""");
        var values = MarketplaceKnownListingValueStore.ImportValues("TRENDYOL", snapshot);
        Assert.Equal(("869001", "Bluz", "Uzun", 2.5m), Assert.Single(values, x => x.ExternalVariantId == "869001"));
        // Trendyol answers 0 for a desi never entered: unknown, not 0.
        Assert.Null(values.Single(x => x.ExternalVariantId == "869002").Desi);

        // A content answer without a description says nothing of it; the mapper's "" fallback is not taken.
        var bare = snapshot with { Description = "", RawJson = """{"contentId":1,"title":"Bluz"}""" };
        Assert.All(MarketplaceKnownListingValueStore.ImportValues("TRENDYOL", bare), x => Assert.Null(x.Description));
    }

    [Fact]
    public void AHepsiburadaListingWithoutAProductNameLeavesTheTitleUnknown_NotItsStockCode()
    {
        // HepsiburadaJsonMapper.CatalogProduct falls back to the stock code as the title: that is not Hepsiburada's title.
        var snapshot = new RemoteCatalogProduct("p1", null, "MZ1-M", "", null, null, null, null, [],
            [Variant("HBV1", null, """{"hepsiburadaSku":"HBV1","merchantSku":"MZ1-M"}"""), Variant("HBV2", 3m, """{"hepsiburadaSku":"HBV2","productName":"Bluz","description":"Uzun"}""")], "{}");
        var values = MarketplaceKnownListingValueStore.ImportValues("HEPSIBURADA", snapshot);
        Assert.DoesNotContain(values, x => x.ExternalVariantId == "HBV1");
        Assert.Equal(("HBV2", "Bluz", "Uzun", 3m), Assert.Single(values));
        Assert.Empty(MarketplaceKnownListingValueStore.ImportValues("N11", snapshot));
    }

    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();
    private static readonly MarketplaceKnownListingValueStore.Listing[] Listings = [new(First, "869001", "MZ1-M"), new(Second, "869002", "MZ1-L")];

    [Fact]
    public void ATrendyolBarcodeLineWritesOnlyWhenAccepted()
    {
        const string request = """{"items":[{"barcode":"869001","title":"Yeni","dimensionalWeight":2},{"barcode":"869002","title":"Yeni","dimensionalWeight":3}]}""";
        var writes = MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "UNAPPROVED", request,
            [new RemoteOperationLine("869001", true, null, null, false), new RemoteOperationLine("869002", false, null, "BAD", false)], Listings);
        var write = Assert.Single(writes);
        Assert.Equal((First, "Yeni", (string?)null, 2m), (write.VariantId, write.Title, write.Description, write.Desi));
        // A line missing from the answer is not an acceptance.
        Assert.Empty(MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "VARIANTS", request, [], Listings));
        Assert.Empty(MarketplaceKnownListingValueStore.Accepted("N11", "CONTENT", request, [new RemoteOperationLine("869001", true, null, null, false)], Listings));
    }

    [Fact]
    public void ATrendyolContentRequestWritesForTheCardOnlyWhenEveryContentWasAccepted()
    {
        const string request = """{"items":[{"contentId":11,"title":"Yeni","description":"Uzun"},{"contentId":12,"title":"Yeni","description":"Uzun"}]}""";
        var both = MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "CONTENT", request,
            [new RemoteOperationLine("11", true, null, null, false), new RemoteOperationLine("12", true, null, null, false)], Listings);
        Assert.Equal(new[] { First, Second }.Order(), both.Select(x => x.VariantId).Order());
        Assert.All(both, write => Assert.Equal(("Yeni", "Uzun", (decimal?)null), (write.Title, write.Description, write.Desi)));
        // A barcode left out of the content request (no approved content) gets nothing.
        Assert.Equal(First, Assert.Single(MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "CONTENT", request,
            [new RemoteOperationLine("11", true, null, null, false), new RemoteOperationLine("12", true, null, null, false)], Listings, ["869002"])).VariantId);
        // One content refused, or one with no line: nothing is known to have been applied for the card.
        Assert.Empty(MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "CONTENT", request,
            [new RemoteOperationLine("11", true, null, null, false), new RemoteOperationLine("12", false, null, "BAD", false)], Listings));
        Assert.Empty(MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "CONTENT", request, [new RemoteOperationLine("11", true, null, null, false)], Listings));
        // Contents that disagree are not one card's data: nothing.
        Assert.Empty(MarketplaceKnownListingValueStore.Accepted("TRENDYOL", "CONTENT", """{"items":[{"contentId":11,"title":"A"},{"contentId":12,"title":"B"}]}""",
            [new RemoteOperationLine("11", true, null, null, false), new RemoteOperationLine("12", true, null, null, false)], Listings));
    }

    [Fact]
    public void AHepsiburadaLineIsReadByStockCode_AndANullFieldMakesItUnknown()
    {
        const string request = """{"merchantId":"m","items":[{"hbSku":"HB1","merchantSku":"MZ1-M","productName":"Yeni","productDescription":null,"desi":"2"},{"hbSku":"HB2","merchantSku":"MZ1-L","productName":"Yeni"}]}""";
        var writes = MarketplaceKnownListingValueStore.Accepted("HEPSIBURADA", "CONTENT", request,
            [new RemoteOperationLine("MZ1-M", true, "HB1", null, false), new RemoteOperationLine("MZ1-L", false, "HB2", "REJECTED", false)], Listings);
        var write = Assert.Single(writes);
        Assert.Equal((First, "Yeni", 2m, true, false), (write.VariantId, write.Title, write.Desi, write.ClearDescription, write.ClearTitle));
    }
}
