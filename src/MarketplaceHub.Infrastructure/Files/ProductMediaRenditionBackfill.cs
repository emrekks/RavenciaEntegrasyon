using System.Diagnostics;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Files;

// Gives the product photos stored before thumbnails existed their small copies, once per worker start: it walks the
// active uploaded photos newest first, one at a time, and skips those that already have both copies, so a later
// start only checks file names. It never blocks a deploy (it starts after the worker is up) and a picture it
// cannot read is logged and left to the original.
public sealed class ProductMediaRenditionBackfill(IServiceScopeFactory scopes, ProductMediaRenditions renditions, IPrivateFileStorage storage, TimeProvider timeProvider, ILogger<ProductMediaRenditionBackfill> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    // A short breath between pictures keeps the backfill a background load on the shared host.
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    public sealed record Outcome(int Checked, int Generated, int Failed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, timeProvider, stoppingToken);
            var watch = Stopwatch.StartNew();
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), storage, renditions, Pause, timeProvider, stoppingToken);
            logger.LogInformation("Product media rendition backfill finished: {Checked} photos checked, {Generated} given thumbnails, {Failed} failed, in {ElapsedSeconds:0.0} s",
                outcome.Checked, outcome.Generated, outcome.Failed, watch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning(exception, "Product media rendition backfill stopped; the next worker start resumes it"); }
    }

    public static async Task<Outcome> RunAsync(AppDbContext db, IPrivateFileStorage storage, ProductMediaRenditions renditions, TimeSpan pause, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var assets = await db.FileAssets.AsNoTracking()
            .Where(x => x.Classification == "PRODUCT_MEDIA" && x.Status == "ACTIVE" && x.ArchivedAt == null && (x.MimeType == "image/jpeg" || x.MimeType == "image/png"))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.TenantId, x.RelativePath })
            .ToListAsync(cancellationToken);
        int generated = 0, failed = 0;
        foreach (var asset in assets)
        {
            if (renditions.HasAll(asset.TenantId, asset.Id)) continue;
            var made = await renditions.TryEnsureAsync(asset.TenantId, asset.Id, async token =>
            {
                await using var original = await storage.OpenReadAsync(asset.TenantId, asset.RelativePath, token);
                using var buffer = new MemoryStream();
                await original.CopyToAsync(buffer, token);
                return buffer.ToArray();
            }, cancellationToken);
            if (made) generated++; else failed++;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, timeProvider, cancellationToken);
        }
        return new(assets.Count, generated, failed);
    }
}
