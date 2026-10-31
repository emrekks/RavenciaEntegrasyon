using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// "Platformlarda Güncelle" on PTT AVM: the picked fields of a card PTT AVM already holds. The values are the
// publication's (PttAvmProductPublicationComposer, so a model family is the same product under the same parent barcode
// with the same name, description and images as when it was sent), plus the card's warranty and delivery time. The
// family's panel variants ride along so the adapter can carry the variants PTT AVM lists only by name; the adapter
// sends every variant PTT AVM holds, never the panel's list as such. Only a card with a publication on this connection
// (listing variants) is updated: the job reports per listing.
internal sealed class PttAvmProductUpdateComposer(AppDbContext db, IConfiguration configuration)
{
    public async Task<ServiceResult<ProductUpdateDraft>> BuildAsync(Guid tenantId, Guid productId, Guid connectionId, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null) return ServiceResult<ProductUpdateDraft>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.ConnectionId == connectionId, cancellationToken);
        if (profile is null || !await db.ChannelListingVariants.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ProfileId == profile.Id, cancellationToken))
            return ServiceResult<ProductUpdateDraft>.Fail("PTTAVM_PUBLICATION_REQUIRED", "PTT AVM'de yalnız panelden yayınlanmış (yayın kaydı olan) kart güncellenebilir; bu kartın PTT AVM yayın kaydı yok.", 409);

        var publication = await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, cancellationToken);
        if (!publication.Succeeded) return ServiceResult<ProductUpdateDraft>.Fail(publication.Error!.Code, publication.Error.Message, publication.Error.Status, publication.Error.FieldErrors);
        var sent = PttAvmProductPayloadRules.Read(publication.Value!.PayloadJson, out var invalid);
        if (sent is null) return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün içeriği geçersiz.", 422);

        var deliveryDays = ProductPublicationComposer.DeliveryDays(profile.DeliveryTimeDays, product.DeliveryDays);
        var built = Build(sent, fields, product.WarrantyMonths, deliveryDays);
        if (built.Error is { } error) return ServiceResult<ProductUpdateDraft>.Fail(error.Code, error.Message, 422);
        var payloadJson = PttAvmProductUpdateRules.Payload(built.Draft!);
        if (PttAvmProductUpdateRules.Read(payloadJson, out var refused) is null) return ServiceResult<ProductUpdateDraft>.Fail("PRODUCT_UPDATE_PAYLOAD_INVALID", refused ?? "PTT AVM ürün güncelleme içeriği geçersiz.", 422);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        return ServiceResult<ProductUpdateDraft>.Ok(new(publication.Value.ProfileId, new ProductUpdatePublication(productId, PttAvmProductUpdateRules.Mode, hash, payloadJson, "", "", ""), publication.Value.Variants));
    }

    internal sealed record BuildError(string Code, string Message);

    // The update from the publication's request and the card's warranty and delivery days (pure, so it can be tested).
    // A picked field without a value PTT AVM takes is refused, never sent empty.
    internal static (PttAvmProductUpdateRules.UpdateDraft? Draft, BuildError? Error) Build(PttAvmProductPayloadRules.ProductDraft sent, IReadOnlyList<string> fields, int? warrantyMonths, int? deliveryDays)
    {
        bool Picked(string field) => fields.Contains(field, StringComparer.Ordinal);
        int? warranty = null;
        if (Picked(ProductUpdateFields.Warranty))
        {
            warranty = warrantyMonths ?? 0;
            if (warranty is < 0 or > PttAvmProductUpdateRules.MaxWarrantyMonths)
                return (null, new("PTTAVM_WARRANTY_INVALID", $"PTT AVM garanti süresini en çok {PttAvmProductUpdateRules.MaxWarrantyMonths} ay alır; ürünün garanti süresi {warranty} ay."));
        }
        int? delivery = null;
        if (Picked(ProductUpdateFields.Delivery))
        {
            if (deliveryDays is not { } days) return (null, new("DELIVERY_DAYS_REQUIRED", "PTT AVM'ye gönderilecek teslimat süresi girilmemiş; ürün kartında Teslimat Süresi girin."));
            if (days is < 0 or > PttAvmProductUpdateRules.MaxCourierDeliveryDays)
                return (null, new("PTTAVM_DELIVERY_DAYS_INVALID", $"PTT AVM tahmini kargoya teslim süresini en çok {PttAvmProductUpdateRules.MaxCourierDeliveryDays} gün alır; ürünün teslimat süresi {days} gün."));
            delivery = days;
        }
        var variants = sent.Variants?.Select(x => new PttAvmProductUpdateRules.PanelVariant(x.Barcode, x.Color, x.Size)).ToList();
        return (new(sent.Barcode, fields,
            Picked(ProductUpdateFields.Title) ? sent.Name : null,
            Picked(ProductUpdateFields.Description) ? sent.LongDescription : null,
            Picked(ProductUpdateFields.Images) ? sent.Images : null,
            warranty,
            Picked(ProductUpdateFields.Desi) ? sent.Desi : null,
            Picked(ProductUpdateFields.VatRate) ? sent.VatRate : null,
            delivery,
            variants), null);
    }
}
