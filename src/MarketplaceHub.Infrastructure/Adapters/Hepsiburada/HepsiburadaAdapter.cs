using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaAdapter : IConnectionPort, IReferenceDataPort, IProductPort, IInventoryPricePort, IOrderPort, IReturnPort
{
    private readonly HepsiburadaConnectionProbe? _probe;
    private readonly HepsiburadaOrderReader? _orderReader;
    private HepsiburadaProbeEvidence? _lastEvidence;
    private Guid? _lastConnectionId;

    public HepsiburadaAdapter() { }
    public HepsiburadaAdapter(HepsiburadaConnectionProbe probe, HepsiburadaOrderReader orderReader) { _probe = probe; _orderReader = orderReader; }

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        if (_probe is null) return await Blocked<ConnectionIdentity>(AuthMessage);
        var result = await _probe.TestAsync(context, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        _lastEvidence = result.Value;
        _lastConnectionId = context.ConnectionId;
        return AdapterResult<ConnectionIdentity>.Success(result.Value!.Identity, result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        if (_probe is null) return await Blocked<IReadOnlyList<CapabilityEvidence>>(AuthMessage);
        var result = _lastEvidence is null || _lastConnectionId != context.ConnectionId ? await _probe.TestAsync(context, cancellationToken) : AdapterResult<HepsiburadaProbeEvidence>.Success(_lastEvidence);
        if (!result.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(result.Error!, result.RateLimit);
        var evidence = result.Value!;
        IReadOnlyList<CapabilityEvidence> capabilities =
        [
            new(
                F3Capabilities.ConnectionTest,
                "SUPPORTED",
                HepsiburadaContract.DocumentedApiVersion,
                evidence.Identity.Environment,
                evidence.Identity.ExternalStoreId,
                HepsiburadaContract.OrderSitSource,
                "2026-06-04",
                null,
                JsonSerializer.Serialize(new { readOnly = true, productFamily = "ORDER", externalWritesEnabled = false, responseItemCount = evidence.ItemCount }),
                "Hepsiburada Sipariş SIT Basic Auth bağlantısı ve anonim yanıt zarfı doğrulandı; sipariş alan eşlemesi doğrulanmadı.",
                evidence.ResponseSha256,
                evidence.VerifiedAt)
        ];
        var orderEvidence = _orderReader is null ? null : await _orderReader.ProbeAsync(context, cancellationToken);
        if (orderEvidence is { IsSuccess: true }) capabilities = [.. capabilities, new(
            F3Capabilities.OrderRead, "SUPPORTED", HepsiburadaContract.DocumentedApiVersion, evidence.Identity.Environment, evidence.Identity.ExternalStoreId,
            HepsiburadaContract.OrderSitSource, "2026-06-04", null,
            JsonSerializer.Serialize(new { readOnly = true, productFamily = "ORDER", externalWritesEnabled = false, responseOrderCount = orderEvidence.Value!.OrderCount }),
            "Hepsiburada SIT dolu sipariş yanıtı doğrulandı; sadece sipariş okuma eşlemesi aktiftir.", orderEvidence.Value!.ResponseSha256, orderEvidence.Value!.VerifiedAt)];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(capabilities, result.RateLimit);
    }

    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteReferenceItem>>(ReadMessage);
    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteProduct>>(ReadMessage);
    public Task<AdapterResult<RemoteOperationRef>> UpsertAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => WriteBlocked<RemoteOperationRef>();
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Blocked<RemoteOperationStatus>(ReadMessage);
    public Task<AdapterResult<bool>> ArchiveAsync(AdapterContext context, ExternalProductIdentity identity, CancellationToken cancellationToken) => WriteBlocked<bool>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushStockAsync(AdapterContext context, IReadOnlyList<StockPushLine> lines, CancellationToken cancellationToken) => WriteBlocked<BatchResult<BatchLineResult>>();
    public Task<AdapterResult<BatchResult<BatchLineResult>>> PushPricesAsync(AdapterContext context, IReadOnlyList<PricePushLine> lines, CancellationToken cancellationToken) => WriteBlocked<BatchResult<BatchLineResult>>();
    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (_orderReader is null) return await Blocked<AdapterPageResult<RemoteOrder>>(ReadMessage);
        if (!int.TryParse(page.Cursor, out var offset) || offset < 0) offset = 0;
        var response = await _orderReader.ReadAsync(context, offset, page.Limit, cancellationToken);
        if (!response.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(response.Error!, response.RateLimit);
        try { return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(HepsiburadaOrderJsonMapper.Orders(response.Value!.Json), response.RateLimit); }
        catch (JsonException) { return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_ORDER_CONTRACT_VIOLATION", "Hepsiburada SIT sipariş yanıtı doğrulanmış sipariş alanlarıyla eşleşmedi.", 422, null, null), response.RateLimit); }
    }
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Blocked<RemoteOrder>(ReadMessage);
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => WriteBlocked<PackageActionResult>();
    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> IReturnPort.PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Blocked<AdapterPageResult<RemoteReturnClaim>>(ReadMessage);
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => Blocked<RemoteReturnClaim>(ReadMessage);
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => WriteBlocked<ReturnActionResult>();

    private const string AuthMessage = "Hepsiburada SIT bağlantı kanıtı enjekte edilmeden dış bağlantı testi yapılmaz.";
    private const string ReadMessage = "Hepsiburada sipariş alan eşlemesi anonim, dolu SIT fixture ile doğrulanmadan dış veri okuması yapılmaz.";
    private static Task<AdapterResult<T>> Blocked<T>(string message) => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_CAPABILITY_UNVERIFIED", message, 422, null, null)));
    private static Task<AdapterResult<T>> WriteBlocked<T>() => Task.FromResult(AdapterResult<T>.Failure(new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITE_DISABLED", "Hepsiburada dış yazmaları SIT capability ve iş otoritesi kanıtı olmadan çalışmaz.", 422, null, null)));
}
