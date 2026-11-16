using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3d step 1 (16 Nov 2026): the catalog attribute machinery the product import and the catalog structure
// import share, moved out of the job processor as it was: the reference snapshots a read needs (read through the
// processor's reference read, passed in as syncReferences so the service does not call back into the processor), the
// category attribute contexts, the mapped categories, attributes and values, the web colour value mappings, the product
// attribute assignments, and the option-key and catalog-key helpers. Mappings stay the user's: the machinery uses the
// mappings the user made and the marketplace's own lists; it looks no brand or category up by name.
internal sealed partial class CatalogAttributeService(
    AppDbContext db,
    TimeProvider timeProvider,
    Func<Guid, Guid, string, string, CancellationToken, Task<bool>> syncReferences,
    JobRunContext run)
{
    public JobRunContext Run => run;

    // The processor's reference read (SyncReferences), handed in by the processor.
    private Task<bool> SyncReferences(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken) =>
        syncReferences(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

    internal static bool IsWebColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace("-", "", StringComparison.Ordinal);
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }

    internal static string? VariantOptionAxis(string value)
    {
        if (IsColorOptionKey(value)) return "COLOR";
        if (IsSizeOptionKey(value)) return "SIZE";
        return null;
    }

    internal static string? ObservedVariantValueAxis(string value) => VariantOptionAxis(value);

    internal static bool IsColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "RENK" or "COLOR" or "COLOUR";
    }

    internal static bool IsRealColorOptionKey(string value) => IsColorOptionKey(value) && !IsWebColorOptionKey(value);

    internal static bool IsSizeOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "BEDEN" or "SIZE" or "SIZ" or "NUMARA" or "NUMBER";
    }

    internal static string CategoryLeafName(string? name, string? path)
    {
        var source = string.IsNullOrWhiteSpace(path) ? name : path;
        var segments = source?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return Short(segments.Length > 0 ? segments[^1] : name, 160);
    }

    internal static string Short(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];

    // The shared catalog key (CatalogKeyNormalizer); the panel uses the same one, so SKU, barcode, option,
    // brand and category keys written here and there agree.
    internal static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);

    internal static string[] CatalogLookupKeys(string? value, int maximum = CatalogKeyNormalizer.DefaultMaximum) => CatalogKeyNormalizer.LookupKeys(value, maximum);

    internal static bool IsVariantOptionName(string value) => VariantOptionAxis(value) is not null;

    internal static bool IsCatalogProductOption(LocalCategoryAttribute mapped, string remoteName) =>
        mapped.Role == "OPTION" || IsVariantOptionName(remoteName);

    internal static bool TryGetMappedAttribute(IReadOnlyDictionary<string, LocalCategoryAttribute> attributes, string remoteName, out LocalCategoryAttribute mapped)
    {
        if (attributes.TryGetValue(NormalizeCatalogKey(remoteName, 320), out var direct)
            && (IsWebColorOptionKey(remoteName) || !IsWebColorOptionKey(direct.Remote.Name)))
        {
            mapped = direct;
            return true;
        }

        var axis = VariantOptionAxis(remoteName);
        if (axis is null)
        {
            mapped = null!;
            return false;
        }

        var wantsWebColor = IsWebColorOptionKey(remoteName);
        mapped = attributes.Values
            .Where(item => VariantOptionAxis(item.Remote.Name) == axis || VariantOptionAxis(item.Definition.Name) == axis)
            .OrderByDescending(item => IsWebColorOptionKey(item.Remote.Name) == wantsWebColor)
            .ThenByDescending(item => item.Role == "OPTION")
            .FirstOrDefault()!;
        // If the feed contains the real Renk field but only Web Color is
        // mapped locally, do not silently replace the real value with the
        // marketplace presentation value. The caller can preserve the raw
        // Renk value instead.
        if (mapped is not null && !wantsWebColor && IsWebColorOptionKey(mapped.Remote.Name))
        {
            mapped = null!;
            return false;
        }
        return mapped is not null;
    }

    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        new IssueRecorder(db).RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);
}
