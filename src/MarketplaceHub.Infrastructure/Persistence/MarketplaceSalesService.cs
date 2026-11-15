using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    // A real Trendyol package supersedes the zero-ID payment placeholder, and a
    // package another package names as its origin (a split UnPacked parent, a
    // resent original) is superseded by it. Retain their history without
    // displaying, counting or invoicing them as live packages.
    internal IQueryable<ShipmentPackage> CurrentShipmentPackages() => CurrentShipmentPackages(db);

    // Also used by the dashboard and invoicing, so their package sets follow the same rule as the order screens.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => CurrentShipmentPackages(db, db.ShipmentPackages.AsNoTracking());

    // The same rule over a caller's source, e.g. tracked packages that the invoicing precheck updates.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db, IQueryable<ShipmentPackage> packages) => packages
        .Where(package => package.RawStatus != ShipmentPackageSupersession.LocallyUnpackedRawStatus)
        .Where(package => (package.ExternalPackageId != "0"
            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                && connection.Id == package.ConnectionId && connection.PlatformCode == "TRENDYOL")
            || !db.ShipmentPackages.Any(real => real.TenantId == package.TenantId
                && real.ConnectionId == package.ConnectionId && real.OrderId == package.OrderId
                && real.ExternalPackageId != "0" && real.ExternalPackageId != ""))
            && !db.ShipmentPackages.Any(child => child.TenantId == package.TenantId
                && child.ConnectionId == package.ConnectionId && child.OrderId == package.OrderId
                && child.OriginExternalPackageId == package.ExternalPackageId
                && child.ExternalPackageId != package.ExternalPackageId));

    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "STAGE", cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "PRODUCTION", cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { using var document = JsonDocument.Parse(settings); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => (await ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken)).Enabled;
    private async Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? (false, 0) : (policy.Enabled, Math.Clamp(policy.IntervalSeconds, 0, 86_400));
    }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    internal static string InvoiceLabel(Invoice? invoice, string customerJson, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice, customerJson, [], marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // The local fiscal invoice and the marketplace delivery are separate
        // facts. A fiscal invoice number does not prove that Trendyol accepted
        // the document, so Submitted/Accepted/MarketplacePending stay visible
        // as an in-progress marketplace delivery.
        if (invoiceStatus is { } status)
        {
            // A lost provider answer is not a rejection: the invoice may exist at the provider.
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview) return "FATURA_REDDEDILDI";
            if (status == InvoiceStatus.MarketplaceFailed) return "FATURA_PLATFORMA_AKTARILMADI";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted) return "FATURA_ISLENIYOR";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (status == InvoiceStatus.Completed)
                return "FATURA_KONTROLDE";
            if (status is InvoiceStatus.Submitting or InvoiceStatus.Submitted)
                return "FATURA_ISLENIYOR";
            if (!string.IsNullOrWhiteSpace(invoiceNumber) && status is not (InvoiceStatus.Draft or InvoiceStatus.Validating or InvoiceStatus.Ready))
                return "FATURA_KONTROLDE";
            return "FATURA_ISLENIYOR";
        }
        // Trendyol's order payloads expose the same business fact in two
        // different places depending on the endpoint/version: invoiceStatus
        // in the package snapshot, or the package's raw status as INVOICED.
        // RawStatus must be considered before the snapshot fallback because a
        // later shipment update can replace the snapshot's invoice fields.
        if (packageRawStatuses.Any(IsInvoicedRemoteStatus))
            return "FATURA_KONTROLDE";

        var remote = JsonText(customerJson, "marketplaceInvoiceStatus", "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KONTROLDE";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED" or "NOT_INVOICED") return "FATURA_BEKLIYOR";
        // Missing marketplace evidence is not the same as an explicit
        // NotInvoiced response. Historical Trendyol payloads often omit the
        // invoice field entirely; presenting those packages as actionable
        // invoice work creates false Dashboard counts.
        return "FATURA_BILINMIYOR";
    }

    internal static string InvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        if (invoiceStatus is { } status)
        {
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed)
                return status == InvoiceStatus.MarketplaceFailed ? "FATURA_PLATFORMA_AKTARILMADI" : "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal)
                return "FATURA_IPTAL";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted or InvoiceStatus.Submitting)
                return "FATURA_ISLENIYOR";
            // The marketplace observation is authoritative for the delivery
            // leg. A local invoice number only proves that our fiscal provider
            // created a document; it does not prove Trendyol accepted it.
            if (status == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Rejected) return "FATURA_REDDEDILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Received) return "FATURA_KONTROLDE";
            return InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        }
        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.Invoiced => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Received => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Rejected => "FATURA_REDDEDILDI",
            MarketplaceInvoiceStatus.NotInvoiced => "FATURA_BEKLIYOR",
            _ => InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed)
        };
    }

    internal static string ReturnInvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // An absent marketplace observation does not prove that the package is
        // waiting for an invoice. Keep unknown separate so the return list does
        // not report a false pending state when Trendyol has not exposed it.
        return InvoiceLabel(invoice, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
    }

    // A status the seller set by hand on the invoice page ("Faturası yüklendi" / "Fatura bekliyor", for marketplaces
    // whose API cannot show an invoice uploaded in their own seller panel) decides before any other evidence.
    internal static string? ManualInvoiceLabel(string? manualInvoiceStatus) =>
        manualInvoiceStatus?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "FATURA_KONTROLDE",
            "PENDING" => "FATURA_BEKLIYOR",
            _ => null
        };

    // The hand-set status itself ("UPLOADED" / "PENDING"), so the screens can say the label was set by hand.
    internal static string? HandSetInvoiceStatus(string? manualInvoiceStatus) =>
        ManualInvoiceLabel(manualInvoiceStatus) is null ? null : manualInvoiceStatus!.Trim().ToUpperInvariant();

    // The orders filter and the row label read an issued invoice with these same two rules (P4, 1 Nov 2026):
    // - not on the platform: the marketplace leg failed, or it stopped with an error while waiting;
    // - needs review: the provider issued it (it has an external reference) and it stopped for review, for example
    //   because the marketplace already shows an invoice. That is not a refusal ("Reddedildi").
    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNotOnPlatform = invoice =>
        invoice.Status == InvoiceStatus.MarketplaceFailed
        || ((invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending) && invoice.LastErrorCode != null);

    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNeedsReview = invoice =>
        invoice.Status == InvoiceStatus.ManualReview && invoice.ExternalReference != null && invoice.ExternalReference != "";

    private static readonly Func<Invoice, bool> IsIssuedInvoiceNotOnPlatform = IssuedInvoiceNotOnPlatform.Compile();
    private static readonly Func<Invoice, bool> IsIssuedInvoiceNeedsReview = IssuedInvoiceNeedsReview.Compile();

    internal const string InvoiceNeedsReviewLabel = "FATURA_INCELEME_GEREKLI";

    internal static string? IssuedInvoiceLabel(Invoice invoice) =>
        IsIssuedInvoiceNeedsReview(invoice) ? InvoiceNeedsReviewLabel
        : IsIssuedInvoiceNotOnPlatform(invoice) ? "FATURA_PLATFORMA_AKTARILMADI"
        : null;

    // The one invoice label of a package: the orders list, the order detail and the invoice page read it the same way.
    internal static string PackageInvoiceLabel(ShipmentPackage? package, Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false) =>
        ManualInvoiceLabel(package?.ManualInvoiceStatus)
            ?? InvoiceLabelForPlatform(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    // The fiscal states the invoice workspace labels (pre-provider refusal, lost answer, adjustment) read the same here.
    internal static string InvoiceLabelForPlatform(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
        => invoice is not null && InvoicingBillingService.SharedFiscalLabel(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference) is { } shared
            ? shared
            : invoice is not null && IssuedInvoiceLabel(invoice) is { } issued
            ? issued
            : InvoiceLabelForPlatform(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabelForPlatform(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
    {
        var label = InvoiceLabel(invoiceStatus, invoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (invoiceStatus == InvoiceStatus.Draft) return "FATURA_BEKLIYOR";
            if (invoiceStatus == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            // Shopify holds only the link we write (ravencia.invoice_pdf_url): without a panel invoice and without
            // that link nothing says whether the order was invoiced elsewhere, so it is to be reviewed, not waiting.
            if (invoiceStatus is null && label is "FATURA_BILINMIYOR" or "FATURA_BEKLIYOR") return ShopifyUnconfirmedInvoiceLabel;
            if (label == "FATURA_BILINMIYOR") return "FATURA_BEKLIYOR";
        }
        // n11 gives no invoice state, only the link of an invoice that was attached to the order (empty until
        // one is); Pazarama gives nothing at all. A link means an invoice exists at the marketplace but no local
        // record matches it, so it is shown for review.
        else if (label == "FATURA_BILINMIYOR" && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(JsonText(customerJson, "invoiceLink")))
            return "FATURA_KONTROLDE";
        // n11, PTT AVM and Pazarama never show an invoice uploaded in their seller panel, so an unknown state there
        // cannot be checked from the marketplace: the panel's own record decides (owner, 24 Oct 2026 for n11 / PTT
        // AVM, 31 Oct 2026 for Pazarama). No panel invoice is waiting for an invoice; an invoice the panel issued and
        // delivered marks the package invoiced (RecordConfirmedDelivery) and reads as issued.
        else if (label == "FATURA_BILINMIYOR" && IsUnverifiableInvoicePlatform(platformCode))
            return UnverifiedInvoiceLabel;
        return label;
    }

    internal const string UnverifiedInvoiceLabel = "FATURA_BEKLIYOR";

    // "İncelenecek": a Shopify order with no panel invoice and no invoice link of ours. It is still invoice work: it
    // counts and is due exactly as "Fatura Bekliyor" does (IsAwaitingInvoiceLabel).
    internal const string ShopifyUnconfirmedInvoiceLabel = "FATURA_INCELENECEK";

    internal static bool IsAwaitingInvoiceLabel(string? label) => label is "FATURA_BEKLIYOR" or ShopifyUnconfirmedInvoiceLabel;

    internal static readonly string[] UnverifiableInvoicePlatforms = ["N11", "PTTAVM", "PAZARAMA"];

    internal static bool IsUnverifiableInvoicePlatform(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "N11" or "PTTAVM" or "PAZARAMA";

    internal static ShipmentPackage? SelectDisplayPackage(IEnumerable<ShipmentPackage> packages, string? platformCode)
    {
        var ordered = packages.OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.UpdatedAt).ToList();
        // The package an order card shows is a live one; a cancelled split package only when
        // nothing else is left.
        if (!string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return ordered.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled) ?? ordered.FirstOrDefault();

        var visible = ordered.Where(x => !(x.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            && x.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase))).ToList();
        return visible.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber))
            ?? visible.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled)
            ?? visible.FirstOrDefault();
    }

    private static bool IsInvoicedRemoteStatus(string? status) =>
        string.Equals(status?.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase);

    private static string? InvoiceDocumentUrl(string customerJson)
    {
        return ValidInvoiceDocumentUrl(JsonText(customerJson, "invoiceLink"));
    }

    private static string? ValidInvoiceDocumentUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId), x.UnreadableLineCount, HandSetInvoiceStatus(x.ManualInvoiceStatus), x.CargoBarcode);
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private const string VerifyingCode = "EXTERNAL_EFFECT_VERIFYING";
    private const string VerifyingMessage = "Pazaryerinin yanıtı kesinleşmedi; sonuç birkaç saniye içinde pazaryerinden okunup panel güncellenecek. İşlemi yeniden göndermeyin.";

    private static ServiceResult<T> Verifying<T>() => ServiceResult<T>.Fail(VerifyingCode, VerifyingMessage, 409);

    // The marketplace's answer was unclear: the action is not sent again; a verification job
    // reads the marketplace and settles the effect (see MarketplaceJobProcessor.ReconcileExternalEffect).
    private async Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken))
            db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now + ExternalEffectReconcilePolicy.FirstCheckDelay));
        await db.SaveChangesAsync(cancellationToken);
        return Verifying<T>();
    }

    private static async Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue)
    {
        var queued = await enqueue;
        return queued.Error is { } error
            ? ServiceResult<T>.Fail(error.Code, error.Message, error.Status, error.FieldErrors)
            : ServiceResult<T>.Fail("ACTION_QUEUED", "Pazaryeri istek kotası şu an dolu; işlem sıraya alındı ve birkaç saniye içinde gönderilecek.", 409);
    }

    private static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", "Kayıt siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 412);
}
