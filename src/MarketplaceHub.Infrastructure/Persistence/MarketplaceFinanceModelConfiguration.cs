using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class MarketplaceFinanceModelConfiguration
{
    public static void ConfigureMarketplaceFinanceModels(this ModelBuilder builder)
    {
        builder.Entity<MarketplaceFinanceTransaction>(entity =>
        {
            entity.ToTable("marketplace_finance_transactions", "sales"); entity.HasKey(x => x.Id);
            entity.Property(x => x.PlatformCode).HasMaxLength(64); entity.Property(x => x.Source).HasMaxLength(32);
            entity.Property(x => x.ExternalId).HasMaxLength(128); entity.Property(x => x.RequestedType).HasMaxLength(64);
            entity.Property(x => x.TransactionSubType).HasMaxLength(64); entity.Property(x => x.TransactionType).HasMaxLength(200);
            entity.Property(x => x.Category).HasMaxLength(32); entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.OrderNumber).HasMaxLength(128); entity.Property(x => x.ShipmentPackageId).HasMaxLength(128);
            entity.Property(x => x.Barcode).HasMaxLength(160); entity.Property(x => x.PaymentOrderId).HasMaxLength(64);
            foreach (var property in new[] { nameof(MarketplaceFinanceTransaction.Debt), nameof(MarketplaceFinanceTransaction.Credit), nameof(MarketplaceFinanceTransaction.CommissionAmount), nameof(MarketplaceFinanceTransaction.SellerRevenue) })
                entity.Property(property).HasPrecision(19, 4);
            entity.Property(x => x.CommissionRate).HasPrecision(9, 4);
            // The upsert key: a record read again (its payment order filled in later) updates the stored one.
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Source, x.ExternalId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.OrderNumber });
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.TransactionDate });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MarketplaceCargoInvoiceItem>(entity =>
        {
            entity.ToTable("marketplace_cargo_invoice_items", "sales"); entity.HasKey(x => x.Id);
            entity.Property(x => x.InvoiceSerialNumber).HasMaxLength(128); entity.Property(x => x.ShipmentPackageType).HasMaxLength(120);
            entity.Property(x => x.ParcelUniqueId).HasMaxLength(64); entity.Property(x => x.OrderNumber).HasMaxLength(128);
            entity.Property(x => x.Amount).HasPrecision(19, 4);
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.InvoiceSerialNumber, x.LineNo }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.OrderNumber });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MarketplaceFinanceSyncState>(entity =>
        {
            entity.ToTable("marketplace_finance_sync_states", "integration"); entity.HasKey(x => new { x.TenantId, x.ConnectionId });
            entity.Property(x => x.RejectedTypes).HasMaxLength(1000); entity.Property(x => x.LastError).HasMaxLength(1024);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
