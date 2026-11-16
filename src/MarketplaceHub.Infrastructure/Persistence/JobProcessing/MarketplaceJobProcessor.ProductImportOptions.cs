using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private static string NormalizeCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(character => character is >= 'A' and <= 'Z') ? currency : "TRY";
    }

    private async Task<string> PanelOptionSignatureAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var hasRealColorSource = options.Keys.Any(IsRealColorOptionKey);
        var candidates = new List<(string PanelLabel, bool IsWebColorSource, LocalCategoryAttribute? Mapped, string RemoteValue, int Order)>();
        var order = 0;
        foreach (var pair in options)
        {
            // Web Color is a category attribute derived from the real Renk
            // slicer. It must never create a second product option axis.
            if (IsWebColorOptionKey(pair.Key))
            {
                order++;
                continue;
            }
            if (TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped) && IsCatalogProductOption(mapped, pair.Key))
            {
                var panelLabel = mapped.Definition.Name;
                var isWebColorSource = IsWebColorOptionKey(pair.Key) || IsWebColorOptionKey(mapped.Remote.Name);
                if (isWebColorSource && hasRealColorSource)
                {
                    order++;
                    continue;
                }
                candidates.Add((panelLabel, isWebColorSource, mapped, pair.Value, order));
            }
            else if (IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key))
            {
                // A missing Renk mapping must not make the importer fall back
                // to Web Color. Preserve the real slicer value under the
                // canonical local option name until it is mapped explicitly.
                candidates.Add((IsColorOptionKey(pair.Key) ? "Renk" : pair.Key, false, null, pair.Value, order));
            }
            order++;
        }

        var panelOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(candidate => NormalizeCatalogKey(candidate.PanelLabel, 160), StringComparer.Ordinal))
        {
            // Trendyol can expose both the slicer value (Renk: Tavşanlı) and
            // the marketplace display value (Web Color: Çok Renkli). They
            // map to the same panel option, so the real Renk value must win
            // regardless of the source dictionary order.
            var selected = group
                .OrderBy(candidate => candidate.IsWebColorSource ? 1 : 0)
                .ThenBy(candidate => candidate.Order)
                .First();
            // Product option names must reflect the actual slicer color. The
            // mapped category color can be a broader display value (for
            // example, Renk=İndigo alongside the category value Renk=Lacivert).
            panelOptions[selected.PanelLabel] = IsRealColorOptionKey(selected.PanelLabel) || selected.Mapped is null
                ? CleanCatalogOptionValue(selected.RemoteValue)
                : await PanelOptionValueAsync(tenantId, connectionId, categoryContext, selected.Mapped, selected.RemoteValue, cancellationToken);
        }
        if (panelOptions.Count > 0) return OptionSignature(panelOptions);
        var fallbackOptions = options.Where(pair => IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return OptionSignature(fallbackOptions);
    }

    // ISLEYICI-9 3d step 1: the option-key helpers live in CatalogAttributeService (shared with the catalog structure).
    private static bool IsWebColorOptionKey(string value) => CatalogAttributeService.IsWebColorOptionKey(value);

    private static string? VariantOptionAxis(string value) => CatalogAttributeService.VariantOptionAxis(value);

    private static string? ObservedVariantValueAxis(string value) => CatalogAttributeService.ObservedVariantValueAxis(value);

    private static bool IsColorOptionKey(string value) => CatalogAttributeService.IsColorOptionKey(value);

    private static bool IsRealColorOptionKey(string value) => CatalogAttributeService.IsRealColorOptionKey(value);

    // True when the local option signature carries the same colour and size values as the remote options.
    // It reads both stored forms ("Renk: Mavi | Beden: XL" and the old panel "RENK=MAVI|BEDEN=XL"). Remote
    // options without a colour or size cannot narrow a model-code match.
    // Every value the marketplace names ("S"; "Lacivert / L") is one of the panel variant's option values.
    internal static bool HasOptionValues(string? localSignature, IReadOnlyDictionary<string, string> remoteOptions)
    {
        if (string.IsNullOrWhiteSpace(localSignature)) return false;
        var local = VariantOptionSignature.ParseKeyed(localSignature).Values.ToHashSet(StringComparer.Ordinal);
        var values = remoteOptions.Values
            .SelectMany(value => value.Split(['/', ',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(value => NormalizeCatalogKey(CleanCatalogOptionValue(value), 120))
            .Where(value => value.Length > 0)
            .ToList();
        return values.Count > 0 && values.All(local.Contains);
    }

    // The panel variant's colour appears in the title as whole words ("Bordo" in "Bordo Kaşe Mini Etek").
    internal static bool TitleNamesColor(string? title, string? localSignature)
    {
        var color = VariantOptionSignature.ParseKeyed(localSignature).FirstOrDefault(option => VariantOptionSignature.IsColorName(option.Key)).Value;
        var titleKey = NormalizeCatalogKey(title, 320);
        return !string.IsNullOrEmpty(color) && titleKey.Length > 0 && $"-{titleKey}-".Contains($"-{color}-", StringComparison.Ordinal);
    }

    // The panel option of the same name, else the one on the same axis ("Color" on the marketplace, "Renk" in the
    // panel); values compare by the shared catalog key, so case and Turkish letters do not matter.
    internal static bool SameColorAndSize(string? localSignature, IReadOnlyDictionary<string, string> remoteOptions)
    {
        var axes = remoteOptions
            .Where(option => IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key))
            .ToList();
        if (axes.Count == 0 || string.IsNullOrWhiteSpace(localSignature)) return false;
        var local = VariantOptionSignature.ParseKeyed(localSignature);
        return axes.All(option =>
        {
            var expected = NormalizeCatalogKey(CleanCatalogOptionValue(option.Value), 120);
            if (local.TryGetValue(NormalizeCatalogKey(option.Key, 80), out var value)) return string.Equals(value, expected, StringComparison.Ordinal);
            var isColor = IsRealColorOptionKey(option.Key);
            var sameAxis = local.Where(pair => isColor ? IsRealColorOptionKey(pair.Key) : IsSizeOptionKey(pair.Key)).Select(pair => pair.Value).ToList();
            return sameAxis.Count == 1 && string.Equals(sameAxis[0], expected, StringComparison.Ordinal);
        });
    }

    // The model-code candidates a remote variant may be bound to (Y5). A model code names every colour and size of a
    // model, so a candidate is kept only when its colour and size are the remote variant's; options without a colour or
    // size name (PTT AVM's "S", "Lacivert / L") are matched by their values, and a colour the title names ("Bordo Kaşe
    // Mini Etek") picks among the colours of the model. A remote variant that names no colour or size keeps the model's
    // one panel variant only when nothing can contradict it: the remote names no option at all, or the panel variant has
    // no colour or size either. Anything else returns the undecided candidates (or none): the caller never picks one.
    internal static List<T> NarrowModelCodeCandidates<T>(IReadOnlyList<T> candidates, Func<T, string?> signature, IReadOnlyDictionary<string, string> remoteOptions, string? title)
    {
        if (candidates.Count == 0) return [];
        var narrowed = candidates.Where(candidate => SameColorAndSize(signature(candidate), remoteOptions)).ToList();
        if (narrowed.Count == 1) return narrowed;
        var byValues = candidates.Where(candidate => HasOptionValues(signature(candidate), remoteOptions)).ToList();
        if (byValues.Count > 1) byValues = byValues.Where(candidate => TitleNamesColor(title, signature(candidate))).ToList();
        if (byValues.Count == 1) return byValues;
        var remoteNamesAxis = remoteOptions.Any(option => (IsRealColorOptionKey(option.Key) || IsSizeOptionKey(option.Key)) && !string.IsNullOrWhiteSpace(CleanCatalogOptionValue(option.Value)));
        if (!remoteNamesAxis && candidates.Count == 1)
        {
            var namesNoOption = remoteOptions.All(option => string.IsNullOrWhiteSpace(CleanCatalogOptionValue(option.Value)));
            var localNamesAxis = VariantOptionSignature.ParseKeyed(signature(candidates[0])).Keys.Any(key => IsRealColorOptionKey(key) || IsSizeOptionKey(key));
            if (namesNoOption || !localNamesAxis) return [candidates[0]];
        }
        return narrowed.Count > 1 ? narrowed : byValues.Count > 1 ? byValues : !remoteNamesAxis && candidates.Count > 1 ? [.. candidates] : [];
    }

    internal sealed record ModelCodeVariant(Guid ProductId, string ModelCode, string? OptionSignature);

    // The panel products a listing's variants land on by model code (Hepsiburada import, Y5). Each remote variant is
    // narrowed among its model's panel variants by colour and size (NarrowModelCodeCandidates); it counts for a product
    // when the variants left all belong to that one product (a listing of one colour without sizes, say). Several
    // products returned are several candidates: the caller links none of them.
    internal static List<Guid> ModelCodeProducts(IReadOnlyList<ModelCodeVariant> modelVariants, IEnumerable<RemoteCatalogVariant> remoteVariants, string? title)
    {
        var products = new List<Guid>();
        foreach (var remote in remoteVariants)
        {
            var code = remote.ModelCode?.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;
            var candidates = modelVariants.Where(variant => string.Equals(variant.ModelCode, code, StringComparison.Ordinal)).ToList();
            var narrowed = NarrowModelCodeCandidates(candidates, variant => variant.OptionSignature, remote.Options, title);
            var narrowedProducts = narrowed.Select(variant => variant.ProductId).Distinct().ToList();
            if (narrowedProducts.Count == 1 && !products.Contains(narrowedProducts[0])) products.Add(narrowedProducts[0]);
        }
        return products;
    }

    private static bool IsVariantOptionName(string value) => CatalogAttributeService.IsVariantOptionName(value);

    private static bool IsCatalogProductOption(LocalCategoryAttribute mapped, string remoteName) => CatalogAttributeService.IsCatalogProductOption(mapped, remoteName);

    private static bool TryGetMappedAttribute(IReadOnlyDictionary<string, LocalCategoryAttribute> attributes, string remoteName, out LocalCategoryAttribute mapped) =>
        CatalogAttributeService.TryGetMappedAttribute(attributes, remoteName, out mapped);

    private static bool IsSizeOptionKey(string value) => CatalogAttributeService.IsSizeOptionKey(value);

    private async Task<string> PanelOptionValueAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, string remoteValue, CancellationToken cancellationToken)
    {
        var cleanedRemoteValue = CleanCatalogOptionValue(remoteValue);
        var remote = mapped.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(cleanedRemoteValue, 320));
        var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remote, cleanedRemoteValue, cancellationToken);
        return localValue?.Value ?? cleanedRemoteValue;
    }

    private async Task RepairMissingCatalogOptionAssignments(Guid tenantId, Guid connectionId, Guid productId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken)
    {
        var colorVariants = snapshot.Variants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId) && variant.Options.Keys.Any(IsRealColorOptionKey))
            .ToList();
        if (colorVariants.Count == 0) return;
        var colorName = colorVariants.SelectMany(variant => variant.Options.Keys)
            .First(IsRealColorOptionKey);
        var colorLabel = categoryContext is not null && TryGetMappedAttribute(categoryContext.Attributes, colorName, out var mappedColor)
            ? mappedColor.Definition.Name
            : "Renk";
        var colorKey = NormalizeCatalogKey(colorLabel, 160);
        var colorOption = await db.ProductOptions.AsNoTracking()
            .SingleOrDefaultAsync(option => option.TenantId == tenantId && option.ProductId == productId && option.NormalizedKey == colorKey, cancellationToken);
        var externalIds = colorVariants.Select(variant => Short(variant.ExternalVariantId, 256)).ToArray();
        var linkedVariants = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                                    join variant in db.ProductVariants.AsNoTracking()
                                        on new { link.TenantId, VariantId = link.VariantId }
                                        equals new { variant.TenantId, VariantId = variant.Id }
                                    where link.TenantId == tenantId
                                        && link.ConnectionId == connectionId
                                        && externalIds.Contains(link.ExternalId)
                                        && variant.ProductId == productId
                                    select new { link.ExternalId, VariantId = variant.Id }).ToListAsync(cancellationToken);
        var assignedVariantIds = colorOption is null
            ? new HashSet<Guid>()
            : (await db.VariantOptionValues.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId && assignment.OptionId == colorOption.Id)
                .Select(assignment => assignment.VariantId)
                .ToListAsync(cancellationToken)).ToHashSet();
        var linksByExternalId = linkedVariants.ToDictionary(link => MarketplaceVariantLinkCoverage.Normalize(link.ExternalId), link => link.VariantId, StringComparer.Ordinal);
        foreach (var remote in colorVariants)
        {
            var externalId = MarketplaceVariantLinkCoverage.Normalize(Short(remote.ExternalVariantId, 256));
            if (!linksByExternalId.TryGetValue(externalId, out var variantId) || assignedVariantIds.Contains(variantId)) continue;
            await UpsertCatalogOptions(tenantId, connectionId, productId, variantId, remote.Options, categoryContext, cancellationToken, onlyFillMissingAssignments: true);
        }
    }
    private async Task UpsertCatalogOptions(Guid tenantId, Guid connectionId, Guid productId, Guid variantId, IReadOnlyDictionary<string, string> options, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken, bool onlyFillMissingAssignments = false)
    {
        var order = 0;
        var processedPanelOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.OrderBy(x => IsWebColorOptionKey(x.Key) ? 1 : 0).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsWebColorOptionKey(pair.Key)) continue;
            var optionKey = NormalizeCatalogKey(pair.Key, 160); var valueKey = NormalizeCatalogKey(pair.Value, 160);
            if (string.IsNullOrWhiteSpace(optionKey) || string.IsNullOrWhiteSpace(valueKey)) continue;
            LocalCategoryAttribute? mapped = null;
            if (categoryContext is not null && (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out mapped) || (mapped.Role != "OPTION" && !IsVariantOptionName(pair.Key)))) continue;
            var panelLabel = mapped?.Definition.Name ?? pair.Key;
            var panelValue = mapped is null || IsRealColorOptionKey(pair.Key) || IsRealColorOptionKey(mapped.Definition.Name)
                ? CleanCatalogOptionValue(pair.Value)
                : await PanelOptionValueAsync(tenantId, connectionId, categoryContext!, mapped, pair.Value, cancellationToken);
            optionKey = NormalizeCatalogKey(panelLabel, 160);
            if (categoryContext is not null && !processedPanelOptions.Add(optionKey)) continue;
            valueKey = NormalizeCatalogKey(panelValue, 160);
            // Panel-created options may still hold the legacy key form ("BEDEN/YAŞ" for "Beden/Yaş"); match
            // both so the import reuses them instead of adding a second option group.
            var optionKeys = CatalogLookupKeys(panelLabel);
            var valueKeys = CatalogLookupKeys(panelValue);
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && CatalogKeyNormalizer.SameKey(x.NormalizedKey, optionKey))
                ?? await db.ProductOptions.Where(x => x.TenantId == tenantId && x.ProductId == productId && optionKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == optionKey ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (option is null) { option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = Short(panelLabel, 160), NormalizedKey = optionKey, SortOrder = order }; db.ProductOptions.Add(option); }
            var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && CatalogKeyNormalizer.SameKey(x.NormalizedKey, valueKey))
                ?? await db.ProductOptionValues.Where(x => x.TenantId == tenantId && x.OptionId == option.Id && valueKeys.Contains(x.NormalizedKey)).OrderBy(x => x.NormalizedKey == valueKey ? 0 : 1).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (optionValue is null) { optionValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, OptionId = option.Id, Label = Short(panelValue, 160), NormalizedKey = valueKey, SortOrder = order }; db.ProductOptionValues.Add(optionValue); }
            var assignment = db.VariantOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id)
                ?? await db.VariantOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id, cancellationToken);
            if (assignment is null) db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variantId, OptionId = option.Id, OptionValueId = optionValue.Id });
            else if (!onlyFillMissingAssignments) assignment.OptionValueId = optionValue.Id;
            order++;
        }
    }

    // K3: media the user manages is never replaced by an import: a custom gallery order (ORDERED_* roles),
    // an uploaded file in the scope, or a media edit recorded on a marketplace link of the product.
    private async Task<bool> MediaIsUserManagedAsync(Guid tenantId, Guid productId, Guid? variantId, CancellationToken cancellationToken)
    {
        if (await db.ProductMedia.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.Status == "ACTIVE" && x.MediaRole.StartsWith("ORDERED_"), cancellationToken)) return true;
        if (await (from media in db.ProductMedia.AsNoTracking()
                   join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                   where media.TenantId == tenantId && media.ProductId == productId && media.VariantId == variantId && media.Status == "ACTIVE" && asset.Classification == "PRODUCT_MEDIA"
                   select media.Id).AnyAsync(cancellationToken)) return true;
        var dirtyFields = db.MarketplaceProductLinks.Local.Where(x => x.TenantId == tenantId && x.ProductId == productId).Select(x => x.DirtyFieldsJson).ToList();
        dirtyFields.AddRange(await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == productId && x.DirtyFieldsJson != null).Select(x => x.DirtyFieldsJson).ToListAsync(cancellationToken));
        return dirtyFields.Any(json => CatalogLocalEdits.HasDirtyField(json, CatalogLocalEdits.MediaField));
    }

    private async Task UpsertCatalogMedia(Guid tenantId, Product product, Guid? variantId, IReadOnlyList<string> sourceUrls, string altText, CancellationToken cancellationToken)
    {
        if (db.Entry(product).State != EntityState.Added && await MediaIsUserManagedAsync(tenantId, product.Id, variantId, cancellationToken)) return;
        if (variantId is Guid ownVariantId && db.Entry(product).State != EntityState.Added && await VariantPicturesAreOwnersAsync(tenantId, product.Id, ownVariantId, cancellationToken)) return;
        var urls = CatalogImageIdentity.DistinctUrls(sourceUrls);
        var existing = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variantId).ToListAsync(cancellationToken);
        var existingAssetIds = existing.Select(x => x.FileAssetId).Distinct().ToArray();
        var existingAssets = existingAssetIds.Length == 0
            ? []
            : await db.FileAssets
                .Where(x => x.TenantId == tenantId && existingAssetIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
        foreach (var row in existing.Where(x => x.SortOrder >= urls.Count)) row.Status = "ARCHIVED";
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            var asset = existingAssets.FirstOrDefault(x => x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? db.FileAssets.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? await db.FileAssets.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && x.RelativePath == url, cancellationToken);
            if (asset is null)
            {
                asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = Path.GetFileName(new Uri(url).AbsolutePath), MimeType = ImageMime(url), SizeBytes = 0, Sha256 = Hash(url), Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
                db.FileAssets.Add(asset);
            }
            else
            {
                if (!string.Equals(asset.RelativePath, url, StringComparison.Ordinal)) asset.RelativePath = url;
                asset.Sha256 = Hash(url);
                if (asset.Status != "ACTIVE" || asset.ArchivedAt is not null) { asset.Status = "ACTIVE"; asset.ArchivedAt = null; }
            }
            var media = existing.SingleOrDefault(x => x.SortOrder == index);
            if (media is null) db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variantId, FileAssetId = asset.Id, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, AltText = Short(altText, 320), Status = "ACTIVE" });
            else
            {
                var nextRole = index == 0 ? "PRIMARY" : "GALLERY";
                var nextAltText = Short(altText, 320);
                if (media.FileAssetId != asset.Id || media.MediaRole != nextRole || media.AltText != nextAltText || media.Status != "ACTIVE")
                {
                    media.FileAssetId = asset.Id; media.MediaRole = nextRole; media.AltText = nextAltText; media.Status = "ACTIVE";
                }
            }
        }
    }

    // A new card's VAT from the listing: the rate most of its variants carry (0-100), else null. A variant the
    // marketplace sent without a rate counts for nothing: unknown stays unknown.
    internal static decimal? ListingVatRate(RemoteCatalogProduct snapshot) => snapshot.Variants
        .Select(variant => variant.VatRate)
        .OfType<decimal>()
        .Where(rate => rate is >= 0 and <= 100)
        .Select(rate => decimal.Round(rate, 4, MidpointRounding.ToEven))
        .GroupBy(rate => rate)
        .OrderByDescending(group => group.Count())
        .Select(group => (decimal?)group.Key)
        .FirstOrDefault();

    private static string ProductTitle(string? title, string externalId) => Short(string.IsNullOrWhiteSpace(title) ? $"Pazar yeri ürünü {externalId}" : title.Trim(), 320);
    private static string CleanCatalogOptionValue(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Trim('"', '“', '”').Trim();
    private static string OptionSignature(IReadOnlyDictionary<string, string> options) => VariantOptionSignature.Build(options);

    private static string CategoryLeafName(string? name, string? path) => CatalogAttributeService.CategoryLeafName(name, path);

    internal static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);
    private static string NormalizeCatalogKey(string? value, int maximum) => CatalogAttributeService.NormalizeCatalogKey(value, maximum);
    private static string[] CatalogLookupKeys(string? value, int maximum = CatalogKeyNormalizer.DefaultMaximum) => CatalogAttributeService.CatalogLookupKeys(value, maximum);
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string ImageMime(string url) => new Uri(url).AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}
