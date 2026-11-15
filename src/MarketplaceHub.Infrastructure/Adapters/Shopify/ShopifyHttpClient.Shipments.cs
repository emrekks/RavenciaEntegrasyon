using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    public async Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
    {
        var shop = await authentication.LoadAsync(context.TenantId, context.ConnectionId, settings.ApiVersion, cancellationToken);
        if (shop is null) return CredentialFailure(Fail<PackageActionResult>(AdapterErrorClass.Authentication, "SHOPIFY_CREDENTIAL_INVALID", "Shopify yetkilendirmesi bulunamadı.", HttpStatusCode.Unauthorized));
        if (!await CanWriteAsync(shop, context, MarketplaceCapabilities.ShipmentWrite, cancellationToken)) return WriteClosed<PackageActionResult>();
        if (string.IsNullOrWhiteSpace(command.ExternalPackageId)) return Fail<PackageActionResult>(AdapterErrorClass.Validation, "SHOPIFY_FULFILLMENT_ID_REQUIRED", "Shopify fulfillment kimliği zorunludur.", HttpStatusCode.BadRequest);
        if (!string.Equals(command.Action, "TRACKING_NUMBER", StringComparison.OrdinalIgnoreCase))
            return Fail<PackageActionResult>(AdapterErrorClass.NotSupported, "SHOPIFY_SHIPMENT_ACTION_UNSUPPORTED", "Shopify bu panelde yalnız mevcut kargo takibi bilgisinin güncellenmesini destekler.", HttpStatusCode.NotImplemented);
        JsonDocument payload;
        try { payload = JsonDocument.Parse(command.PayloadJson); }
        catch (JsonException) { return Fail<PackageActionResult>(AdapterErrorClass.Validation, "SHOPIFY_TRACKING_PAYLOAD_INVALID", "Kargo takip bilgisi geçerli JSON olmalıdır.", HttpStatusCode.BadRequest); }
        using (payload)
        {
            var number = TextValue(payload.RootElement, "trackingNumber") ?? TextValue(payload.RootElement, "number");
            var url = TextValue(payload.RootElement, "trackingUrl") ?? TextValue(payload.RootElement, "url");
            var company = TextValue(payload.RootElement, "cargoProviderName") ?? TextValue(payload.RootElement, "company");
            if (string.IsNullOrWhiteSpace(number) || number.Length > 255 || url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var trackingUri) || trackingUri.Scheme != Uri.UriSchemeHttps))
                return Fail<PackageActionResult>(AdapterErrorClass.Validation, "SHOPIFY_TRACKING_PAYLOAD_INVALID", "Geçerli takip numarası ve varsa HTTPS takip bağlantısı zorunludur.", HttpStatusCode.BadRequest);
            var fulfillmentId = command.ExternalPackageId.StartsWith("gid://", StringComparison.Ordinal) ? command.ExternalPackageId : $"gid://shopify/Fulfillment/{command.ExternalPackageId}";
            var result = await QueryAsync(shop, "mutation($fulfillmentId:ID!,$trackingInfoInput:FulfillmentTrackingInput!){fulfillmentTrackingInfoUpdate(fulfillmentId:$fulfillmentId,trackingInfoInput:$trackingInfoInput,notifyCustomer:false){fulfillment{id} userErrors{field message}}}", new { fulfillmentId, trackingInfoInput = new { number, url, company } }, cancellationToken);
            if (!result.IsSuccess) return AdapterResult<PackageActionResult>.Failure(result.Error!, result.RateLimit);
            var response = result.Value!.RootElement.GetProperty("fulfillmentTrackingInfoUpdate");
            if (HasUserErrors(response, out var error)) return Fail<PackageActionResult>(AdapterErrorClass.Validation, "SHOPIFY_TRACKING_UPDATE_REJECTED", error ?? "Shopify kargo takip bilgisini güncellemedi.", HttpStatusCode.UnprocessableEntity);
            return AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "TRACKING_UPDATED", null), result.RateLimit);
        }
    }
    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<RemoteCargoCompany>>("Shopify kargo firması listesi sağlamaz.");
    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<RemotePackageableLine>>("Shopify Hepsiburada paketlenebilir kalem listesini sağlamaz.");
    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => Unsupported<CreateOrderPackageResult>("Shopify Hepsiburada paket oluşturma işlemini sağlamaz.");
    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => Unsupported<bool>("Shopify etiket işlemi ilk sürümde kapalıdır.");
    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => Unsupported<CommonLabelDocument>("Shopify etiket işlemi ilk sürümde kapalıdır.");
    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Unsupported<StageTestOrderResult>("Shopify test siparişi oluşturma ilk sürümde kapalıdır.");
}
