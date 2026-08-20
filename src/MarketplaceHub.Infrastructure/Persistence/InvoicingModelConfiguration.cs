using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class InvoicingModelConfiguration
{
    public static void ConfigureInvoicingModels(this ModelBuilder builder)
    {
        builder.Entity<FileAsset>().HasAlternateKey(x => new { x.TenantId, x.Id });

        builder.Entity<LegalEntityProfile>(entity =>
        {
            entity.ToTable("legal_entity_profiles", "billing");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.ProtectedTaxId).HasMaxLength(4096);
            entity.Property(x => x.MaskedTaxId).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.Title, x.Status }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InvoicePolicy>(entity =>
        {
            entity.ToTable("invoice_policies", "billing");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.TriggerState).HasMaxLength(64);
            entity.Property(x => x.PackageScope).HasMaxLength(64);
            entity.Property(x => x.DueRule).HasMaxLength(128);
            entity.Property(x => x.RoundingRule).HasMaxLength(128);
            entity.Property(x => x.AdjustmentRule).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ProviderConnectionId }).IsUnique();
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProviderConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Invoice>(entity =>
        {
            entity.ToTable("invoices", "billing", table => table.HasCheckConstraint("ck_invoice_totals_nonnegative", "\"TaxExclusiveTotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"PayableTotal\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.InvoiceType).HasMaxLength(64);
            entity.Property(x => x.SequencePurpose).HasMaxLength(64);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.TaxExclusiveTotal).HasPrecision(19, 4);
            entity.Property(x => x.DiscountTotal).HasPrecision(19, 4);
            entity.Property(x => x.TaxTotal).HasPrecision(19, 4);
            entity.Property(x => x.PayableTotal).HasPrecision(19, 4);
            entity.Property(x => x.Note).HasMaxLength(512);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.ExternalReference).HasMaxLength(256);
            entity.Property(x => x.InvoiceNumber).HasMaxLength(64);
            entity.Property(x => x.EttnUuid).HasMaxLength(64);
            entity.Property(x => x.LastErrorCode).HasMaxLength(96);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OrderId, x.Status });
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProviderConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegalEntityProfile>().WithMany().HasForeignKey(x => new { x.TenantId, x.LegalEntityProfileId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InvoicePolicy>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoicePolicyId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalInvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InvoiceLine>(entity =>
        {
            entity.ToTable("invoice_lines", "billing", table => table.HasCheckConstraint("ck_invoice_line_values", "\"LineSequence\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountAmount\" >= 0 AND \"VatRate\" >= 0 AND \"VatAmount\" >= 0 AND \"LineTotal\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DescriptionSnapshot).HasMaxLength(1024);
            entity.Property(x => x.SkuSnapshot).HasMaxLength(160);
            entity.Property(x => x.UnitSnapshot).HasMaxLength(32);
            entity.Property(x => x.Quantity).HasPrecision(19, 4);
            entity.Property(x => x.UnitPrice).HasPrecision(19, 4);
            entity.Property(x => x.DiscountAmount).HasPrecision(19, 4);
            entity.Property(x => x.VatRate).HasPrecision(9, 4);
            entity.Property(x => x.VatAmount).HasPrecision(19, 4);
            entity.Property(x => x.LineTotal).HasPrecision(19, 4);
            entity.HasIndex(x => new { x.TenantId, x.InvoiceId, x.LineSequence }).IsUnique();
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrderLine>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrderLineId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InvoicePartySnapshot>(entity =>
        {
            entity.ToTable("invoice_party_snapshots", "billing");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(32);
            entity.Property(x => x.ProtectedContent).HasMaxLength(16384);
            entity.Property(x => x.ContentHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.InvoiceId, x.Role }).IsUnique();
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<InvoiceDocument>(entity =>
        {
            entity.ToTable("invoice_documents", "billing");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DocumentType).HasMaxLength(32);
            entity.Property(x => x.Sha256).HasMaxLength(128);
            entity.Property(x => x.ExternalDocumentId).HasMaxLength(256);
            entity.Property(x => x.PermanentUrl).HasMaxLength(4096);
            entity.HasIndex(x => new { x.TenantId, x.InvoiceId, x.DocumentType, x.Sha256 }).IsUnique();
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FileAsset>().WithMany().HasForeignKey(x => new { x.TenantId, x.FileAssetId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InvoiceSubmissionAttempt>(entity =>
        {
            entity.ToTable("invoice_submission_attempts", "billing");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestHash).HasMaxLength(128);
            entity.Property(x => x.Outcome).HasMaxLength(40);
            entity.Property(x => x.ErrorClass).HasMaxLength(48);
            entity.Property(x => x.ErrorCode).HasMaxLength(96);
            entity.Property(x => x.RemoteRequestId).HasMaxLength(256);
            entity.Property(x => x.ExternalReference).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.InvoiceId, x.AttemptNumber }).IsUnique();
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MarketplaceDelivery>(entity =>
        {
            entity.ToTable("marketplace_deliveries", "billing");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.RequestHash).HasMaxLength(128);
            entity.Property(x => x.DeliveryType).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.ExternalReference).HasMaxLength(256);
            entity.Property(x => x.ErrorCode).HasMaxLength(96);
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.InvoiceId, x.AttemptNumber }).IsUnique();
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => new { x.TenantId, x.InvoiceId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformConnection>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectionId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ShipmentPackage>().WithMany().HasForeignKey(x => new { x.TenantId, x.PackageId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
