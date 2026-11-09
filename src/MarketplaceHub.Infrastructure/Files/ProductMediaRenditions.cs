using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetVips;

namespace MarketplaceHub.Infrastructure.Files;

// Small WebP copies of an uploaded product photo for the panel's frames: 168x252 for the list, order, return and
// editor thumbnails (54-92 px wide, sharp up to 3x density) and 480x720 for the preview. The original stays as it
// was and is what marketplaces get (/public/product-media/). A copy lives beside its tenant's files as
// {tenant}/renditions/{asset}-w{width}.webp; a file asset never changes (a new upload is a new id), so the copy is
// written once and served as immutable. Copies are made at upload, by the worker's one-time backfill and, when one
// is still missing, on the first request that asks for it.
public sealed class ProductMediaRenditions
{
    public static readonly IReadOnlyList<int> Widths = [168, 480];
    // A 4-core host: at most two pictures are decoded at once, each on one libvips thread.
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly string _root;
    private readonly ILogger _logger;
    private static readonly Lazy<bool> Configured = new(Configure);

    public ProductMediaRenditions(string root, ILogger<ProductMediaRenditions>? logger = null)
    {
        _root = Path.GetFullPath(root);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // ?w=168 or ?w=480; anything else (absent, another number, text) asks for the original.
    public static int? RequestedWidth(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width) && Widths.Contains(width) ? width : null;

    public bool Exists(Guid tenantId, Guid assetId, int width) => File.Exists(FullPath(tenantId, assetId, width));

    public bool HasAll(Guid tenantId, Guid assetId) => Widths.All(width => Exists(tenantId, assetId, width));

    public Stream? TryOpen(Guid tenantId, Guid assetId, int width)
    {
        try { return new FileStream(FullPath(tenantId, assetId, width), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // Writes the missing copies from the original's bytes. False (logged) when the picture cannot be read or
    // rendered; the caller then serves the original. Cancellation still throws.
    public async Task<bool> TryEnsureAsync(Guid tenantId, Guid assetId, Func<CancellationToken, Task<byte[]>> readOriginal, CancellationToken cancellationToken)
    {
        if (HasAll(tenantId, assetId)) return true;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var missing = Widths.Where(width => !Exists(tenantId, assetId, width)).ToList();
            if (missing.Count == 0) return true;
            var source = await readOriginal(cancellationToken);
            foreach (var width in missing)
                await WriteAsync(FullPath(tenantId, assetId, width), Render(source, width), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Product media rendition failed for asset {AssetId}", assetId);
            return false;
        }
        finally { _gate.Release(); }
    }

    public void Delete(Guid tenantId, Guid assetId)
    {
        foreach (var width in Widths)
        {
            try { File.Delete(FullPath(tenantId, assetId, width)); }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Fits the picture inside width x 1.5*width keeping its aspect; a smaller picture keeps its own size.
    public static byte[] Render(byte[] source, int width)
    {
        _ = Configured.Value;
        using var image = Image.ThumbnailBuffer(source, width, height: width * 3 / 2, size: Enums.Size.Down, failOn: Enums.FailOn.Error);
        return image.WebpsaveBuffer(q: 80, effort: 4, keep: Enums.ForeignKeep.None);
    }

    private static bool Configure()
    {
        // No operation cache (every picture is different), one worker thread per picture, and only the
        // loaders libvips counts as safe for untrusted input.
        Cache.Max = 0;
        global::NetVips.NetVips.Concurrency = 1;
        global::NetVips.NetVips.BlockUntrusted = true;
        return true;
    }

    private static async Task WriteAsync(string target, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Written beside the target and moved in place, so a reader never sees half a file.
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string FullPath(Guid tenantId, Guid assetId, int width)
    {
        if (!Widths.Contains(width)) throw new ArgumentOutOfRangeException(nameof(width));
        return Path.Combine(_root, tenantId.ToString("N"), "renditions", $"{assetId:N}-w{width}.webp");
    }
}
