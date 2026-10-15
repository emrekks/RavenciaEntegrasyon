using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Order reads. Pazarama lists orders by order date only (at most one month per request, end day
// excluded) or by order number (up to six months back). Labels, cargo changes and package
// creation are not offered here: with Pazarama contracted cargo the cargo process runs from the
// shipment code once an order is set to "Hazırlanıyor" (phase 5).
public sealed partial class PazaramaHttpClient : IOrderPort
{
    public async Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        if (window.ModifiedAfter is not { } start || window.ModifiedBefore is not { } end || end <= start || end - start > TimeSpan.FromDays(31))
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.Validation, "PAZARAMA_ORDER_WINDOW_INVALID", "Pazarama siparişleri en fazla bir aylık, başlangıcı ve bitişi belli bir tarih aralığıyla sorgulanabilir.", HttpStatusCode.UnprocessableEntity);
        var pageNumber = int.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        var size = Math.Clamp(page.Limit, 1, PazaramaOrderHistoryPolicy.PageSize);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteOrder>>();
        var local = PazaramaOrderHistoryPolicy.TurkeyOffset;
        var body = new { pageSize = size, pageNumber, startDate = Day(start.ToOffset(local).Date), endDate = Day(end.ToOffset(local).Date) };
        var result = await SendAsync(account, HttpMethod.Post, Orders, cancellationToken, Json(body));
        var mapped = MapPage(result, size, pageNumber);
        return mapped;
    }

    public async Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
    {
        var orderNumber = externalOrderId.Trim();
        if (!long.TryParse(orderNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return Failure<RemoteOrder>(AdapterErrorClass.Validation, "PAZARAMA_ORDER_NUMBER_INVALID", "Pazarama sipariş numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOrder>();
        // By order number Pazarama looks six months back; the dates still have to be sent.
        var today = timeProvider.GetUtcNow().ToOffset(PazaramaOrderHistoryPolicy.TurkeyOffset).Date;
        var body = new { orderNumber = number, startDate = Day(today.AddMonths(-6)), endDate = Day(today.AddDays(1)) };
        var page = MapPage(await SendAsync(account, HttpMethod.Post, Orders, cancellationToken, Json(body)), 100, 1);
        if (!page.IsSuccess) return AdapterResult<RemoteOrder>.Failure(page.Error!, page.RateLimit);
        var order = page.Value!.Items.FirstOrDefault(item => item.ExternalOrderId == orderNumber);
        return order is null
            ? Failure<RemoteOrder>(AdapterErrorClass.NotFound, "PAZARAMA_ORDER_NOT_FOUND", $"Pazarama'da {externalOrderId} numaralı sipariş bulunamadı (sipariş numarasıyla en çok 6 ay geriye bakılır).", HttpStatusCode.NotFound)
            : AdapterResult<RemoteOrder>.Success(order, page.RateLimit);
    }

    private static AdapterResult<AdapterPageResult<RemoteOrder>> MapPage(AdapterResult<JsonDocument> result, int pageSize, int pageNumber)
    {
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteOrder>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try
        {
            var page = PazaramaOrderMapper.Page(document.RootElement, pageSize);
            return AdapterResult<AdapterPageResult<RemoteOrder>>.Success(page with { NextCursor = page.HasMore ? (pageNumber + 1).ToString(CultureInfo.InvariantCulture) : null }, result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteOrder>>(AdapterErrorClass.ContractViolation, "PAZARAMA_ORDER_CONTRACT_INVALID", "Pazarama sipariş yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemoteCargoCompany>>("PAZARAMA_CARGO_CHANGE_UNSUPPORTED", "Pazarama anlaşmalı kargoda kargo firması panelden değiştirilmez."));

    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<IReadOnlyList<RemotePackageableLine>>("PAZARAMA_PACKAGEABLE_LINES_UNSUPPORTED", "Pazarama paketlenebilir kalem listesi sağlamıyor."));

    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<CreateOrderPackageResult>("PAZARAMA_PACKAGE_CREATE_UNSUPPORTED", "Pazarama siparişten paket oluşturma henüz panelden desteklenmiyor."));

    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<bool>("PAZARAMA_LABEL_UNSUPPORTED", "Pazarama kargo etiketi servisi sağlamıyor; anlaşmalı kargo gönderi koduyla (PZ...) ilerler."));

    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") =>
        Task.FromResult(NotSupported<CommonLabelDocument>("PAZARAMA_LABEL_UNSUPPORTED", "Pazarama kargo etiketi servisi sağlamıyor; anlaşmalı kargo gönderi koduyla (PZ...) ilerler."));

    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<StageTestOrderResult>("PAZARAMA_TEST_ORDER_UNSUPPORTED", "Pazarama'nın test ortamı olmadığı için test siparişi oluşturulamaz."));

    private static AdapterResult<T> NotSupported<T>(string code, string message) =>
        Failure<T>(AdapterErrorClass.NotSupported, code, message, HttpStatusCode.NotImplemented);
}
