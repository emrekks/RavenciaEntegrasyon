using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Price and stock writes (phase 4). UpdateProductsStockPrice takes up to 1000 products keyed by barcode with the
// VAT-inclusive price, the VAT rate and the stock (0-9999) in one request, queues it and answers a tracking id;
// GetProductsTrackingResult later answers each barcode's outcome. PTT AVM refuses the same request again within five
// minutes. Every send passes the adapter's write gate, so nothing leaves while external writes are off. Variants of
// a product are keyed by their attributes on PTT AVM's side; the panel sends each linked barcode on its own.
public sealed partial class PttAvmHttpClient : IInventoryPricePort
{
    internal const string StockPriceUpdate = "UpdateProductsStockPrice";
    internal const string ProductsTracking = "GetProductsTrackingResult";
    internal const string RequestsNamespace = "http://schemas.datacontract.org/2004/07/ePttAVMService.Model.Requests";
    internal const string TrackingOperationPrefix = "t:";

    public async Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken)
    {
        var items = PttAvmPriceInventoryRules.Items(payloadJson);
        if (items is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRICE_STOCK_PAYLOAD_INVALID", $"PTT AVM fiyat-stok içeriği beklenen yapıda değil (en fazla {PttAvmPriceInventoryRules.MaxItemsPerRequest} ürün, barkod ve geçerli değerler).", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOperationRef>(load);
        if (WriteBlocked<RemoteOperationRef>(load.Account, "fiyat-stok") is { } blocked) return blocked;

        var result = await SendSoapAsync(load.Account, StockPriceUpdate, [new("items", PttAvmPriceInventoryRules.ItemsXml(items), Raw: true)], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        var trackingId = ContractValue(result.Value!, "TrackingId");
        if (!string.Equals(ContractValue(result.Value!, "Success"), "true", StringComparison.OrdinalIgnoreCase) || trackingId is null)
            // A refusal is final for this payload (the same request may not come again within five minutes).
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRICE_STOCK_REJECTED", $"PTT AVM fiyat-stok isteğini kabul etmedi: {ContractValue(result.Value!, "Message") ?? "gerekçe verilmedi"}.", HttpStatusCode.UnprocessableEntity);
        return AdapterResult<RemoteOperationRef>.Success(new(TrackingOperationPrefix + trackingId, "PRICE_STOCK_UPDATE", timeProvider.GetUtcNow()), result.RateLimit);
    }

    // Serves the price/stock tracking ids ("t:{trackingId}"); product writes open with phase 7.
    public async Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
    {
        if (!externalOperationId.StartsWith(TrackingOperationPrefix, StringComparison.Ordinal) || externalOperationId.Length <= TrackingOperationPrefix.Length)
            return Failure<RemoteOperationStatus>(AdapterErrorClass.Validation, "PTTAVM_OPERATION_ID_INVALID", "PTT AVM işlem numarası geçersiz.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOperationStatus>(load);
        var result = await SendSoapAsync(load.Account, ProductsTracking, [new("trackingId", externalOperationId[TrackingOperationPrefix.Length..])], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOperationStatus>.Failure(result.Error!, result.RateLimit);
        return PttAvmPriceInventoryRules.Tracking(externalOperationId, result.Value!) is { } status
            ? AdapterResult<RemoteOperationStatus>.Success(status, result.RateLimit)
            : Failure<RemoteOperationStatus>(AdapterErrorClass.ContractViolation, "PTTAVM_TRACKING_CONTRACT_INVALID", $"PTT AVM işlem sonucu beklenen yapıda değil (yanıt öğeleri: {ElementShape(result.Value)}).", HttpStatusCode.BadGateway);
    }
}

public static class PttAvmPriceInventoryRules
{
    public const int MaxItemsPerRequest = 1000;
    public const int MaxQuantity = 9999;
    public const int MaxBarcodeLength = 250;
    // PTT AVM takes these VAT rates only; another rate is left out so PTT AVM keeps the one it holds.
    private static readonly decimal[] VatRates = [0m, 1m, 10m, 20m];

    public sealed record Item(string Barcode, decimal PriceWithVat, decimal? VatRate, int Quantity);

    public static decimal Price(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static int Quantity(decimal available) => (int)Math.Clamp(decimal.Floor(Math.Max(0, available)), 0, MaxQuantity);

    public static decimal? VatRate(decimal? rate) => rate is { } value && VatRates.Contains(value) ? value : null;

    public static string PriceHash(decimal salePrice, decimal? vatRate, long priceVersion) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{salePrice:0.00}|{vatRate?.ToString("0", CultureInfo.InvariantCulture) ?? "-"}|TRY|{priceVersion}"))));

    // The composer writes {"items":[{barcode, priceWithVat, vatRate?, quantity}]}.
    public static string Payload(IReadOnlyList<(PriceInventoryPushLine Line, decimal? VatRate)> lines)
    {
        var items = new JsonArray();
        foreach (var (line, vatRate) in lines)
        {
            var item = new JsonObject { ["barcode"] = line.Barcode, ["priceWithVat"] = Price(line.SalePrice), ["quantity"] = Quantity(line.Quantity) };
            if (VatRate(vatRate) is { } rate) item["vatRate"] = rate;
            items.Add(item);
        }
        return new JsonObject { ["items"] = items }.ToJsonString();
    }

    // Null when the shape, the count or a value is not acceptable.
    public static IReadOnlyList<Item>? Items(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var items = new List<Item>();
            foreach (var entry in list.EnumerateArray())
            {
                var barcode = entry.TryGetProperty("barcode", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()?.Trim() : null;
                var price = entry.TryGetProperty("priceWithVat", out var priceValue) && priceValue.TryGetDecimal(out var parsedPrice) ? parsedPrice : (decimal?)null;
                var quantity = entry.TryGetProperty("quantity", out var quantityValue) && quantityValue.TryGetInt32(out var parsedQuantity) ? parsedQuantity : (int?)null;
                decimal? vat = entry.TryGetProperty("vatRate", out var vatValue) && vatValue.TryGetDecimal(out var parsedVat) ? parsedVat : null;
                // PTT AVM refuses a price of 1 or less.
                if (string.IsNullOrEmpty(barcode) || barcode.Length > MaxBarcodeLength || price is not > 1 || quantity is not (>= 0 and <= MaxQuantity) || vat is not null && VatRate(vat) is null) return null;
                items.Add(new(barcode, Price(price.Value), vat, quantity.Value));
            }
            return items.Count is > 0 and <= MaxItemsPerRequest && items.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count ? items : null;
        }
        catch (JsonException) { return null; }
    }

    // ProductStockPriceRequest fields in the data-contract (alphabetical) order of the documented sample.
    internal static string ItemsXml(IReadOnlyList<Item> items)
    {
        var xml = new StringBuilder();
        foreach (var item in items)
        {
            xml.Append($"""<ept:ProductStockPriceRequest xmlns:ept="{PttAvmHttpClient.RequestsNamespace}">""");
            xml.Append("<ept:Barcode>").Append(SecurityElement.Escape(item.Barcode)).Append("</ept:Barcode>");
            xml.Append("<ept:PriceWithVAT>").Append(item.PriceWithVat.ToString("0.00", CultureInfo.InvariantCulture)).Append("</ept:PriceWithVAT>");
            xml.Append("<ept:Quantity>").Append(item.Quantity.ToString(CultureInfo.InvariantCulture)).Append("</ept:Quantity>");
            if (item.VatRate is { } rate) xml.Append("<ept:VATRate>").Append(rate.ToString("0", CultureInfo.InvariantCulture)).Append("</ept:VATRate>");
            xml.Append("</ept:ProductStockPriceRequest>");
        }
        return xml.ToString();
    }

    // GetProductsTrackingResultResult {Status, Progress, ProductsSubTrackingResult{ProductBasedInfos{ProductBasedInfo*}}}.
    // Waiting/InProgress anywhere is IN_PROGRESS; otherwise every barcode is final and the operation is COMPLETED,
    // a line succeeding when PTT AVM completed it without a failure reason, or found nothing to change.
    public static RemoteOperationStatus? Tracking(string operationId, XElement result)
    {
        var overall = PttAvmHttpClient.ContractValue(result, "Status");
        if (overall is null) return null;
        var infos = result.Descendants().Where(x => x.Name.LocalName == "ProductBasedInfo").ToList();
        var lines = new List<RemoteOperationLine>();
        var pending = overall.Equals("Waiting", StringComparison.OrdinalIgnoreCase) || overall.Equals("InProgress", StringComparison.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            var barcode = PttAvmHttpClient.ContractValue(info, "Barcode");
            var status = PttAvmHttpClient.ContractValue(info, "Status") ?? "";
            if (barcode is null) continue;
            if (status.Equals("Waiting", StringComparison.OrdinalIgnoreCase) || status.Equals("InProgress", StringComparison.OrdinalIgnoreCase)) pending = true;
            var message = PttAvmHttpClient.ContractValue(info, "Message");
            var reasons = info.Elements().FirstOrDefault(x => x.Name.LocalName == "FailureReasons")?.Elements().Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList() ?? [];
            var unchanged = message is not null && PttAvmOrderMapper.Fold(message).Contains("degisiklik tespit edilemedi", StringComparison.Ordinal);
            var succeeded = status.Equals("Completed", StringComparison.OrdinalIgnoreCase) && reasons.Count == 0 || unchanged;
            var text = string.Join(" ", new[] { message }.Concat(reasons).Where(x => !string.IsNullOrWhiteSpace(x)));
            lines.Add(new(barcode, succeeded, PttAvmHttpClient.ContractValue(info, "ProductId"),
                succeeded ? null : status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? "PTTAVM_PRODUCT_UPDATE_PARTIAL" : "PTTAVM_PRODUCT_UPDATE_CANCELLED",
                false, text.Length == 0 ? status : text));
        }
        if (!pending && infos.Count == 0 && overall.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return new(operationId, "REJECTED", lines);
        return new(operationId, pending ? "IN_PROGRESS" : "COMPLETED", lines);
    }
}
