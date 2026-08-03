using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class F3ModelConfiguration
{
    public static void ConfigureF3Models(this ModelBuilder builder)
    {
        ConfigureConnectionEvidence(builder);
        ConfigureOrders(builder);
        ConfigureShipments(builder);
        ConfigureReturns(builder);
    }

    private static void ConfigureConnectionEvidence(ModelBuilder builder)
    {
        builder.Entity<PlatformCredential>(entity =>
        {
            entity.ToTable("platform_credentials", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.CredentialType).HasMaxLength(64); entity.Property(x => x.ProtectedPayload).HasMaxLength(8192); entity.Property(x => x.MaskedHint).HasMaxLength(160); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.RevokedAt });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<PlatformCapability>(entity =>
        {
            entity.ToTable("platform_capabilities", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(96); entity.Property(x => x.SupportLevel).HasConversion<string>().HasMaxLength(32); entity.Property(x => x.ApiVersion).HasMaxLength(64); entity.Property(x => x.Environment).HasMaxLength(24); entity.Property(x => x.StoreScope).HasMaxLength(256); entity.Property(x => x.SourceUrl).HasMaxLength(1024); entity.Property(x => x.SourceVersion).HasMaxLength(128); entity.Property(x => x.RequiredScope).HasMaxLength(256); entity.Property(x => x.FixtureChecksum).HasMaxLength(128); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Code, x.ApiVersion, x.Environment, x.StoreScope }).IsUnique();
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<WebhookSubscription>(entity =>
        {
            entity.ToTable("webhook_subscriptions", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RouteTokenHash).HasMaxLength(128); entity.Property(x => x.AuthenticationType).HasMaxLength(32); entity.Property(x => x.ProtectedVerifierSecret).HasMaxLength(8192); entity.Property(x => x.ExternalSubscriptionId).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.RouteTokenHash }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SyncCursor>(entity =>
        {
            entity.ToTable("sync_cursors", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceType).HasMaxLength(64); entity.Property(x => x.OpaqueCursor).HasMaxLength(4096); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ResourceType }).IsUnique();
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ConnectionSyncPolicy>(entity =>
        {
            entity.ToTable("connection_sync_policies", "integration", table => table.HasCheckConstraint("ck_connection_sync_policy_intervals", "\"IntervalSeconds\" > 0 AND \"OverlapSeconds\" >= 0 AND \"JitterSeconds\" >= 0")); entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceType).HasMaxLength(64); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ResourceType }).IsUnique();
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReconciliationRun>(entity =>
        {
            entity.ToTable("reconciliation_runs", "integration"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Scope).HasMaxLength(96); entity.Property(x => x.Status).HasMaxLength(24); entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.StartedAt });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReconciliationDifference>(entity =>
        {
            entity.ToTable("reconciliation_differences", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.EntityType).HasMaxLength(64); entity.Property(x => x.EntityKey).HasMaxLength(256); entity.Property(x => x.FieldName).HasMaxLength(96); entity.Property(x => x.LocalValueHash).HasMaxLength(128); entity.Property(x => x.RemoteValueHash).HasMaxLength(128); entity.Property(x => x.Resolution).HasMaxLength(32);
            entity.HasIndex(x => new { x.TenantId, x.RunId, x.EntityType, x.EntityKey, x.FieldName }).IsUnique();
            entity.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.RunId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureOrders(ModelBuilder builder)
    {
        builder.Entity<Order>(entity =>
        {
            entity.ToTable("orders", "sales", table => { table.HasCheckConstraint("ck_order_amounts", "\"GrossAmount\" >= 0 AND \"DiscountAmount\" >= 0 AND \"NetAmount\" >= 0"); table.HasCheckConstraint("ck_order_currency", "char_length(\"Currency\")=3"); });
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.ExternalOrderId).HasMaxLength(256); entity.Property(x => x.OrderNumber).HasMaxLength(160); entity.Property(x => x.Currency).HasColumnType("char(3)"); entity.Property(x => x.GrossAmount).HasPrecision(19, 4); entity.Property(x => x.DiscountAmount).HasPrecision(19, 4); entity.Property(x => x.NetAmount).HasPrecision(19, 4); entity.Property(x => x.DerivedStatus).HasMaxLength(48); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalOrderId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.OrderedAt, x.Id });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<OrderLine>(entity =>
        {
            entity.ToTable("order_lines", "sales", table => table.HasCheckConstraint("ck_order_line_quantities", "\"OrderedQuantity\" >= 0 AND \"CancelledQuantity\" >= 0 AND \"ShippedQuantity\" >= 0 AND \"DeliveredQuantity\" >= 0 AND \"ReturnedQuantity\" >= 0 AND \"ShippedQuantity\" <= \"OrderedQuantity\" AND \"DeliveredQuantity\" <= \"ShippedQuantity\" AND \"ReturnedQuantity\" <= \"DeliveredQuantity\""));
            entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.ExternalLineId).HasMaxLength(256); entity.Property(x => x.Sku).HasMaxLength(160); entity.Property(x => x.Barcode).HasMaxLength(160); entity.Property(x => x.TitleSnapshot).HasMaxLength(512); entity.Property(x => x.RawStatus).HasMaxLength(96); entity.Property(x => x.OrderedQuantity).HasPrecision(19, 4); entity.Property(x => x.CancelledQuantity).HasPrecision(19, 4); entity.Property(x => x.ShippedQuantity).HasPrecision(19, 4); entity.Property(x => x.DeliveredQuantity).HasPrecision(19, 4); entity.Property(x => x.ReturnedQuantity).HasPrecision(19, 4); entity.Property(x => x.UnitPrice).HasPrecision(19, 4); entity.Property(x => x.VatRate).HasPrecision(7, 4); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.OrderId, x.ExternalLineId }).IsUnique();
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<OrderFinancialAllocation>(entity =>
        {
            entity.ToTable("order_financial_allocations", "sales"); entity.HasKey(x => x.Id); entity.Property(x => x.AllocationType).HasMaxLength(64); entity.Property(x => x.Amount).HasPrecision(19, 4); entity.Property(x => x.Currency).HasColumnType("char(3)"); entity.Property(x => x.SourceKey).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.OrderId, x.SourceKey }).IsUnique();
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrderLine>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderLineId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ShipmentPackage>(entity =>
        {
            entity.ToTable("shipment_packages", "sales"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.ExternalPackageId).HasMaxLength(256); entity.Property(x => x.OriginExternalPackageId).HasMaxLength(256); entity.Property(x => x.CargoProviderExternalId).HasMaxLength(256); entity.Property(x => x.CargoTrackingNumber).HasMaxLength(256); entity.Property(x => x.GrossAmount).HasPrecision(19, 4); entity.Property(x => x.DiscountAmount).HasPrecision(19, 4); entity.Property(x => x.NetAmount).HasPrecision(19, 4); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); entity.Property(x => x.RawStatus).HasMaxLength(96); entity.Property(x => x.RemoteVersion).HasMaxLength(128); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalPackageId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.Status, x.StatusOccurredAt });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PackageLineAllocation>(entity =>
        {
            entity.ToTable("package_line_allocations", "sales", table => table.HasCheckConstraint("ck_package_allocation_quantities", "\"AllocatedQuantity\" >= 0 AND \"CancelledQuantity\" >= 0 AND \"ShippedQuantity\" >= 0 AND \"DeliveredQuantity\" >= 0 AND \"ReturnedQuantity\" >= 0")); entity.HasKey(x => x.Id);
            entity.Property(x => x.AllocatedQuantity).HasPrecision(19, 4); entity.Property(x => x.CancelledQuantity).HasPrecision(19, 4); entity.Property(x => x.ShippedQuantity).HasPrecision(19, 4); entity.Property(x => x.DeliveredQuantity).HasPrecision(19, 4); entity.Property(x => x.ReturnedQuantity).HasPrecision(19, 4); entity.Property(x => x.SourceEventId).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.PackageId, x.OrderLineId, x.SourceEventId }).IsUnique();
            entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrderLine>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderLineId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<OrderStatusHistory>(entity =>
        {
            entity.ToTable("order_status_history", "sales"); entity.HasKey(x => x.Id); entity.Property(x => x.CanonicalStatus).HasMaxLength(48); entity.Property(x => x.RawStatus).HasMaxLength(96); entity.Property(x => x.SourceEventId).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.OrderId, x.SourceEventId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.OrderId, x.OccurredAt });
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureShipments(ModelBuilder builder)
    {
        builder.Entity<ShipmentDocument>(entity =>
        {
            entity.ToTable("shipment_documents", "sales"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.DocumentKind).HasMaxLength(64); entity.Property(x => x.Format).HasMaxLength(32); entity.Property(x => x.Source).HasMaxLength(32); entity.Property(x => x.Checksum).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.PackageId, x.DocumentKind, x.DocumentVersion }).IsUnique();
            entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FileAsset>().WithMany().HasForeignKey(x => x.FileAssetId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ShipmentDocumentAttempt>(entity =>
        {
            entity.ToTable("shipment_document_attempts", "sales"); entity.HasKey(x => x.Id); entity.Property(x => x.IdempotencyKey).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(32); entity.Property(x => x.ExternalOperationId).HasMaxLength(256); entity.Property(x => x.ErrorCode).HasMaxLength(96);
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShipmentDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<CargoProviderMapping>(entity =>
        {
            entity.ToTable("cargo_provider_mappings", "sales"); entity.HasKey(x => x.Id); entity.Property(x => x.ExternalProviderId).HasMaxLength(256); entity.Property(x => x.ExternalProviderName).HasMaxLength(256); entity.Property(x => x.LocalProviderCode).HasMaxLength(96); entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalProviderId }).IsUnique(); entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureReturns(ModelBuilder builder)
    {
        builder.Entity<ReturnClaim>(entity =>
        {
            entity.ToTable("return_claims", "sales"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.ExternalClaimId).HasMaxLength(256); entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); entity.Property(x => x.RawStatus).HasMaxLength(96); entity.Property(x => x.ReasonCode).HasMaxLength(96); entity.Property(x => x.ReasonText).HasMaxLength(1024); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ExternalClaimId }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.Status, x.ActionDueAt });
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict); entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReturnLine>(entity =>
        {
            entity.ToTable("return_lines", "sales", table => table.HasCheckConstraint("ck_return_line_quantity", "\"Quantity\" > 0")); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.ExternalLineId).HasMaxLength(256); entity.Property(x => x.Quantity).HasPrecision(19, 4); entity.HasIndex(x => new { x.TenantId, x.ClaimId, x.ExternalLineId }).IsUnique(); entity.HasOne<ReturnClaim>().WithMany().HasForeignKey(x => new { x.TenantId, x.ClaimId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade); entity.HasOne<OrderLine>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderLineId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReturnDecision>(entity =>
        {
            entity.ToTable("return_decisions", "sales"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id }); entity.Property(x => x.Action).HasMaxLength(48); entity.Property(x => x.ReasonCode).HasMaxLength(96); entity.Property(x => x.Explanation).HasMaxLength(2048); entity.Property(x => x.IdempotencyKey).HasMaxLength(256); entity.Property(x => x.Status).HasMaxLength(32); entity.Property(x => x.ExternalOperationId).HasMaxLength(256); entity.Property(x => x.ErrorCode).HasMaxLength(96); entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); entity.HasOne<ReturnClaim>().WithMany().HasForeignKey(x => new { x.TenantId, x.ClaimId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReturnEvidence>(entity =>
        {
            entity.ToTable("return_evidence", "sales"); entity.HasKey(x => x.Id); entity.Property(x => x.EvidenceKind).HasMaxLength(64); entity.Property(x => x.Checksum).HasMaxLength(128); entity.HasIndex(x => new { x.TenantId, x.DecisionId, x.FileAssetId }).IsUnique(); entity.HasOne<ReturnClaim>().WithMany().HasForeignKey(x => new { x.TenantId, x.ClaimId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict); entity.HasOne<ReturnDecision>().WithMany().HasForeignKey(x => new { x.TenantId, x.DecisionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict); entity.HasOne<FileAsset>().WithMany().HasForeignKey(x => x.FileAssetId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReturnStockDisposition>(entity =>
        {
            entity.ToTable("return_stock_dispositions", "sales", table => table.HasCheckConstraint("ck_return_stock_disposition_quantity", "\"Quantity\" > 0")); entity.HasKey(x => x.Id); entity.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(24); entity.Property(x => x.Quantity).HasPrecision(19, 4); entity.Property(x => x.IdempotencyKey).HasMaxLength(256); entity.Property(x => x.Reason).HasMaxLength(512); entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique(); entity.HasOne<ReturnClaim>().WithMany().HasForeignKey(x => new { x.TenantId, x.ClaimId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict); entity.HasOne<ReturnLine>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReturnLineId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict); entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.InventoryItemId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
