namespace MarketplaceHub.Domain;

public enum ReservationStatus { Active, Released }

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Create(decimal amount, string currency)
    {
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be an uppercase ISO 4217 code.", nameof(currency));
        return new Money(decimal.Round(amount, 4, MidpointRounding.ToEven), currency);
    }
}

public static class InventoryProjection
{
    public static decimal Available(decimal onHand, decimal reserved) => Math.Max(0m, onHand - reserved);
    public static decimal ChannelPublishable(decimal available, decimal safetyStock) => Math.Max(0m, available - safetyStock);
}

public sealed class ConnectionInventoryPolicy
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string AuthorityMode { get; set; }
    public required string ReservationMode { get; set; }
    public required string ReserveOnStatuses { get; set; }
    public required string ReleaseOnStatuses { get; set; }
    public bool NegativeStockAllowed { get; set; }
    public decimal DefaultSafetyStock { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InventoryLocation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public int Priority { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ConnectionLocationMapping
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid LocationId { get; set; }
    public required string ExternalLocationId { get; set; }
    public required string Status { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InventoryItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VariantId { get; set; }
    public required string LocationCode { get; set; }
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public decimal Available { get; set; }
    public long ProjectionVersion { get; set; } = 1;
    public DateTimeOffset? ReconciledAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class StockLedgerEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InventoryItemId { get; set; }
    public required string MovementType { get; set; }
    public decimal QuantityDelta { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string SourceEventId { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string CorrelationId { get; set; }
}

public sealed class StockReservation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InventoryItemId { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public decimal Quantity { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Active;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ChannelOffer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid VariantId { get; set; }
    public decimal ListPrice { get; set; }
    public decimal SalePrice { get; set; }
    public required string Currency { get; set; }
    public decimal VatRate { get; set; }
    public required string VatInclusion { get; set; }
    public required string RoundingMode { get; set; }
    public decimal SafetyStock { get; set; }
    public required string Status { get; set; }
    public long PriceVersion { get; set; } = 1;
    public string? LastPriceHash { get; set; }
    public long? LastStockProjectionVersion { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ChannelPriceHistory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OfferId { get; set; }
    public long PriceVersion { get; set; }
    public decimal ListPrice { get; set; }
    public decimal SalePrice { get; set; }
    public required string Currency { get; set; }
    public required string Reason { get; set; }
    public required string ActorSource { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
}
