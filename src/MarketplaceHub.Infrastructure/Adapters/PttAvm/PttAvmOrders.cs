using System.Net;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Order reads (phase 2). PTT AVM lists orders by date only (SiparisKontrolListesiV2, at most 40 days, no paging,
// AktifSiparisler=0 for every status) and has no read by order number; a single order is found in the list of the
// day its number names. Package actions, labels and cargo changes are not offered here: the PTT Kargo barcode and
// label come with phase 5 (docs/PTTAVM-ENTEGRASYON-PLANI.md).
public sealed partial class PttAvmHttpClient : IOrderPort
{
    internal static readonly TimeSpan MaxOrderWindow = TimeSpan.FromDays(40);

    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is not { } start || window.ModifiedBefore is not { } end || end <= start)
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "PTTAVM_ORDER_WINDOW_INVALID", "PTT AVM siparişleri başlangıcı ve bitişi belli bir tarih aralığıyla sorgulanabilir.", HttpStatusCode.UnprocessableEntity);
        var firstDay = start.ToOffset(TurkeyOffset).Date;
        var lastDay = end.ToOffset(TurkeyOffset).Date;
        if (lastDay - firstDay > MaxOrderWindow)
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "PTTAVM_ORDER_WINDOW_INVALID", "PTT AVM siparişleri en fazla 40 günlük bir tarih aralığıyla sorgulanabilir.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<AdapterPageResult<RemoteOrder>>(load);
        return await ReadOrdersAsync(load.Account, firstDay, lastDay, cancellationToken);
    }

    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
        GetAsync(context, externalOrderId, null, cancellationToken);

    // The order number names the order day (PTT-…-ddMMyy); without it the day the panel saw the order on is used.
    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken)
    {
        var orderNumber = externalOrderId.Trim();
        var day = PttAvmOrderMapper.OrderDay(orderNumber) ?? hint?.OrderedAt?.ToOffset(TurkeyOffset).Date;
        if (orderNumber.Length == 0 || day is null)
            return Failure<RemoteOrder>(AdapterErrorClass.Validation, "PTTAVM_ORDER_NUMBER_INVALID", "PTT AVM sipariş numarasından sipariş günü okunamadı.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOrder>(load);
        // A day either side covers an order placed around midnight.
        var page = await ReadOrdersAsync(load.Account, day.Value.AddDays(-1), day.Value.AddDays(1), cancellationToken);
        if (!page.IsSuccess) return AdapterResult<RemoteOrder>.Failure(page.Error!, page.RateLimit);
        var order = page.Value!.Items.FirstOrDefault(item => string.Equals(item.ExternalOrderId, orderNumber, StringComparison.OrdinalIgnoreCase));
        return order is null
            ? Failure<RemoteOrder>(AdapterErrorClass.NotFound, "PTTAVM_ORDER_NOT_FOUND", $"PTT AVM'de {orderNumber} numaralı sipariş {day.Value:dd.MM.yyyy} günü çevresinde bulunamadı.", HttpStatusCode.NotFound)
            : AdapterResult<RemoteOrder>.Success(order, page.RateLimit);
    }

    // One request reads every order of the [firstDay, lastDay] range with its current line statuses.
    internal async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> ReadOrdersAsync(PttAvmRequestContext account, DateTime firstDay, DateTime lastDay, CancellationToken cancellationToken)
    {
        var result = await SendSoapAsync(account, Orders, OrderWindowParameters(firstDay, lastDay), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(result.Error!, result.RateLimit);
        if (!OrderListShape(result.Value!))
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "PTTAVM_ORDER_CONTRACT_INVALID", $"PTT AVM sipariş yanıtı beklenen yapıda değil (yanıt öğeleri: {ElementShape(result.Value)}).", HttpStatusCode.BadGateway);
        return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(PttAvmOrderMapper.Page(result.Value!), result.RateLimit);
    }

    internal static IReadOnlyList<SoapParameter> OrderWindowParameters(DateTime firstDay, DateTime lastDay) =>
        [new("BaslangicTarihi", Day(firstDay)), new("BitisTarihi", Day(lastDay)), new("AktifSiparisler", "0")];

    // An empty, nil or TedarikciSiparisKontrolV2-only result is the documented list; anything else is not.
    internal static bool OrderListShape(System.Xml.Linq.XElement result) =>
        IsNil(result) || result.Elements().All(x => x.Name.LocalName == "TedarikciSiparisKontrolV2");

    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<PackageActionResult>("PTTAVM_PACKAGE_ACTION_UNSUPPORTED", "PTT AVM siparişlerinde paket işlemleri (işleme alma, kargoya verme) henüz panelden desteklenmiyor; PTT AVM satıcı panelinden yapılır."));

    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemoteCargoCompany>>("PTTAVM_CARGO_CHANGE_UNSUPPORTED", "PTT AVM siparişleri PTT Kargo ile gönderilir; kargo firması değiştirilmez."));

    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemotePackageableLine>>("PTTAVM_PACKAGEABLE_LINES_UNSUPPORTED", "PTT AVM paketlenebilir kalem listesi sağlamıyor."));

    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<CreateOrderPackageResult>("PTTAVM_PACKAGE_CREATE_UNSUPPORTED", "PTT AVM siparişten paket oluşturma servisi sağlamıyor."));

    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<bool>("PTTAVM_LABEL_UNSUPPORTED", "PTT AVM kargo etiketi henüz panelden alınamıyor; PTT Kargo barkodu ve etiketi sonraki aşamada eklenecek."));

    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") =>
        Task.FromResult(NotSupported<CommonLabelDocument>("PTTAVM_LABEL_UNSUPPORTED", "PTT AVM kargo etiketi henüz panelden alınamıyor; PTT Kargo barkodu ve etiketi sonraki aşamada eklenecek."));

    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<StageTestOrderResult>("PTTAVM_TEST_ORDER_UNSUPPORTED", "PTT AVM'nin test ortamı olmadığı için test siparişi oluşturulamaz."));

    private static AdapterResult<T> NotSupported<T>(string code, string message) =>
        Failure<T>(AdapterErrorClass.NotSupported, code, message, HttpStatusCode.NotImplemented);
}
