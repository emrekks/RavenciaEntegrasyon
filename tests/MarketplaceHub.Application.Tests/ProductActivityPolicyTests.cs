using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ürün Hareketleri: which source a row names, which kind it counts under, and the change rows a status or panel price
// edit writes.
public sealed class ProductActivityPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("ORDER_RECEIVED", "ORDER_LINE", "01a1a7f0-a183-77dd-bc93-7a1119cb8f5f", ProductActivitySources.Order)]
    [InlineData("ORDER_STOCK_SHORTFALL", "ORDER_LINE", "x", ProductActivitySources.Order)]
    [InlineData("RETURN_PASS", "RETURN_CLAIM", "e08a7b72", ProductActivitySources.Return)]
    [InlineData("MARKETPLACE_STOCK_IMPORT", "MARKETPLACE_CATALOG", "c", ProductActivitySources.MarketplaceImport)]
    [InlineData("IMPORT_OPENING", "MARKETPLACE_CATALOG", "c", ProductActivitySources.MarketplaceImport)]
    [InlineData("IMPORT_OPENING", "IMPORT", "session", ProductActivitySources.ClassicImport)]
    [InlineData("IMPORT_OPENING", OpeningStockLedger.EditorSourceType, "İlk ürün stoğu", ProductActivitySources.Editor)]
    [InlineData("MANUAL_ADJUSTMENT", "MANUAL", "Toplu ürün stok düzenleme", ProductActivitySources.BulkEdit)]
    [InlineData("MANUAL_ADJUSTMENT", "MANUAL", "Ürün düzenleme stoğu", ProductActivitySources.Editor)]
    [InlineData("MANUAL_ADJUSTMENT", "MANUAL", "İlk ürün stoğu", ProductActivitySources.Editor)]
    [InlineData("MANUAL_ADJUSTMENT", "MANUAL", "Ürün dosyası içe aktarma", ProductActivitySources.FileImport)]
    [InlineData("MANUAL_ADJUSTMENT", "MANUAL", "Sayım farkı", ProductActivitySources.Manual)]
    public void A_ledger_row_names_its_source(string movementType, string sourceType, string reason, string expected) =>
        Assert.Equal(expected, ProductActivityPolicy.LedgerSource(movementType, sourceType, reason));

    [Theory]
    [InlineData("SYSTEM:TRENDYOL", "TRENDYOL_CATALOG_IMPORT", ProductActivitySources.MarketplaceImport)]
    [InlineData("USER:0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1", "Toplu ürün fiyat düzenleme", ProductActivitySources.BulkEdit)]
    [InlineData("USER:0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1", "Toplu yayın fiyatı", ProductActivitySources.Publish)]
    [InlineData("USER:0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1", "İlk ürün fiyatı", ProductActivitySources.Editor)]
    [InlineData("USER:0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1", "Ürün dosyası içe aktarma", ProductActivitySources.FileImport)]
    [InlineData("USER:0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1", "Hızlı düzenleme", ProductActivitySources.PriceEdit)]
    public void A_price_history_row_names_its_source(string actorSource, string reason, string expected) =>
        Assert.Equal(expected, ProductActivityPolicy.PriceHistorySource(actorSource, reason));

    [Fact]
    public void A_price_history_actor_is_read_from_a_user_source_only()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, ProductActivityPolicy.PriceHistoryActor($"USER:{id:D}"));
        Assert.Null(ProductActivityPolicy.PriceHistoryActor("SYSTEM:TRENDYOL"));
        Assert.Null(ProductActivityPolicy.PriceHistoryActor("USER:not-a-guid"));
    }

    [Theory]
    [InlineData("RETURN_PASS", ProductActivityKinds.Return)]
    [InlineData("RETURN_QUARANTINE", ProductActivityKinds.Return)]
    [InlineData("RETURN_NOTRECEIVED", ProductActivityKinds.Return)]
    [InlineData("OFFER_PRICE", ProductActivityKinds.Price)]
    [InlineData("DEFAULT_PRICE", ProductActivityKinds.Price)]
    [InlineData("STATUS", ProductActivityKinds.Status)]
    [InlineData("ORDER_RECEIVED", ProductActivityKinds.Stock)]
    [InlineData("ORDER_STOCK_SHORTFALL", ProductActivityKinds.Stock)]
    [InlineData("IMPORT_OPENING", ProductActivityKinds.Stock)]
    public void A_type_counts_under_its_kind(string type, string kind) => Assert.Equal(kind, ProductActivityPolicy.KindOfType(type));

    [Fact]
    public void A_status_change_records_before_and_after_and_a_same_status_records_nothing()
    {
        var tenant = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();

        var entry = ProductChangeLog.Status(tenant, product, null, ProductStatus.Active, ProductStatus.Archived, ProductChangeSources.BulkEdit, user, null, "corr", Now);

        Assert.NotNull(entry);
        Assert.Equal((ProductChangeKinds.Status, "ACTIVE", "ARCHIVED", ProductChangeSources.BulkEdit, (Guid?)user), (entry.Kind, entry.PreviousStatus, entry.NewStatus, entry.Source, entry.ActorUserId));
        Assert.Equal(TimeSpan.Zero, entry.OccurredAt.Offset);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.Null(ProductChangeLog.Status(tenant, product, null, ProductStatus.Draft, ProductStatus.Draft, ProductChangeSources.Editor, user, null, "corr", Now));
    }

    [Fact]
    public void A_panel_price_change_records_both_prices_and_an_unchanged_price_records_nothing()
    {
        var tenant = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();

        var entry = ProductChangeLog.DefaultPrice(tenant, product, variant, (999m, 899m), (999m, 950m), ProductChangeSources.FileImport, null, "corr", Now);

        Assert.NotNull(entry);
        Assert.Equal((ProductChangeKinds.DefaultPrice, (decimal?)899m, (decimal?)950m, (decimal?)999m, (decimal?)999m, (Guid?)variant),
            (entry.Kind, entry.PreviousSalePrice, entry.NewSalePrice, entry.PreviousListPrice, entry.NewListPrice, entry.VariantId));
        Assert.Null(ProductChangeLog.DefaultPrice(tenant, product, variant, (999m, 899m), (999m, 899m), ProductChangeSources.Editor, null, "corr", Now));
        // A price set for the first time (none before) is a change.
        Assert.NotNull(ProductChangeLog.DefaultPrice(tenant, product, variant, (null, null), (100m, 90m), ProductChangeSources.Editor, null, "corr", Now));
    }

    [Fact]
    public void The_note_is_cut_to_its_column()
    {
        var entry = ProductChangeLog.Status(Guid.NewGuid(), Guid.NewGuid(), null, ProductStatus.Active, ProductStatus.Draft, ProductChangeSources.Editor, null, null, "corr", Now, new string('a', 300));
        Assert.Equal(ProductChangeLog.MaxNoteLength, entry!.Note!.Length);
    }

    [Fact]
    public void A_stock_row_shows_before_and_after_and_an_unreachable_before_stays_unknown()
    {
        Assert.Equal(((decimal?)4m, 5m), ProductActivityPolicy.StockBalance(5m, 1m));
        Assert.Equal(((decimal?)3m, 1m), ProductActivityPolicy.StockBalance(1m, -2m));
        Assert.Equal(((decimal?)0m, 5m), ProductActivityPolicy.StockBalance(5m, 5m));
        // Stock set before the ledger kept every change: the before would be below zero, so only the after is known.
        Assert.Equal(((decimal?)null, 2m), ProductActivityPolicy.StockBalance(2m, 6m));
        Assert.DoesNotContain(ProductActivityTypes.OrderStockShortfall, ProductActivityTypes.OnHandLedger);
    }
}
