using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3d (16 Nov 2026): the product writes — PRODUCT_CREATE, PRODUCT_UPDATE (Trendyol's content and
// update groups, PTT AVM's own update), PRODUCT_ARCHIVE (Pazarama's sale-status switch) and PRODUCT_APPROVAL_RECONCILE —
// with the publication-side link writes (ProductLinks), moved out of the job processor whole: each of its partial files
// became a part of this class unchanged but for its class line. Every send opens, sends and closes its ExternalEffect
// record through the processor's ExternalEffectGuard and checks the same write switches (JobWriteSwitches). It works for
// one run of one job (run) on the job's AppDbContext.
internal sealed partial class ProductJobService(
    AppDbContext db,
    IssueRecorder issues,
    IProductPort products,
    IReferenceDataPort references,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ExternalEffectGuard effectGuard,
    JobWriteSwitches writeSwitches,
    OrderIngestionService orderIngestion,
    VariantLinkBookkeeping variantLinks,
    JobRunContext run)
{
    // The payload deadline is the authoritative approval bound. The worker currently
    // applies exponential backoff, but this ceiling also keeps retry accounting from
    // becoming the earlier bound if the polling schedule is made more frequent later.
    private const int ProductApprovalReconcileMaxAttempts = (7 * 24 * 12) + 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static string Hash(string value) => TextValues.Sha256Hex(value);

    private static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);

    private static string AmbiguousCreateSummary(AdapterError error) => PriceStockService.AmbiguousCreateSummary(error);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task<bool> ExternalIdTaken(Guid tenantId, Guid connectionId, string externalId, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        variantLinks.ExternalIdTaken(tenantId, connectionId, externalId, exceptLinkId, cancellationToken);

    private Task<bool> ExternalWriteMasterAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        writeSwitches.ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> PttAvmManualWritesAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        writeSwitches.PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> HasHepsiburadaPublicationEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        writeSwitches.HasHepsiburadaPublicationEvidenceAsync(tenantId, connectionId, cancellationToken);
}
