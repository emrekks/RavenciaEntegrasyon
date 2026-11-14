using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Model Kaydı Faz 2: the model-level id each marketplace gives a model (MarketplaceModelLinks.ModelListings).
public sealed class MarketplaceModelListingsTests
{
    [Fact]
    public void ModelListings_TakeEachMarketplacesModelId_HepsiburadaNone()
    {
        static RemoteCatalogVariant Variant(string id) => new(id, id, id, null, new Dictionary<string, string>(), false, null, null, null, null, "TRY", "{}");
        static RemoteCatalogProduct Snapshot(string id, string? main, params string[] variants) => new(id, main, "t", "", null, null, null, null, [], [.. variants.Select(Variant)], "{}");
        // Trendyol: one content per colour, grouped by productMainId; a row without one records nothing.
        var trendyol = MarketplaceModelLinks.ModelListings("TRENDYOL", [Snapshot("c1", "MZ01", "b1", "b2"), Snapshot("c2", "MZ01", "b3"), Snapshot("c3", null, "b4")]);
        Assert.Equal(("MZ01", (string?)null, 3), (trendyol.Single().ExternalId, trendyol.Single().ExternalCode, trendyol.Single().ExternalVariantIds.Count));
        Assert.Equal(["main:MZ02"], MarketplaceModelLinks.ModelListings("N11", [Snapshot("main:MZ02", "MZ02", "s1"), Snapshot("n11:42", null, "s2")]).Select(x => x.ExternalId));
        var pazarama = MarketplaceModelLinks.ModelListings("PAZARAMA", [Snapshot("group:abc", "abc", "p1"), Snapshot("code:X1", null, "p2")]).Single();
        Assert.Equal(("group:abc", "abc"), (pazarama.ExternalId, pazarama.ExternalCode));
        Assert.Equal(["7001"], MarketplaceModelLinks.ModelListings("SHOPIFY", [Snapshot("7001", null, "g1")]).Select(x => x.ExternalId));
        Assert.Empty(MarketplaceModelLinks.ModelListings("HEPSIBURADA", [Snapshot("HBC1", "MZ01", "h1")]));
    }
}
