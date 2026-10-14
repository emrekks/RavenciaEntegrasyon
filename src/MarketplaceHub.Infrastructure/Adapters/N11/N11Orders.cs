using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Order reads (phase 2). Package actions, labels and cargo changes come with
// the order-action phase; until then they report "not supported".
public sealed partial class N11HttpClient
{
    internal static readonly TimeSpan MaxOrderWindow = TimeSpan.FromDays(15);
    internal const int OrderPageSize = 100;
    private const string OrderActionsLater = "N11 sipariş işlemleri (onay, paket bölme, iptal) sonraki entegrasyon aşamasında açılacak.";

    // orderByField=true filters by the package's last modification, so status
    // changes of older orders come back in the incremental window as well.
    internal static string ModifiedShipmentPackages(DateTimeOffset start, DateTimeOffset end, int page, int size) =>
        $"rest/delivery/v1/shipmentPackages?startDate={start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endDate={end.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}&orderByField=true&orderByDirection=ASC";

    internal static string OrderNumberPackages(string orderNumber) =>
        $"rest/delivery/v1/shipmentPackages?orderNumber={Uri.EscapeDataString(orderNumber)}&page=0&size={OrderPageSize.ToString(CultureInfo.InvariantCulture)}";

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is not { } start || window.ModifiedBefore is not { } end || end <= start || end - start > MaxOrderWindow)
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "N11_ORDER_WINDOW_INVALID", "N11 siparişleri en fazla 15 günlük, başlangıcı ve bitişi belli bir tarih aralığıyla sorgulanabilir.", HttpStatusCode.UnprocessableEntity);
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteOrder>>();
        var result = await SendAsync(account, HttpMethod.Get, ModifiedShipmentPackages(start, end, pageNumber, Math.Clamp(page.Limit, 1, OrderPageSize)), cancellationToken);
        return MapOrders(result);
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOrder>();
        var page = MapOrders(await SendAsync(account, HttpMethod.Get, OrderNumberPackages(externalOrderId.Trim()), cancellationToken));
        if (!page.IsSuccess) return AdapterResult<RemoteOrder>.Failure(page.Error!, page.RateLimit);
        var order = TrendyolJsonMapper.MergeOrderPackages(page.Value!.Items, externalOrderId.Trim());
        return order is null
            ? Failure<RemoteOrder>(AdapterErrorClass.NotFound, "N11_ORDER_NOT_FOUND", $"N11'de {externalOrderId} numaralı sipariş bulunamadı. N11 Kasım 2024 öncesi siparişleri bu servisten vermiyor.", HttpStatusCode.NotFound)
            : AdapterResult<RemoteOrder>.Success(order, page.RateLimit);
    }

    private static AdapterResult<AdapterPageResult<RemoteOrder>> MapOrders(AdapterResult<JsonDocument> result)
    {
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try { return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(N11OrderMapper.Packages(document.RootElement.GetRawText()), result.RateLimit); }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "N11_ORDER_CONTRACT_INVALID", "N11 sipariş paketi yanıtı beklenen sayfa yapısında değil.", HttpStatusCode.BadGateway);
        }
    }

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<PackageActionResult>("N11_PACKAGE_ACTION_UNSUPPORTED", OrderActionsLater));

    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemoteCargoCompany>>("N11_CARGO_CHANGE_UNSUPPORTED", "N11 kargo firması değiştirmeyi API'den desteklemiyor."));

    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemotePackageableLine>>("N11_PACKAGEABLE_LINES_UNSUPPORTED", "N11 paketlenebilir kalem listesi sağlamıyor."));

    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<CreateOrderPackageResult>("N11_PACKAGE_CREATE_UNSUPPORTED", "N11 siparişten paket oluşturmayı desteklemiyor; paketler N11 tarafından açılır."));

    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<bool>("N11_LABEL_UNSUPPORTED", "N11 kargo etiketi servisi sağlamıyor; etiket kargo kampanya koduyla panelden basılır."));

    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") =>
        Task.FromResult(NotSupported<CommonLabelDocument>("N11_LABEL_UNSUPPORTED", "N11 kargo etiketi servisi sağlamıyor; etiket kargo kampanya koduyla panelden basılır."));

    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<StageTestOrderResult>("N11_TEST_ORDER_UNSUPPORTED", "N11'in test ortamı olmadığı için test siparişi oluşturulamaz."));

    private static AdapterResult<T> NotSupported<T>(string code, string message) =>
        Failure<T>(AdapterErrorClass.NotSupported, code, message, HttpStatusCode.NotImplemented);
}
