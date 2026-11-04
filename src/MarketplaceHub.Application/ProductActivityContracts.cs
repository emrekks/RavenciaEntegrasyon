using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// ProductChangeEntry.Kind.
public static class ProductChangeKinds
{
    public const string Status = "STATUS";
    public const string DefaultPrice = "DEFAULT_PRICE";
}

// ProductChangeEntry.Source: where a status or panel price change came from.
public static class ProductChangeSources
{
    // The product editor (Ürün Düzenle).
    public const string Editor = "EDITOR";
    // Toplu işlem on the product list (bulk status, bulk price).
    public const string BulkEdit = "BULK_EDIT";
    // A single variant's price edit (quick edit).
    public const string PriceEdit = "PRICE_EDIT";
    // The product file (Excel) import.
    public const string FileImport = "FILE_IMPORT";
    // A marketplace catalog import; a system change, no user.
    public const string MarketplaceImport = "MARKETPLACE_IMPORT";
}

// The rows a status or panel price change writes, in the same save as the change. A change to the same value writes
// nothing.
public static class ProductChangeLog
{
    public const int MaxNoteLength = 200;

    public static ProductChangeEntry? Status(Guid tenantId, Guid productId, Guid? variantId, ProductStatus previous, ProductStatus next, string source,
        Guid? actorUserId, Guid? connectionId, string correlationId, DateTimeOffset now, string? note = null)
    {
        if (previous == next) return null;
        return Entry(tenantId, productId, variantId, ProductChangeKinds.Status, source, actorUserId, connectionId, correlationId, now, note, entry =>
        {
            entry.PreviousStatus = StatusCode(previous);
            entry.NewStatus = StatusCode(next);
        });
    }

    public static ProductChangeEntry? DefaultPrice(Guid tenantId, Guid productId, Guid? variantId, (decimal? List, decimal? Sale) previous, (decimal? List, decimal? Sale) next,
        string source, Guid? actorUserId, string correlationId, DateTimeOffset now, string? note = null)
    {
        if (previous.List == next.List && previous.Sale == next.Sale) return null;
        return Entry(tenantId, productId, variantId, ProductChangeKinds.DefaultPrice, source, actorUserId, null, correlationId, now, note, entry =>
        {
            entry.PreviousListPrice = previous.List;
            entry.NewListPrice = next.List;
            entry.PreviousSalePrice = previous.Sale;
            entry.NewSalePrice = next.Sale;
        });
    }

    public static string StatusCode(ProductStatus status) => status switch
    {
        ProductStatus.Active => "ACTIVE",
        ProductStatus.Archived => "ARCHIVED",
        _ => "DRAFT"
    };

    private static ProductChangeEntry Entry(Guid tenantId, Guid productId, Guid? variantId, string kind, string source, Guid? actorUserId, Guid? connectionId,
        string correlationId, DateTimeOffset now, string? note, Action<ProductChangeEntry> fill)
    {
        var entry = new ProductChangeEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProductId = productId,
            VariantId = variantId,
            Kind = kind,
            Source = source,
            ActorUserId = actorUserId,
            ConnectionId = connectionId,
            Note = Cut(note, MaxNoteLength),
            CorrelationId = Cut(correlationId, 128) ?? "",
            OccurredAt = now.ToUniversalTime()
        };
        fill(entry);
        return entry;
    }

    private static string? Cut(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}

// İşlem Takibi › Loglar › Ürün Hareketleri: what changed on a product, from every record that keeps it.
public static class ProductActivityKinds
{
    public const string Stock = "STOCK";
    public const string Price = "PRICE";
    public const string Status = "STATUS";
    public const string Return = "RETURN";

    public static readonly IReadOnlyList<string> All = [Stock, Price, Status, Return];
}

// Where an activity row came from, as the page names it.
public static class ProductActivitySources
{
    public const string Order = "ORDER";
    public const string Return = "RETURN";
    public const string Manual = "MANUAL";
    public const string Editor = ProductChangeSources.Editor;
    public const string BulkEdit = ProductChangeSources.BulkEdit;
    public const string PriceEdit = ProductChangeSources.PriceEdit;
    public const string FileImport = ProductChangeSources.FileImport;
    public const string MarketplaceImport = ProductChangeSources.MarketplaceImport;
    // A marketplace offer's first price set at publishing (Toplu yayın).
    public const string Publish = "PUBLISH";
    // The classic import (İçe Aktarım) that created the product with its opening stock.
    public const string ClassicImport = "CLASSIC_IMPORT";
}

// The row types an activity row can have (ProductActivityView.Type).
public static class ProductActivityTypes
{
    // Stock ledger movement types that change on-hand stock.
    public const string OrderReceived = "ORDER_RECEIVED";
    public const string ManualAdjustment = "MANUAL_ADJUSTMENT";
    public const string MarketplaceStockImport = MarketplaceStockImportPolicy.MovementType;
    public const string ImportOpening = "IMPORT_OPENING";
    public const string ReturnPass = "RETURN_PASS";
    // Not a stock change: an order line found too little stock (QuantityDelta is the units missing).
    public const string OrderStockShortfall = OrderInventoryStockPolicy.ShortfallMovementType;
    // A return line kept out of stock (QuantityDelta is its quantity; stock did not change).
    public const string ReturnQuarantine = "RETURN_QUARANTINE";
    public const string ReturnDamaged = "RETURN_DAMAGED";
    public const string ReturnNotReceived = "RETURN_NOTRECEIVED";
    public const string OfferPrice = "OFFER_PRICE";
    public const string DefaultPrice = ProductChangeKinds.DefaultPrice;
    public const string Status = ProductChangeKinds.Status;

    public static readonly IReadOnlyList<string> StockLedger = [OrderReceived, ManualAdjustment, MarketplaceStockImport, ImportOpening, OrderStockShortfall];
    public static readonly IReadOnlyList<string> ReturnLedger = [ReturnPass];
    // The ledger rows that move on-hand stock: every on-hand change writes one, so the stock after a row is the item's
    // on-hand now less these rows' deltas after it. Shortfall, baseline, shipment and reservation rows move nothing.
    public static readonly IReadOnlyList<string> OnHandLedger = [OrderReceived, ManualAdjustment, MarketplaceStockImport, ImportOpening, ReturnPass];
}

public static class ProductActivityPolicy
{
    // The catalog import's opening stock (MarketplaceJobProcessor): the source type the ledger row carries.
    public const string MarketplaceCatalogSourceType = MarketplaceStockImportPolicy.SourceType;

    // Reasons the panel writes on a manual stock or price change (the web sends them, the file import its own).
    public const string BulkStockReason = "Toplu ürün stok düzenleme";
    public const string BulkPriceReason = "Toplu ürün fiyat düzenleme";
    public const string EditorStockReason = "Ürün düzenleme stoğu";
    public const string NewProductStockReason = "İlk ürün stoğu";
    public const string NewProductPriceReason = "İlk ürün fiyatı";
    public const string PublishPriceReason = "Toplu yayın fiyatı";
    public const string FileImportReason = "Ürün dosyası içe aktarma";

    public static string KindOfType(string type) => type switch
    {
        ProductActivityTypes.ReturnPass or ProductActivityTypes.ReturnQuarantine or ProductActivityTypes.ReturnDamaged or ProductActivityTypes.ReturnNotReceived => ProductActivityKinds.Return,
        ProductActivityTypes.OfferPrice or ProductActivityTypes.DefaultPrice => ProductActivityKinds.Price,
        ProductActivityTypes.Status => ProductActivityKinds.Status,
        _ => ProductActivityKinds.Stock
    };

    // Where a stock ledger row came from: its movement type, then the reason a manual change carries.
    public static string LedgerSource(string movementType, string sourceType, string? reason) => movementType switch
    {
        ProductActivityTypes.OrderReceived or ProductActivityTypes.OrderStockShortfall => ProductActivitySources.Order,
        ProductActivityTypes.ReturnPass => ProductActivitySources.Return,
        ProductActivityTypes.MarketplaceStockImport => ProductActivitySources.MarketplaceImport,
        ProductActivityTypes.ImportOpening => sourceType switch
        {
            MarketplaceCatalogSourceType => ProductActivitySources.MarketplaceImport,
            OpeningStockLedger.EditorSourceType => ProductActivitySources.Editor,
            _ => ProductActivitySources.ClassicImport
        },
        _ => ReasonSource(reason)
    };

    // Where a marketplace offer's price came from: a user's change by its reason, anything else a marketplace import.
    public static string PriceHistorySource(string actorSource, string? reason)
    {
        if (!actorSource.StartsWith("USER:", StringComparison.Ordinal)) return ProductActivitySources.MarketplaceImport;
        return reason?.Trim() switch
        {
            PublishPriceReason => ProductActivitySources.Publish,
            _ => ReasonSource(reason) is var source && source == ProductActivitySources.Manual ? ProductActivitySources.PriceEdit : source
        };
    }

    // A stock row's before → after from the stock after it (on-hand now less the later on-hand rows) and its delta. A
    // before below zero means the ledger does not reach back to that row's start (stock set before the ledger kept every
    // change): only the after is known then, the before stays unknown.
    public static (decimal? Before, decimal After) StockBalance(decimal after, decimal delta)
    {
        var before = after - delta;
        return (before < 0 ? null : before, after);
    }

    // The user a price history row names (ActorSource "USER:{id}"); null for the system.
    public static Guid? PriceHistoryActor(string actorSource) =>
        actorSource.StartsWith("USER:", StringComparison.Ordinal) && Guid.TryParse(actorSource.AsSpan(5), out var id) ? id : null;

    private static string ReasonSource(string? reason) => reason?.Trim() switch
    {
        BulkStockReason or BulkPriceReason => ProductActivitySources.BulkEdit,
        EditorStockReason or NewProductStockReason or NewProductPriceReason => ProductActivitySources.Editor,
        FileImportReason => ProductActivitySources.FileImport,
        _ => ProductActivitySources.Manual
    };
}

// A variant's first stock is a stock move like any other: it is written as an opening row (IMPORT_OPENING, "Açılış
// Stoğu") in the same save as the variant, whichever way the variant came in (the product editor here, the classic
// import and the marketplace catalog import their own). It is not an on-hand movement for MarketplaceStockImportPolicy.
public static class OpeningStockLedger
{
    // The product editor: a new card, a variant added to a card, a copied card.
    public const string EditorSourceType = "PRODUCT_EDITOR";

    // Null when there is no opening stock (0 is no movement).
    public static StockLedgerEntry? FromEditor(Guid tenantId, Guid inventoryItemId, Guid variantId, decimal quantity, Guid? actorUserId, string correlationId, DateTimeOffset now)
    {
        if (quantity == 0) return null;
        var eventId = $"editor-opening:{variantId:N}";
        return new StockLedgerEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InventoryItemId = inventoryItemId,
            MovementType = ProductActivityTypes.ImportOpening,
            QuantityDelta = quantity,
            SourceType = EditorSourceType,
            SourceId = ProductActivityPolicy.NewProductStockReason,
            SourceEventId = eventId,
            IdempotencyKey = eventId,
            OccurredAt = now.ToUniversalTime(),
            RecordedAt = now.ToUniversalTime(),
            ActorUserId = actorUserId,
            CorrelationId = correlationId
        };
    }
}

// The activity list: newest first, at most Limit rows (default 50, max 100); After is the NextCursor of the previous page.
public sealed record ProductActivityQuery(int? Limit = null, string? After = null, IReadOnlyList<string>? Kinds = null, IReadOnlyList<string>? Platforms = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 100;
    public const int MaximumSearchLength = 100;
    public int EffectiveLimit => Math.Clamp(Limit ?? DefaultLimit, 1, MaximumLimit);
}

// One row of Ürün Hareketleri. QuantityDelta is the stock change (or, for a shortfall or a return kept out of stock,
// the units concerned); StockBefore → StockAfter is the on-hand stock around a stock-changing row (null on any other
// row; a null before is unknown); the price and status pairs are before → after, a null before being unknown (a store
// price's first version). The product and variant fields are the card as it is now; null when it was deleted.
public sealed record ProductActivityView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Kind,
    string Type,
    Guid? ProductId,
    string? ProductTitle,
    string? ImageUrl,
    Guid? VariantId,
    string? Sku,
    string? Barcode,
    string? ModelCode,
    string? OptionText,
    decimal? QuantityDelta,
    decimal? StockBefore,
    decimal? StockAfter,
    decimal? PreviousSalePrice,
    decimal? NewSalePrice,
    decimal? PreviousListPrice,
    decimal? NewListPrice,
    string? Currency,
    string? PreviousStatus,
    string? NewStatus,
    string Source,
    string? Note,
    Guid? ConnectionId,
    string? PlatformCode,
    string? StoreName,
    Guid? OrderId,
    string? OrderNumber,
    Guid? ReturnId,
    string? ReturnNumber,
    Guid? ActorUserId,
    string? ActorName);

public sealed record ProductActivityCount(string Kind, int Count);

public interface IProductActivityService
{
    Task<ServiceResult<PageResult<ProductActivityView>>> PageAsync(Guid tenantId, ProductActivityQuery query, CancellationToken cancellationToken);

    // Rows per kind under the list's filters except the kind filter; Kinds, After and Limit are ignored.
    Task<ServiceResult<IReadOnlyList<ProductActivityCount>>> CountsAsync(Guid tenantId, ProductActivityQuery query, CancellationToken cancellationToken);
}
