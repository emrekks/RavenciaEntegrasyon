using System.Text.Json;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A local edit is protected from the next marketplace import by the product version and by the dirty
// field list of the product's marketplace links (["product"], ["media"], ...). Every edit path records
// itself here so the import can tell what the user owns.
public static class CatalogLocalEdits
{
    public const string ProductField = "product";
    public const string MediaField = "media";

    public static string AddDirtyField(string? dirtyFieldsJson, string field)
    {
        var fields = DirtyFields(dirtyFieldsJson);
        if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);
        return JsonSerializer.Serialize(fields);
    }

    public static bool HasDirtyField(string? dirtyFieldsJson, string field) => DirtyFields(dirtyFieldsJson).Contains(field, StringComparer.Ordinal);

    private static List<string> DirtyFields(string? dirtyFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(dirtyFieldsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(dirtyFieldsJson)?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? []; }
        catch (JsonException) { return [ProductField]; }
    }

    // Marks every marketplace link of the products as carrying local changes of the given field.
    public static async Task MarkLinksDirtyAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, string field, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return;
        var ids = productIds.Distinct().ToArray();
        var links = db.MarketplaceProductLinks.Local.Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId)).ToList();
        var trackedIds = links.Select(x => x.Id).ToHashSet();
        links.AddRange((await db.MarketplaceProductLinks.Where(x => x.TenantId == tenantId && ids.Contains(x.ProductId)).ToListAsync(cancellationToken)).Where(x => !trackedIds.Contains(x.Id)));
        foreach (var link in links.DistinctBy(x => x.Id))
        {
            var next = AddDirtyField(link.DirtyFieldsJson, field);
            if (link.SyncStatus == "LOCAL_CHANGES_PENDING" && link.DirtyFieldsJson == next) continue;
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson = next;
            link.Version++;
        }
    }

    // A media edit (upload, URL, reorder, delete, clear) bumps the product version and marks its links.
    public static async Task MarkMediaEditedAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Product> products, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var product in products.DistinctBy(x => x.Id)) { product.UpdatedAt = now; product.Version++; }
        await MarkLinksDirtyAsync(db, tenantId, products.Select(x => x.Id).ToArray(), MediaField, cancellationToken);
    }

    public static async Task MarkMediaEditedAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> productIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToArray();
        var tracked = db.Products.Local.Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).ToList();
        var trackedIds = tracked.Select(x => x.Id).ToHashSet();
        var missing = ids.Where(id => !trackedIds.Contains(id)).ToArray();
        if (missing.Length > 0) tracked.AddRange(await db.Products.Where(x => x.TenantId == tenantId && missing.Contains(x.Id)).ToListAsync(cancellationToken));
        await MarkMediaEditedAsync(db, tenantId, tracked, now, cancellationToken);
    }
}
