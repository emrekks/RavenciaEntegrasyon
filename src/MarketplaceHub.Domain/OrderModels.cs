namespace MarketplaceHub.Domain;

public enum ShipmentPackageStatus
{
    New,
    Processing,
    OnHold,
    ReadyToShip,
    Shipped,
    Delivered,
    PartiallyCancelled,
    Cancelled,
    Undelivered,
    ReturnInTransit,
    Returned,
    ManualReview
}

public static class ShipmentPackageStateMachine
{
    private static readonly IReadOnlyDictionary<ShipmentPackageStatus, ShipmentPackageStatus[]> Allowed =
        new Dictionary<ShipmentPackageStatus, ShipmentPackageStatus[]>
        {
            [ShipmentPackageStatus.New] = [ShipmentPackageStatus.Processing, ShipmentPackageStatus.OnHold, ShipmentPackageStatus.PartiallyCancelled, ShipmentPackageStatus.Cancelled],
            [ShipmentPackageStatus.Processing] = [ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.OnHold, ShipmentPackageStatus.PartiallyCancelled, ShipmentPackageStatus.Cancelled],
            [ShipmentPackageStatus.OnHold] = [ShipmentPackageStatus.New, ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.Cancelled],
            [ShipmentPackageStatus.ReadyToShip] = [ShipmentPackageStatus.Shipped, ShipmentPackageStatus.OnHold, ShipmentPackageStatus.PartiallyCancelled, ShipmentPackageStatus.Cancelled],
            [ShipmentPackageStatus.Shipped] = [ShipmentPackageStatus.Delivered, ShipmentPackageStatus.Undelivered, ShipmentPackageStatus.ReturnInTransit],
            [ShipmentPackageStatus.Undelivered] = [ShipmentPackageStatus.Delivered, ShipmentPackageStatus.ReturnInTransit, ShipmentPackageStatus.ManualReview],
            [ShipmentPackageStatus.Delivered] = [ShipmentPackageStatus.ReturnInTransit],
            [ShipmentPackageStatus.ReturnInTransit] = [ShipmentPackageStatus.Returned, ShipmentPackageStatus.ManualReview],
            [ShipmentPackageStatus.PartiallyCancelled] = [ShipmentPackageStatus.Processing, ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Cancelled]
        };

    public static bool CanTransition(ShipmentPackageStatus current, ShipmentPackageStatus next) =>
        current == next || Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next);
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalOrderId { get; set; }
    public required string OrderNumber { get; set; }
    public required string Currency { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal NetAmount { get; set; }
    public DateTimeOffset OrderedAt { get; set; }
    public DateTimeOffset LastRemoteModifiedAt { get; set; }
    public required string CustomerSnapshotJson { get; set; }
    public required string ShipmentAddressSnapshotJson { get; set; }
    public required string InvoiceAddressSnapshotJson { get; set; }
    public required string DerivedStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? VariantId { get; set; }
    public required string ExternalLineId { get; set; }
    public required string Sku { get; set; }
    public string? Barcode { get; set; }
    public required string TitleSnapshot { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal CancelledQuantity { get; set; }
    public decimal ShippedQuantity { get; set; }
    public decimal DeliveredQuantity { get; set; }
    public decimal ReturnedQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public required string RawStatus { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class OrderFinancialAllocation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? OrderLineId { get; set; }
    public required string AllocationType { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string SourceKey { get; set; }
}

public sealed class ShipmentPackage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid OrderId { get; set; }
    public required string ExternalPackageId { get; set; }
    public string? OriginExternalPackageId { get; set; }
    public string? CargoProviderExternalId { get; set; }
    public string? CargoTrackingNumber { get; set; }
    public ShipmentPackageStatus Status { get; set; }
    public required string RawStatus { get; set; }
    public DateTimeOffset StatusOccurredAt { get; set; }
    public string? RemoteVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PackageLineAllocation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PackageId { get; set; }
    public Guid OrderLineId { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal CancelledQuantity { get; set; }
    public decimal ShippedQuantity { get; set; }
    public decimal DeliveredQuantity { get; set; }
    public decimal ReturnedQuantity { get; set; }
    public required string SourceEventId { get; set; }
}

public sealed class OrderStatusHistory
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? PackageId { get; set; }
    public required string CanonicalStatus { get; set; }
    public required string RawStatus { get; set; }
    public required string SourceEventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public static class OrderQuantityInvariant
{
    public static bool IsValid(decimal ordered, decimal activeAllocated, decimal cancelled, decimal shipped, decimal delivered, decimal returned) =>
        ordered >= 0 && activeAllocated >= 0 && cancelled >= 0 && shipped >= 0 && delivered >= 0 && returned >= 0 &&
        ordered == activeAllocated + cancelled && shipped <= activeAllocated && delivered <= shipped && returned <= delivered;
}
