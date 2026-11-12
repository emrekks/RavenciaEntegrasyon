using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The one way an import turns a brand name into a panel brand (owner decision 12 Nov 2026: imports map brands like
// categories), shared by Platformdan Ürün Aktar (MarketplaceJobProcessor.ImportBrandId, after the connection's own
// mapping), the classic file import (/imports, ImportJobProcessor) and the products Excel import (ProductTransferService),
// so one name always lands on one brand:
//   - the name is trimmed and cut to 160 characters and compared by the shared catalog key (CatalogKeyNormalizer:
//     "Gümüş Moda", "GUMUS-MODA" and " gümüş moda " are one brand), stored keys in their legacy forms included;
//   - of the brands with that key an active one wins, then one stored with the current key form, then the oldest id;
//   - a brand the user switched off is not revived and no second brand is opened beside it: the name is reported as
//     switched off and the product gets no brand from it;
//   - otherwise a new active panel brand is opened under the name (FindOrCreateAsync only; it is saved with the caller's
//     save). Brands opened through this lookup are remembered, so a later row of the same run takes the same one.
public sealed class PanelBrandByName(AppDbContext db, Guid tenantId)
{
    public const int MaximumNameLength = 160;

    // Brand: the usable panel brand. SwitchedOff: the brand the name names that the user switched off.
    public readonly record struct Match(Brand? Brand, Brand? SwitchedOff, bool Created = false)
    {
        public Guid? Id => Brand?.Id;
    }

    private readonly Dictionary<string, List<Brand>> loaded = new(StringComparer.Ordinal);

    public static string PanelName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length <= MaximumNameLength ? trimmed : trimmed[..MaximumNameLength].Trim();
    }

    public static string Key(string? name) => CatalogKeyNormalizer.Key(PanelName(name), MaximumNameLength);

    // Which of the stored brands the key names: active first, then the current key form, then the oldest id.
    public static T? Pick<T>(IEnumerable<T> brands, string key, Func<T, string> normalizedName, Func<T, bool> isActive, Func<T, Guid> id) where T : class =>
        key.Length == 0
            ? null
            : brands.Where(brand => CatalogKeyNormalizer.SameKey(normalizedName(brand), key, MaximumNameLength))
                .OrderByDescending(isActive).ThenBy(brand => normalizedName(brand) == key ? 0 : 1).ThenBy(id).FirstOrDefault();

    // Reads the brands the names may name in one query (a file's rows), so FindAsync and FindOrCreateAsync answer them
    // from memory.
    public async Task PreloadAsync(IEnumerable<string?> names, CancellationToken cancellationToken)
    {
        // The names themselves give the stored legacy forms (LookupKeys); their keys alone would not.
        var wanted = names.Select(PanelName).Where(name => Key(name) is { Length: > 0 } key && !loaded.ContainsKey(key)).ToList();
        var keys = wanted.Select(Key).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return;
        var lookup = CatalogKeyNormalizer.LookupKeys(wanted, MaximumNameLength);
        var brands = await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && lookup.Contains(x.NormalizedName)).ToListAsync(cancellationToken);
        foreach (var key in keys) loaded[key] = [];
        foreach (var brand in brands)
            if (loaded.TryGetValue(CatalogKeyNormalizer.Key(brand.NormalizedName, MaximumNameLength), out var list)) list.Add(brand);
    }

    public async Task<Match> FindAsync(string? name, CancellationToken cancellationToken)
    {
        var key = Key(name);
        if (key.Length == 0) return default;
        if (!loaded.ContainsKey(key)) await PreloadAsync([name], cancellationToken);
        // A brand opened earlier in this unit of work and not saved yet is in the change tracker only.
        var candidates = loaded[key]
            .Concat(db.Brands.Local.Where(x => x.TenantId == tenantId && db.Entry(x).State != EntityState.Deleted))
            .DistinctBy(x => x.Id);
        var brand = Pick(candidates, key, x => x.NormalizedName, x => x.IsActive, x => x.Id);
        return brand is null ? default : brand.IsActive ? new Match(brand, null) : new Match(null, brand);
    }

    public async Task<Match> FindOrCreateAsync(string? name, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var found = await FindAsync(name, cancellationToken);
        var key = Key(name);
        if (key.Length == 0 || found.Brand is not null || found.SwitchedOff is not null) return found;
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = PanelName(name), NormalizedName = key, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Brands.Add(brand);
        loaded[key].Add(brand);
        return new Match(brand, null, Created: true);
    }
}
