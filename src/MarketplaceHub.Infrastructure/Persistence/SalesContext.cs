using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: what the sales services share: the database, the marketplace ports, the clock and the helpers every
// one of them used as a private member of MarketplaceSalesService (connection checks, the verify-later answer, the
// current packages, the catalog pictures). One per request scope, built by the facade from its own dependencies.
internal sealed class SalesContext(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider)
{
    public AppDbContext Db => db;
    public CursorCodec Cursors => cursors;
    public IConfiguration Configuration => configuration;
    public IProductVisualLookupPort ProductVisuals => productVisuals;
    public IOrderPort Orders => orders;
    public IReturnPort Returns => returns;
    public IPrivateFileStorage Files => files;
    public TimeProvider TimeProvider => timeProvider;

    // A real Trendyol package supersedes the zero-ID payment placeholder, and a package another package names as its origin
    // (a split UnPacked parent, a resent original) is superseded by it (MarketplaceSalesService.CurrentShipmentPackages).
    public IQueryable<ShipmentPackage> CurrentShipmentPackages() => MarketplaceSalesService.CurrentShipmentPackages(db);

    public Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "STAGE", cancellationToken);
    public Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "PRODUCTION", cancellationToken);
    public async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); return ConnectionSettings.ExternalWritesEnabled(settings); }
    public async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => (await ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken)).Enabled;
    public async Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? (false, 0) : (policy.Enabled, Math.Clamp(policy.IntervalSeconds, 0, 86_400));
    }
    public async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }

    // The marketplace's answer was unclear: the action is not sent again; a verification job reads the marketplace and
    // settles the effect (see MarketplaceJobProcessor.ReconcileExternalEffect).
    public async Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken))
            db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now + ExternalEffectReconcilePolicy.FirstCheckDelay));
        await db.SaveChangesAsync(cancellationToken);
        return SalesResults.Verifying<T>();
    }

    public Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) =>
        OrderLineCatalog.MediaUrlsAsync(db, tenantId, variantIds.Where(x => x is not null).Select(x => x!.Value), cancellationToken);
}

// SIPARIS-STOK-10: the answers and wire names the sales services share.
internal static class SalesResults
{
    private const string VerifyingCode = "EXTERNAL_EFFECT_VERIFYING";
    private const string VerifyingMessage = "Pazaryerinin yanıtı kesinleşmedi; sonuç birkaç saniye içinde pazaryerinden okunup panel güncellenecek. İşlemi yeniden göndermeyin.";

    public static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();

    public static ServiceResult<T> Verifying<T>() => ServiceResult<T>.Fail(VerifyingCode, VerifyingMessage, 409);

    public static async Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue)
    {
        var queued = await enqueue;
        return queued.Error is { } error
            ? ServiceResult<T>.Fail(error.Code, error.Message, error.Status, error.FieldErrors)
            : ServiceResult<T>.Fail("ACTION_QUEUED", "Pazaryeri istek kotası şu an dolu; işlem sıraya alındı ve birkaç saniye içinde gönderilecek.", 409);
    }

    public static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);
    public static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    public static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    public static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", "Kayıt siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 412);
    public static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId), x.UnreadableLineCount, InvoiceLabelPolicy.HandSetInvoiceStatus(x.ManualInvoiceStatus), x.CargoBarcode);
}
