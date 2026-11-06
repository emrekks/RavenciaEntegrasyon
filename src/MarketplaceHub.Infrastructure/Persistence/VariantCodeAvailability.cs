using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Which stock codes and barcodes the tenant's catalog already holds. The editor asks before it generates codes from a
// model code (one request for a whole range of candidates, matched on the indexed normalized keys), and a save that
// clashes names each code and the card that holds it, so the editor can mark the rows.
public static class VariantCodeAvailability
{
    public const int MaxCodesPerRequest = 1000;
    public const int MaxCodeLength = 160;

    public sealed record TakenVariantCode(string Code, string Field, Guid VariantId, Guid ProductId, string ProductTitle);
    public sealed record Row(Guid VariantId, Guid ProductId, string ProductTitle, string SkuNormalized, string? BarcodeNormalized);

    public static IReadOnlyList<string> RequestedCodes(IEnumerable<string?>? codes)
    {
        var list = (codes ?? []).Select(code => code?.Trim() ?? "").Where(code => code.Length is > 0 and <= MaxCodeLength).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > MaxCodesPerRequest) throw new ArgumentException($"En fazla {MaxCodesPerRequest} kod sorulabilir.", nameof(codes));
        return list;
    }

    // The editor's own saved variants: their codes are theirs, not taken.
    public static Guid[] ExcludedVariantIds(IEnumerable<Guid>? ids)
    {
        var list = (ids ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (list.Length > MaxCodesPerRequest * 2) throw new ArgumentException($"En fazla {MaxCodesPerRequest * 2} varyant hariç tutulabilir.", nameof(ids));
        return list;
    }

    // Every requested code another variant holds, once per field: "sku" when a stock code has its key, "barcode" when a
    // barcode has it. The first holder is named.
    public static List<TakenVariantCode> Match(IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, IReadOnlyCollection<Row> rows)
    {
        var taken = new List<TakenVariantCode>();
        foreach (var code in skus)
        {
            var keys = CatalogKeyNormalizer.LookupKeys(code);
            if (rows.FirstOrDefault(row => keys.Contains(row.SkuNormalized, StringComparer.Ordinal)) is { } row)
                taken.Add(new(code, "sku", row.VariantId, row.ProductId, row.ProductTitle));
        }
        foreach (var code in barcodes)
        {
            var keys = CatalogKeyNormalizer.LookupKeys(code);
            if (rows.FirstOrDefault(row => row.BarcodeNormalized is { } barcode && keys.Contains(barcode, StringComparer.Ordinal)) is { } row)
                taken.Add(new(code, "barcode", row.VariantId, row.ProductId, row.ProductTitle));
        }
        return taken;
    }

    public static async Task<List<TakenVariantCode>> FindAsync(AppDbContext db, IQueryable<ProductVariant> variants, IReadOnlyCollection<string> skus, IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken)
    {
        var skuKeys = CatalogKeyNormalizer.LookupKeys(skus);
        var barcodeKeys = CatalogKeyNormalizer.LookupKeys(barcodes);
        if (skuKeys.Length == 0 && barcodeKeys.Length == 0) return [];
        var rows = await (from variant in variants.AsNoTracking()
                          join product in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { product.TenantId, ProductId = product.Id }
                          where skuKeys.Contains(variant.SkuNormalized) || variant.BarcodeNormalized != null && barcodeKeys.Contains(variant.BarcodeNormalized)
                          select new Row(variant.Id, variant.ProductId, product.Title, variant.SkuNormalized, variant.BarcodeNormalized))
            .Take(MaxCodesPerRequest * 2).ToListAsync(cancellationToken);
        return Match(skus, barcodes, rows);
    }

    // "MZ021Y-CD-01 ('Kadın Çiçek … Bluz' ürününde), …" for a refused save, and the same codes as field errors the
    // editor reads to mark its rows ("sku\tMZ021Y-CD-01\tKadın Çiçek … Bluz").
    public static (string Message, Dictionary<string, string[]> Fields) Describe(string lead, IReadOnlyCollection<TakenVariantCode> taken)
    {
        var shown = taken.Take(5).Select(item => $"{item.Code} ('{item.ProductTitle}' ürününde)");
        var more = taken.Count > 5 ? $" ve {taken.Count - 5} kod daha" : "";
        var message = taken.Count == 0 ? lead : $"{lead}: {string.Join(", ", shown)}{more}.";
        return (message, new Dictionary<string, string[]> { ["variantCodeConflicts"] = [.. taken.Select(item => $"{item.Field}\t{item.Code}\t{item.ProductTitle}")] });
    }

    // O7/D6: a row of the family as one save leaves it: a kept variant with its updated codes ("variant:{id}") or a new
    // one ("new:{index}"). Deleted variants are not rows, so their codes may go to another row in the same save.
    public sealed record SaveRow(string RowRef, string? Sku, string? Barcode);
    public sealed record DuplicateInSave(string Field, string Code, IReadOnlyList<string> RowRefs);
    public const string SameSaveMarker = "SAME_SAVE";

    // Every stock code or barcode that more than one row would hold after the save (the unique index would refuse it).
    public static List<DuplicateInSave> DuplicatesInSave(IReadOnlyList<SaveRow> rows)
    {
        static IEnumerable<DuplicateInSave> Find(IReadOnlyList<SaveRow> rows, string field, Func<SaveRow, string?> codeOf) =>
            rows.Select(row => (Row: row, Code: codeOf(row)?.Trim() ?? ""))
                .Where(item => item.Code.Length > 0 && CatalogKeyNormalizer.Key(item.Code).Length > 0)
                .GroupBy(item => CatalogKeyNormalizer.Key(item.Code), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new DuplicateInSave(field, group.First().Code, [.. group.Select(item => item.Row.RowRef)]));
        return [.. Find(rows, "sku", row => row.Sku), .. Find(rows, "barcode", row => row.Barcode)];
    }

    // "MZ071-M iki varyantta kullanılıyor" and the field errors that mark every row holding it
    // ("sku\tMZ071-M\t\tSAME_SAVE\tvariant:…,new:0").
    public static (string Message, Dictionary<string, string[]> Fields) DescribeDuplicates(IReadOnlyCollection<DuplicateInSave> duplicates)
    {
        static string Count(int count) => count == 2 ? "iki" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shown = duplicates.Take(5).Select(item => $"{item.Code} {Count(item.RowRefs.Count)} varyantta kullanılıyor");
        var more = duplicates.Count > 5 ? $" ve {duplicates.Count - 5} kod daha" : "";
        var message = $"Aynı kod bu kayıtta birden çok varyanta verilmiş; hiçbir değişiklik kaydedilmedi. {string.Join(", ", shown)}{more}. Kodları düzeltip tekrar kaydedin.";
        return (message, new Dictionary<string, string[]> { ["variantCodeConflicts"] = [.. duplicates.Select(item => $"{item.Field}\t{item.Code}\t\t{SameSaveMarker}\t{string.Join(',', item.RowRefs)}")] });
    }
}
