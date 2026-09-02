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

public enum MarketplaceInvoiceStatus
{
    Unknown,
    NotInvoiced,
    Received,
    Invoiced,
    Rejected
}

public static class MarketplaceInvoiceStatePolicy
{
    public static MarketplaceInvoiceStatus FromRemote(string? rawStatus, string? packageRawStatus = null, string? invoiceNumber = null, string? invoiceUrl = null)
    {
        var normalized = rawStatus?.Trim().ToUpperInvariant();
        if (normalized is "INVOICED" or "INVOICE" or "COMPLETED") return MarketplaceInvoiceStatus.Invoiced;
        if (normalized is "RECEIVED" or "PROCESSING" or "PENDING") return MarketplaceInvoiceStatus.Received;
        if (normalized is "REJECTED" or "FAILED") return MarketplaceInvoiceStatus.Rejected;
        if (normalized is "NOTINVOICED" or "NOT_INVOICED" or "WAITING" or "WAITING_FOR_INVOICE") return MarketplaceInvoiceStatus.NotInvoiced;
        if (!string.IsNullOrWhiteSpace(invoiceNumber) || !string.IsNullOrWhiteSpace(invoiceUrl)) return MarketplaceInvoiceStatus.Invoiced;

        // Older Trendyol payloads sometimes exposed only the package status.
        // Preserve this as positive evidence, while never treating a generic
        // delivered/shipped status as proof that an invoice exists.
        return packageRawStatus?.Trim().ToUpperInvariant() == "INVOICED"
            ? MarketplaceInvoiceStatus.Invoiced
            : MarketplaceInvoiceStatus.Unknown;
    }

    public static bool ShouldApply(
        MarketplaceInvoiceStatus current,
        DateTimeOffset? currentSourceUpdatedAt,
        DateTimeOffset? currentObservedAt,
        MarketplaceInvoiceStatus incoming,
        DateTimeOffset? incomingSourceUpdatedAt,
        DateTimeOffset incomingObservedAt)
    {
        if (incoming == MarketplaceInvoiceStatus.Unknown) return false;
        if (current == MarketplaceInvoiceStatus.Invoiced && incoming != MarketplaceInvoiceStatus.Invoiced)
            return false;
        if (currentSourceUpdatedAt is { } currentSource && incomingSourceUpdatedAt is { } incomingSource && incomingSource < currentSource)
            return false;
        if (currentSourceUpdatedAt is { } sameCurrentSource && incomingSourceUpdatedAt is { } sameIncomingSource
            && sameIncomingSource == sameCurrentSource && current != incoming)
            return false;
        if (current == incoming)
            return incomingSourceUpdatedAt > currentSourceUpdatedAt || incomingObservedAt > currentObservedAt;
        if (incomingSourceUpdatedAt is not null && currentSourceUpdatedAt is null) return true;
        return currentObservedAt is null || incomingObservedAt >= currentObservedAt;
    }
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

    public static bool CanTransition(ShipmentPackageStatus current, ShipmentPackageStatus next)
    {
        if (current == next) return true;
        if (Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next)) return true;

        // Marketplace snapshots are authoritative and can skip intermediate
        // workflow states (for example NEW -> SHIPPED). Accept a monotonic
        // forward projection while keeping local terminal states immutable.
        if (current is ShipmentPackageStatus.Cancelled or ShipmentPackageStatus.Returned) return false;
        if (current == ShipmentPackageStatus.ManualReview) return next != ShipmentPackageStatus.ManualReview;
        if (next is ShipmentPackageStatus.New or ShipmentPackageStatus.ManualReview or ShipmentPackageStatus.Cancelled) return false;

        return ProgressRank(next) > ProgressRank(current);
    }

    private static int ProgressRank(ShipmentPackageStatus status) => status switch
    {
        ShipmentPackageStatus.New => 10,
        ShipmentPackageStatus.PartiallyCancelled => 20,
        ShipmentPackageStatus.Processing => 30,
        ShipmentPackageStatus.OnHold => 40,
        ShipmentPackageStatus.ReadyToShip => 50,
        ShipmentPackageStatus.Shipped => 60,
        ShipmentPackageStatus.Undelivered => 70,
        ShipmentPackageStatus.Delivered => 80,
        ShipmentPackageStatus.ReturnInTransit => 90,
        ShipmentPackageStatus.Returned => 100,
        ShipmentPackageStatus.Cancelled => 0,
        _ => 110
    };
}

public static class OpenOrderLifecyclePolicy
{
    public static bool ShouldPoll(ShipmentPackageStatus status) =>
        status is not ShipmentPackageStatus.Delivered and not ShipmentPackageStatus.Cancelled and not ShipmentPackageStatus.Returned;
}

public static class ShipmentPackageStatusPolicy
{
    public static ShipmentPackageStatus FromRemote(string? rawStatus) => (rawStatus ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "CREATED" => ShipmentPackageStatus.New,
        "PICKING" => ShipmentPackageStatus.Processing,
        "INVOICED" or "READY_TO_SHIP" or "READYTOSHIP" => ShipmentPackageStatus.ReadyToShip,
        "SHIPPED" => ShipmentPackageStatus.Shipped,
        "DELIVERED" => ShipmentPackageStatus.Delivered,
        "PARTIALLY_CANCELLED" or "PARTIALLYCANCELLED" => ShipmentPackageStatus.PartiallyCancelled,
        "CANCELLED" or "CANCELED" or "UNSUPPLIED" => ShipmentPackageStatus.Cancelled,
        "UNDELIVERED" => ShipmentPackageStatus.Undelivered,
        "RETURN_IN_TRANSIT" or "RETURNINTRANSIT" => ShipmentPackageStatus.ReturnInTransit,
        "RETURNED" => ShipmentPackageStatus.Returned,
        "AWAITING" or "UNPACKED" or "AT_COLLECTION_POINT" => ShipmentPackageStatus.OnHold,
        _ => ShipmentPackageStatus.ManualReview
    };

    public static ShipmentPackageStatus Aggregate(IEnumerable<ShipmentPackageStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Length == 0) return ShipmentPackageStatus.New;
        if (values.Contains(ShipmentPackageStatus.ManualReview)) return ShipmentPackageStatus.ManualReview;

        // An order remains operational while any package still needs work. A
        // cancelled split package must not hide another package that is being
        // shipped or delivered.
        var operational = values.Where(status => status is not ShipmentPackageStatus.Cancelled and not ShipmentPackageStatus.Returned);
        if (operational.Any()) return operational.OrderByDescending(Rank).First();
        if (values.Contains(ShipmentPackageStatus.Returned)) return ShipmentPackageStatus.Returned;
        return ShipmentPackageStatus.Cancelled;
    }

    public static int Rank(ShipmentPackageStatus status) => status switch
    {
        ShipmentPackageStatus.New => 10,
        ShipmentPackageStatus.PartiallyCancelled => 20,
        ShipmentPackageStatus.Processing => 30,
        ShipmentPackageStatus.OnHold => 40,
        ShipmentPackageStatus.ReadyToShip => 50,
        ShipmentPackageStatus.Shipped => 60,
        ShipmentPackageStatus.Undelivered => 70,
        ShipmentPackageStatus.Delivered => 80,
        ShipmentPackageStatus.ReturnInTransit => 90,
        ShipmentPackageStatus.Returned => 100,
        ShipmentPackageStatus.Cancelled => 0,
        _ => 110
    };
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
    public DateTimeOffset? ShipmentDueAt { get; set; }
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
    public string? SourceSnapshotJson { get; set; }
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
    public decimal GrossAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal NetAmount { get; set; }
    public ShipmentPackageStatus Status { get; set; }
    public required string RawStatus { get; set; }
    public DateTimeOffset StatusOccurredAt { get; set; }
    public MarketplaceInvoiceStatus MarketplaceInvoiceStatus { get; set; }
    public string? MarketplaceInvoiceRawStatus { get; set; }
    public string? MarketplaceInvoiceNumber { get; set; }
    public string? MarketplaceInvoiceUrl { get; set; }
    public DateTimeOffset? MarketplaceInvoiceSourceUpdatedAt { get; set; }
    public DateTimeOffset? MarketplaceInvoiceObservedAt { get; set; }
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
