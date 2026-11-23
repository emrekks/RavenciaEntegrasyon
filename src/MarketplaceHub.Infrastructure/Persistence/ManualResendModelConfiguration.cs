using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class ManualResendModelConfiguration
{
    // sales.manual_resends (ManualResend): one hand-added re-shipment per order. The order key cascades, so every delete
    // of orders (a connection's data, Tam Sıfırlama, Paneli Tamamen Sıfırla, code older than the table) takes its rows.
    public static void ConfigureManualResendModels(this ModelBuilder builder)
    {
        builder.Entity<ManualResend>(entity =>
        {
            entity.ToTable("manual_resends", "sales"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.OrderId }).IsUnique();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
