using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CommissionTariffModelConfiguration
{
    public static void ConfigureCommissionTariffModels(this ModelBuilder builder)
    {
        builder.Entity<CommissionTariff>(entity =>
        {
            entity.ToTable("commission_tariffs", "catalog"); entity.HasKey(x => x.Id); entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.PlatformCode).HasMaxLength(64); entity.Property(x => x.PeriodText).HasMaxLength(120); entity.Property(x => x.PeriodLabel).HasMaxLength(40);
            entity.Property(x => x.FileName).HasMaxLength(260); entity.Property(x => x.FileHash).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.PeriodStart, x.PeriodEnd });
            // The same file uploaded again for the same period is the same tariff.
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.FileHash, x.PeriodStart }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CommissionTariffRow>(entity =>
        {
            entity.ToTable("commission_tariff_rows", "catalog"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Barcode).HasMaxLength(160); entity.Property(x => x.SellerSku).HasMaxLength(160); entity.Property(x => x.ModelCode).HasMaxLength(160);
            entity.Property(x => x.ProductName).HasMaxLength(500); entity.Property(x => x.Size).HasMaxLength(80); entity.Property(x => x.TariffGroup).HasMaxLength(80); entity.Property(x => x.Category).HasMaxLength(160); entity.Property(x => x.Brand).HasMaxLength(160); entity.Property(x => x.Stock).HasPrecision(19, 4);
            foreach (var property in new[] { nameof(CommissionTariffRow.Tier1Min), nameof(CommissionTariffRow.Tier2Max), nameof(CommissionTariffRow.Tier2Min), nameof(CommissionTariffRow.Tier3Max), nameof(CommissionTariffRow.Tier3Min), nameof(CommissionTariffRow.Tier4Max), nameof(CommissionTariffRow.BasisPrice), nameof(CommissionTariffRow.CurrentSalePrice) })
                entity.Property(property).HasPrecision(19, 4);
            foreach (var property in new[] { nameof(CommissionTariffRow.Rate1), nameof(CommissionTariffRow.Rate2), nameof(CommissionTariffRow.Rate3), nameof(CommissionTariffRow.Rate4), nameof(CommissionTariffRow.CurrentRate) })
                entity.Property(property).HasPrecision(7, 4);
            entity.HasIndex(x => new { x.TariffId, x.Barcode });
            entity.HasIndex(x => new { x.TariffId, x.SellerSku });
            entity.HasOne<CommissionTariff>().WithMany().HasForeignKey(x => new { x.TenantId, x.TariffId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CommissionTariffSourceFile>(entity =>
        {
            entity.ToTable("commission_tariff_source_files", "catalog"); entity.HasKey(x => x.TariffId);
            entity.HasOne<CommissionTariff>().WithOne().HasForeignKey<CommissionTariffSourceFile>(x => new { x.TenantId, x.TariffId }).HasPrincipalKey<CommissionTariff>(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CommissionTariffSelection>(entity =>
        {
            entity.ToTable("commission_tariff_selections", "catalog"); entity.HasKey(x => x.TariffId);
            entity.Property(x => x.BulkKind).HasMaxLength(16);
            entity.Property(x => x.MinRate).HasPrecision(19, 4); entity.Property(x => x.MinAmount).HasPrecision(19, 4);
            entity.Property(x => x.MaxRate).HasPrecision(19, 4); entity.Property(x => x.MaxAmount).HasPrecision(19, 4);
            entity.HasOne<CommissionTariff>().WithOne().HasForeignKey<CommissionTariffSelection>(x => new { x.TenantId, x.TariffId }).HasPrincipalKey<CommissionTariff>(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CommissionTariffPick>(entity =>
        {
            entity.ToTable("commission_tariff_picks", "catalog"); entity.HasKey(x => new { x.TariffId, x.RowId });
            entity.Property(x => x.Price).HasPrecision(19, 4);
            entity.HasIndex(x => x.RowId);
            // A pick goes with its tariff and with its row (a data reset deletes the rows first).
            entity.HasOne<CommissionTariff>().WithMany().HasForeignKey(x => new { x.TenantId, x.TariffId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CommissionTariffRow>().WithMany().HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PromotionOfferFile>(entity =>
        {
            entity.ToTable("promotion_offer_files", "catalog"); entity.HasKey(x => x.Id);
            entity.Property(x => x.PlatformCode).HasMaxLength(64); entity.Property(x => x.Kind).HasMaxLength(16); entity.Property(x => x.PeriodText).HasMaxLength(120);
            entity.Property(x => x.FileName).HasMaxLength(260); entity.Property(x => x.FileHash).HasMaxLength(64); entity.Property(x => x.RowsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Kind });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
