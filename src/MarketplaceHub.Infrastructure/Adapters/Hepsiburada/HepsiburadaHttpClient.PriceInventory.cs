using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    // C19: the known clients upload at most 1,000 items per inventory-upload; 4,000 was never confirmed.
    internal const int MaxInventoryUploadItems = 1000;
    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return await Unsupported<RemoteOperationRef>("Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fiyat/stok yüklemesi gönderilmedi.");
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIAL_INVALID", "Hepsiburada bağlantı bilgileri bulunamadı.", HttpStatusCode.Unauthorized);
        if (!ExternalWriteGate.AllowsRuntimeWrite(account.Connection, context, GlobalWritesEnabled)
            || !await authentication.HasVerifiedWriteEvidenceAsync(account.Connection, cancellationToken, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite))
            return await Unsupported<RemoteOperationRef>("Hepsiburada fiyat/stok yüklemesi yalnız dış yazma kapıları ve mağaza/ortam kapsamlı PRICE_WRITE/INVENTORY_WRITE SIT kanıtları sağlandığında kullanılabilir.");
        if (!ValidPriceInventoryPayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRICE_INVENTORY_PAYLOAD_INVALID", "Hepsiburada fiyat/stok yüklemesi merchantId, hbSku, merchantSku, fiyat ve adet sözleşmesini sağlamıyor.", HttpStatusCode.BadRequest);
        payloadJson = WithHepsiburadaSkus(payloadJson, await StageHepsiburadaSkusAsync(account, cancellationToken));
        if (!ValidPriceInventoryPayload(payloadJson, account.Connection.ExternalStoreId))
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "HEPSIBURADA_PRICE_INVENTORY_PAYLOAD_INVALID", "Hepsiburada listing hbSku eşlemesi fiyat/stok yüklemesinde yinelenen hbSku üretti.", HttpStatusCode.BadRequest);

        using var content = new StringContent(BuildInventoryUploadXml(payloadJson), Encoding.UTF8, "application/xml");
        var response = await SendAsync(account, account.ListingBaseAddress, HttpMethod.Post, InventoryUpload(account), content, cancellationToken);
        using var responseDocument = response.Value;
        if (!response.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(response.Error!, response.RateLimit);
        try
        {
            var uploadId = HepsiburadaJsonMapper.InventoryUploadId(response.Value!.RootElement);
            return AdapterResult<RemoteOperationRef>.Success(new($"LISTING_INVENTORY:{uploadId}", "LISTING_INVENTORY_UPLOAD", timeProvider.GetUtcNow()), response.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<RemoteOperationRef>(AdapterErrorClass.ContractViolation, "HEPSIBURADA_INVENTORY_UPLOAD_CONTRACT_INVALID", "Hepsiburada listing yanıtında inventoryUploadId bulunamadı.", HttpStatusCode.BadGateway);
        }
    }
    internal static string InventoryUpload(HepsiburadaRequestContext context) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/inventory-uploads";
    internal static string InventoryUploadStatus(HepsiburadaRequestContext context, string uploadId) => $"listings/merchantid/{Uri.EscapeDataString(context.Connection.ExternalStoreId)}/inventory-uploads/id/{Uri.EscapeDataString(uploadId)}";
    internal static string InventoryUploadStatus(string merchantId, string uploadId) => $"listings/merchantid/{Uri.EscapeDataString(merchantId)}/inventory-uploads/id/{Uri.EscapeDataString(uploadId)}";
    internal static bool ValidPriceInventoryPayload(string payloadJson, string merchantId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("merchantId", out var merchant) || merchant.ValueKind != JsonValueKind.String
                || !string.Equals(merchant.GetString(), merchantId, StringComparison.Ordinal)
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() < 1 || items.GetArrayLength() > MaxInventoryUploadItems) return false;
            var merchantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hbSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var merchantSku = item.TryGetProperty("merchantSku", out var merchantSkuValue) && merchantSkuValue.ValueKind == JsonValueKind.String ? merchantSkuValue.GetString() : null;
                var hbSku = item.TryGetProperty("hepsiburadaSku", out var hbSkuValue) && hbSkuValue.ValueKind == JsonValueKind.String ? hbSkuValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(merchantSku) || merchantSku != merchantSku.Trim() || merchantSku.Any(char.IsWhiteSpace)
                    || string.IsNullOrWhiteSpace(hbSku) || hbSku != hbSku.Trim() || hbSku.Any(char.IsWhiteSpace)
                    || !merchantSkus.Add(merchantSku) || !hbSkus.Add(hbSku)) return false;
                // A listing Hepsiburada fulfils (isFulfilledByHB) goes without a stock: its stock is Hepsiburada's.
                // A stock change alone goes without a price; a line carries at least one of the two.
                var hasStock = item.TryGetProperty("availableStock", out var stockValue);
                if (hasStock && RemoteJson.Int32(stockValue) is not >= 0) return false;
                var hasPrice = item.TryGetProperty("price", out var priceValue);
                if (hasPrice && (RemoteJson.NumberDecimal(priceValue) is not { } price || price <= 0 || decimal.Round(price, 2) != price)) return false;
                if (!hasStock && !hasPrice) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static string BuildInventoryUploadXml(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new XElement("listing",
            new XElement("HepsiburadaSku", item.GetProperty("hepsiburadaSku").GetString()),
            new XElement("MerchantSku", item.GetProperty("merchantSku").GetString()),
            item.TryGetProperty("price", out var price) ? new XElement("Price", price.GetDecimal().ToString("0.00", CultureInfo.GetCultureInfo("tr-TR"))) : null,
            item.TryGetProperty("availableStock", out var stock) ? new XElement("AvailableStock", stock.GetInt32().ToString(CultureInfo.InvariantCulture)) : null));
        return new XDocument(new XElement("listings", items)).ToString(SaveOptions.DisableFormatting);
    }
}
