using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaAdapter : IConnectionPort, IReferenceDataPort, IProductPort, IInventoryPricePort, IOrderPort, IReturnPort
{
    public Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) => Blocked<ConnectionIdentity>(AuthMessage);
    public Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) => Blocked<IReadOnlyList<CapabilityEvidence>>(AuthMessage);
    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteReferenceItem>>(ReadMessage);
    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteProduct>>(ReadMessage);
    public Task<AdapterResult<RemoteOperationRef>> UpsertAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => WriteBlocked<RemoteOperationRef>();
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Blocked<RemoteOperationStatus>(ReadMessage);
    public Task<AdapterResult<bool>> ArchiveAsync(AdapterContext context, ExternalProductIdentity identity, CancellationToken cancellationToken) => WriteBlocked<bool>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushStockAsync(AdapterContext context, IReadOnlyList<StockPushLine> lines, CancellationToken cancellationToken) => WriteBlocked<BatchResult<BatchLineResult>>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushPricesAsync(AdapterContext context, IReadOnlyList<PricePushLine> lines, CancellationToken cancellationToken) => WriteBlocked<BatchResult<BatchLineResult>>();
    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteOrder>>(ReadMessage);
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Blocked<RemoteOrder>(ReadMessage);
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => WriteBlocked<PackageActionResult>();
    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> IReturnPort.PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteReturnClaim>>(ReadMessage);
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => Blocked<RemoteReturnClaim>(ReadMessage);
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => WriteBlocked<ReturnActionResult>();

    private const string AuthMessage = "Hepsiburada partner hesabında auth modeli, environment ve merchant scope doğrulanmadan dış bağlantı testi yapılmaz.";
    private const string ReadMessage = "Hepsiburada SIT fixture ve capability kanıtı olmadan dış read çağrısı yapılmaz.";
    private static Task<AdapterResult<T>> Blocked<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_CAPABILITY_UNVERIFIED", message, 422, null, null)));
    private static Task<AdapterResult<T>> WriteBlocked<T>() => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITE_DISABLED", "Hepsiburada dış yazmaları SIT capability ve iş otoritesi kanıtı olmadan çalışmaz.", 422, null, null)));
}
