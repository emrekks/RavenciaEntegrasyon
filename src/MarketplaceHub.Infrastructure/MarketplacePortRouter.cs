using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure;

public sealed class MarketplacePortRouter(
    AppDbContext db,
    TrendyolHttpClient trendyol,
    ShopifyHttpClient shopify,
    HepsiburadaHttpClient hepsiburada,
    N11HttpClient n11,
    PazaramaHttpClient pazarama,
    PttAvmHttpClient pttAvm)
    : IConnectionPort, IReferenceDataPort, IProductPort, IHepsiburadaProductMatchPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IOrderPackageReadPort, IWebhookRegistrationPort, IReturnPort, IInvoiceMarketplacePort, IHepsiburadaInvoiceStatusPort, IQuestionPort, IOrderFinancePort
{
    // The same answer the adapters give when they cannot load the connection: a connection removed while its
    // job waited fails as an adapter result, not as an exception out of the router.
    internal static readonly AdapterError ConnectionMissing = new(AdapterErrorClass.Authentication, "CONNECTION_CONFIGURATION_UNAVAILABLE", "Bağlantı veya şifreli credential kullanılamıyor.", null, null, null);

    private async Task<AdapterResult<TResult>> Route<TPort, TResult>(AdapterContext context, Func<TPort, Task<AdapterResult<TResult>>> call, CancellationToken cancellationToken) where TPort : class
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == context.TenantId && x.Id == context.ConnectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (platform is null) return AdapterResult<TResult>.Failure(ConnectionMissing);
        // A known platform without this port is a wiring bug, which stays an exception.
        object port = SelectPort(platform, trendyol, shopify, hepsiburada, n11, pazarama, pttAvm);
        return port is TPort typed
            ? await call(typed)
            : throw new InvalidOperationException($"{platform} bağlantısı {typeof(TPort).Name} portunu sağlamıyor.");
    }

    internal static object SelectPort(string? platform, object trendyol, object shopify, object? hepsiburada = null, object? n11 = null, object? pazarama = null, object? pttAvm = null) => platform?.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => trendyol,
        "SHOPIFY" => shopify,
        "HEPSIBURADA" when hepsiburada is not null => hepsiburada,
        "N11" when n11 is not null => n11,
        "PAZARAMA" when pazarama is not null => pazarama,
        // PTT AVM serves the connection and order ports (phases 1–2); any other port is a wiring bug until its phase lands.
        "PTTAVM" when pttAvm is not null => pttAvm,
        _ => throw new InvalidOperationException($"{platform ?? "UNKNOWN"} platformu için adaptör yönlendirmesi tanımlı değil.")
    };

    public Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) => Route(context, (IConnectionPort port) => port.TestAsync(context, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) => Route(context, (IConnectionPort port) => port.DiscoverCapabilitiesAsync(context, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IReferenceDataPort port) => port.ReadAsync(context, resource, page, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.ListAsync(context, page, filter, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.ListCatalogAsync(context, page, filter, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteProductMatch>>> ListPendingProductMatchesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IHepsiburadaProductMatchPort port) => port.ListPendingProductMatchesAsync(context, page, cancellationToken), cancellationToken);
    public Task<AdapterResult<bool>> ReviewProductMatchesAsync(AdapterContext context, IReadOnlyList<string> merchantSkus, bool approve, CancellationToken cancellationToken) => Route(context, (IHepsiburadaProductMatchPort port) => port.ReviewProductMatchesAsync(context, merchantSkus, approve, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.CreateAsync(context, publication, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.UpdateUnapprovedAsync(context, publication, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.UpdateApprovedContentAsync(context, publication, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.UpdateApprovedVariantsAsync(context, publication, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.UpdateApprovedDeliveryAsync(context, publication, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.GetOperationAsync(context, externalOperationId, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.GetPublicationStatusAsync(context, barcode, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Route(context, (IProductPort port) => port.ArchiveAsync(context, payloadJson, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Route(context, (IProductVisualLookupPort port) => port.FindByBarcodeAsync(context, barcode, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Route(context, (IInventoryPricePort port) => port.PushPriceAndInventoryAsync(context, payloadJson, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.PollAsync(context, window, page, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetAsync(context, externalOrderId, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetShipmentPackageAsync(context, externalPackageId, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetShipmentPackageAsync(context, externalPackageId, packageStatusOccurredAt, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetShipmentPackageAsync(context, externalPackageId, lookup, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetAsync(context, externalOrderId, hint, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken) => Route(context, (IWebhookRegistrationPort port) => port.ListWebhooksAsync(context, cancellationToken), cancellationToken);
    public Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken) => Route(context, (IWebhookRegistrationPort port) => port.CreateWebhookAsync(context, url, username, password, cancellationToken), cancellationToken);
    public Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) => Route(context, (IWebhookRegistrationPort port) => port.RestoreMissingWebhooksAsync(context, externalId, cancellationToken), cancellationToken);
    public Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) => Route(context, (IWebhookRegistrationPort port) => port.DeleteWebhookAsync(context, externalId, cancellationToken), cancellationToken);
    public Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) => Route(context, (IWebhookRegistrationPort port) => port.ActivateWebhookAsync(context, externalId, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetShipmentPackagesAsync(context, externalPackageIds, hint, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IOrderPackageReadPort port) => port.PollPackagesAsync(context, window, page, cancellationToken), cancellationToken);
    public Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken) => Route(context, (IOrderPackageReadPort port) => port.GetPackageTrackingInfoAsync(context, packageNumber, cancellationToken), cancellationToken);
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.ExecutePackageActionAsync(context, command, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetChangeableCargoCompaniesAsync(context, externalPackageId, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.GetPackageableLineItemsAsync(context, externalLineItemId, cancellationToken), cancellationToken);
    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.CreateOrderPackageAsync(context, command, cancellationToken), cancellationToken);
    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.CreateCommonLabelAsync(context, request, cancellationToken), cancellationToken);
    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => Route(context, (IOrderPort port) => port.GetCommonLabelAsync(context, cargoTrackingNumber, cancellationToken, format), cancellationToken);
    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Route(context, (IOrderPort port) => port.CreateStageTestOrderAsync(context, barcode, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IReturnPort port) => port.PollAsync(context, window, page, cancellationToken), cancellationToken);
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => Route(context, (IReturnPort port) => port.GetAsync(context, externalReturnId, cancellationToken), cancellationToken);
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => Route(context, (IReturnPort port) => port.IssueReasonsAsync(context, cancellationToken), cancellationToken);
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => Route(context, (IReturnPort port) => port.ExecuteAsync(context, command, cancellationToken), cancellationToken);
    public Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken) => Route(context, (IQuestionPort port) => port.ListQuestionsAsync(context, request, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken) => Route(context, (IQuestionPort port) => port.GetQuestionAsync(context, questionId, kind, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken) => Route(context, (IQuestionPort port) => port.AnswerQuestionAsync(context, questionId, kind, answer, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteQuestionReportResult>> ReportQuestionAsync(AdapterContext context, string questionId, string kind, RemoteQuestionReport report, CancellationToken cancellationToken) => Route(context, (IQuestionPort port) => port.ReportQuestionAsync(context, questionId, kind, report, cancellationToken), cancellationToken);
    // The reasons are known per platform without a connection: the panel asks before a question is reported.
    public QuestionReportCapability ReportCapability(string platformCode)
    {
        var platform = platformCode.Trim().ToUpperInvariant();
        object? port;
        try { port = SelectPort(platform, trendyol, shopify, hepsiburada, n11, pazarama); }
        catch (InvalidOperationException) { port = null; }
        return port is IQuestionPort questions
            ? questions.ReportCapability(platform)
            : QuestionReportCapability.Unsupported(platform, "Bu pazaryerinin soruları panelde yönetilmiyor.", null);
    }
    public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken) => Route(context, (IInvoiceMarketplacePort port) => port.DeliverAsync(context, command, cancellationToken), cancellationToken);
    public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) => Route(context, (IInvoiceMarketplacePort port) => port.QueryDeliveryAsync(context, reference, cancellationToken), cancellationToken);
    public Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken) => Route(context, (IInvoiceMarketplacePort port) => port.RevokeLinkAsync(context, command, cancellationToken), cancellationToken);
    public Task<AdapterResult<RemoteOrderFinance>> GetOrderFinanceAsync(AdapterContext context, string orderNumber, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Route(context, (IOrderFinancePort port) => port.GetOrderFinanceAsync(context, orderNumber, from, to, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IHepsiburadaInvoiceStatusPort port) => port.ListCancelledOrderLinesAsync(context, since, until, page, cancellationToken), cancellationToken);
    public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => Route(context, (IHepsiburadaInvoiceStatusPort port) => port.ListMissingInvoicePackagesAsync(context, page, cancellationToken), cancellationToken);
}
