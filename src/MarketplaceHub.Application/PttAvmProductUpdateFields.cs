namespace MarketplaceHub.Application;

// "Platformlarda Güncelle" on PTT AVM. UpdateProductsV3 is an upsert by barcode in which a product field left out stays
// as it is ("Güncellenmesi amaçlanan alanlar dışındaki alanların gönderilmesine gerek yoktur"), so each picked field goes
// on its own: Name, LongDescription, Images, WarrantyDuration (0-24 months), Desi (0-300), VATRate (0/1/10/20) and
// EstimatedCourierDelivery (0-30 days). PTT AVM has no product attribute field (only a variant's colour and size), so
// ATTRIBUTES does not go. Price and stock go through the price-stock send, not this one.
public static class PttAvmProductUpdateFields
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(
        [ProductUpdateFields.Title, ProductUpdateFields.Description, ProductUpdateFields.Images, ProductUpdateFields.Warranty, ProductUpdateFields.Desi, ProductUpdateFields.VatRate, ProductUpdateFields.Delivery],
        StringComparer.Ordinal);

    // The picked fields PTT AVM takes, in a fixed order. No list (an older client) is all of them; an unknown code is
    // refused as everywhere else; a pick PTT AVM takes none of is refused instead of sending an empty request.
    public static ServiceResult<IReadOnlyList<string>> Parse(IReadOnlyCollection<string>? fields)
    {
        if (fields is not null)
        {
            var sections = ProductUpdateFields.Sections(fields);
            // Only a known code that no other marketplace's request carries could fail the check below; let the unknown
            // ones and the empty list fail as they do elsewhere.
            if (!sections.Succeeded && sections.Error!.Code == "PRODUCT_UPDATE_FIELD_UNKNOWN")
                return ServiceResult<IReadOnlyList<string>>.Fail(sections.Error.Code, sections.Error.Message, sections.Error.Status);
        }
        var codes = fields?.Select(field => field?.Trim().ToUpperInvariant() ?? "").ToHashSet(StringComparer.Ordinal);
        var picked = Ordered.Where(code => codes is null || codes.Contains(code)).ToList();
        if (picked.Count == 0)
            return codes is { Count: > 0 } && codes.Any(ProductUpdateFields.Known.Contains)
                ? ServiceResult<IReadOnlyList<string>>.Fail("PRODUCT_UPDATE_FIELDS_UNSUPPORTED", "Seçilen ürün bilgileri PTT AVM'ye gönderilemiyor (PTT AVM kategori özelliği almıyor).", 422)
                : ServiceResult<IReadOnlyList<string>>.Fail("PRODUCT_UPDATE_FIELDS_REQUIRED", "Gönderilecek en az bir ürün bilgisi alanı seçin.", 422);
        return ServiceResult<IReadOnlyList<string>>.Ok(picked);
    }

    private static readonly string[] Ordered =
        [ProductUpdateFields.Title, ProductUpdateFields.Description, ProductUpdateFields.Images, ProductUpdateFields.Warranty, ProductUpdateFields.Desi, ProductUpdateFields.VatRate, ProductUpdateFields.Delivery];
}
