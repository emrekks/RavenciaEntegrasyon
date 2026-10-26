using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A marketplace attribute filled from the product itself rather than from a panel attribute: Hepsiburada's Kombin ID
// takes each variant's own model code. Kept per connection on the attribute's default (ConnectionAttributeDefault
// .SourceField), so it holds in every category of the connection; only for attributes that take free text.
public static class ProductFieldSources
{
    public const string ModelCode = "MODEL_CODE";
    public const string Sku = "SKU";
    public const string Barcode = "BARCODE";
    public const string Title = "TITLE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { ModelCode, Sku, Barcode, Title };

    // The marketplaces whose publication sends free text for an attribute (Pazarama sends listed value ids only).
    public static readonly string[] Platforms = ["HEPSIBURADA", "TRENDYOL", "N11"];

    public static string Label(string field) => field switch
    {
        ModelCode => "Model Kodu",
        Sku => "Stok Kodu",
        Barcode => "Barkod",
        Title => "Ürün Adı",
        _ => field
    };

    // The text sent for a variant; null when the variant has none (the publication then stops and says which).
    public static string? Value(string field, ProductVariant variant, string title) => (field switch
    {
        ModelCode => variant.ModelCode,
        Sku => variant.Sku,
        Barcode => variant.Barcode,
        Title => title,
        _ => null
    }) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public sealed record FieldDefault(string ExternalAttributeId, string AttributeName, string Field);

    // The connection's product-field defaults that the category's current attribute list can take (free text only).
    public static async Task<IReadOnlyList<FieldDefault>> LoadAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyList<ReferenceItem> remoteAttributes, CancellationToken cancellationToken)
    {
        var rows = await db.ConnectionAttributeDefaults.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.SourceField != null)
            .Select(x => new { x.ExternalAttributeId, x.SourceField })
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => (Row: row, Remote: remoteAttributes.FirstOrDefault(remote => string.Equals(remote.ExternalId, row.ExternalAttributeId, StringComparison.Ordinal))))
            .Where(entry => entry.Remote is { AllowsCustomValue: true } && All.Contains(entry.Row.SourceField!))
            .Select(entry => new FieldDefault(entry.Row.ExternalAttributeId, entry.Remote!.Name, entry.Row.SourceField!))
            .OrderBy(entry => entry.ExternalAttributeId, StringComparer.Ordinal)
            .ToList();
    }

    public static string MissingValueMessage(string sku, FieldDefault field) => $"'{sku}' için '{field.AttributeName}' alanına gönderilecek {Label(field.Field)} boş. Ürünün {Label(field.Field)} bilgisini doldurun.";
}
