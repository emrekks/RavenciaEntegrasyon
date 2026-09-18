using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure;

public sealed class MarketplacePortRouter(
    AppDbContext db,
    TrendyolHttpClient trendyol,
    ShopifyHttpClient shopify)
    : IConnectionPort, IReferenceDataPort, IProductPort, IProductVisualLookupPort, IInventoryPricePort, IOrderPort, IReturnPort
{
    private async Task<T> Resolve<T>(AdapterContext context) where T : class
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == context.TenantId && x.Id == context.ConnectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync();
        object port = platform == "SHOPIFY" ? shopify : trendyol;
        return port as T ?? throw new InvalidOperationException($"{platform ?? "UNKNOWN"} bağlantısı {typeof(T).Name} portunu sağlamıyor.");
    }

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) => await (await Resolve<IConnectionPort>(context)).TestAsync(context, cancellationToken);
    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) => await (await Resolve<IConnectionPort>(context)).DiscoverCapabilitiesAsync(context, cancellationToken);
    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) => await (await Resolve<IReferenceDataPort>(context)).ReadAsync(context, resource, page, cancellationToken);
    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).ListAsync(context, page, filter, cancellationToken);
    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).ListCatalogAsync(context, page, filter, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).CreateAsync(context, publication, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).UpdateUnapprovedAsync(context, publication, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).UpdateApprovedContentAsync(context, publication, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).UpdateApprovedVariantsAsync(context, publication, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).UpdateApprovedDeliveryAsync(context, publication, cancellationToken);
    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).GetOperationAsync(context, externalOperationId, cancellationToken);
    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).GetPublicationStatusAsync(context, barcode, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => await (await Resolve<IProductPort>(context)).ArchiveAsync(context, payloadJson, cancellationToken);
    public async Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => await (await Resolve<IProductVisualLookupPort>(context)).FindByBarcodeAsync(context, barcode, cancellationToken);
    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => await (await Resolve<IInventoryPricePort>(context)).PushPriceAndInventoryAsync(context, payloadJson, cancellationToken);
    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).PollAsync(context, window, page, cancellationToken);
    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).GetAsync(context, externalOrderId, cancellationToken);
    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).ExecutePackageActionAsync(context, command, cancellationToken);
    public async Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).CreateCommonLabelAsync(context, request, cancellationToken);
    public async Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).GetCommonLabelAsync(context, cargoTrackingNumber, cancellationToken);
    public async Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => await (await Resolve<IOrderPort>(context)).CreateStageTestOrderAsync(context, barcode, cancellationToken);
    public async Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => await (await Resolve<IReturnPort>(context)).PollAsync(context, window, page, cancellationToken);
    async Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => await (await Resolve<IReturnPort>(context)).GetAsync(context, externalReturnId, cancellationToken);
    public async Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => await (await Resolve<IReturnPort>(context)).IssueReasonsAsync(context, cancellationToken);
    public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => await (await Resolve<IReturnPort>(context)).ExecuteAsync(context, command, cancellationToken);
}
