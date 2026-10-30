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
// a product are keyed by their attributes on PTT AVM's side: a product with variants goes by its barcode (the model
// code) with each variant's colour/size, stock and price difference in Variants.
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
        return StockPriceAnswer(result.Value!, timeProvider.GetUtcNow(), result.RateLimit);
    }

    // UpdateProductsStockPrice's answer: a tracking id, or a refusal. "Aynı isteği ancak 5 dakika sonra tekrardan
    // gönderebilirsiniz" is not a refusal of the values: the request did not run and may go again after the window
    // (the job decides whether PTT AVM already holds the values). Any other refusal is final for this payload.
    internal static AdapterResult<RemoteOperationRef> StockPriceAnswer(XElement answer, DateTimeOffset now, RateLimitMetadata? rateLimit)
    {
        var trackingId = ContractValue(answer, "TrackingId");
        if (string.Equals(ContractValue(answer, "Success"), "true", StringComparison.OrdinalIgnoreCase) && trackingId is not null)
            return AdapterResult<RemoteOperationRef>.Success(new(TrackingOperationPrefix + trackingId, "PRICE_STOCK_UPDATE", now), rateLimit);
        var message = ContractValue(answer, "Message");
        if (PttAvmProductPayloadRules.IsRepeatRequestRefusal(message))
            return AdapterResult<RemoteOperationRef>.Failure(PttAvmProductPayloadRules.RepeatRequestError((int)HttpStatusCode.TooManyRequests), new RateLimitMetadata(null, null, PttAvmProductPayloadRules.RepeatRequestWait));
        return AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.Validation, "PTTAVM_PRICE_STOCK_REJECTED", $"PTT AVM fiyat-stok isteğini kabul etmedi: {message ?? "gerekçe verilmedi"}.", (int)HttpStatusCode.UnprocessableEntity, null, null), rateLimit);
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

    public const int MaxVariantsPerProduct = 100;

    // A product with variants carries no stock of its own: PTT AVM sets it to the sum of the variants it holds. Each
    // variant is named by its colour and size (Definition "Renk"/"Beden"), and its Price is the VAT-inclusive
    // difference from the product's price.
    public sealed record Item(string Barcode, decimal PriceWithVat, decimal? VatRate, int? Quantity, IReadOnlyList<VariantItem>? Variants = null)
    {
        public bool Equals(Item? other) => other is not null && Barcode == other.Barcode && PriceWithVat == other.PriceWithVat && VatRate == other.VatRate && Quantity == other.Quantity
            && (Variants ?? []).SequenceEqual(other.Variants ?? []);
        public override int GetHashCode() => HashCode.Combine(Barcode, PriceWithVat, VatRate, Quantity);
    }

    public sealed record VariantItem(IReadOnlyList<VariantAttribute> Attributes, int Quantity, decimal Price)
    {
        public bool Equals(VariantItem? other) => other is not null && Quantity == other.Quantity && Price == other.Price && Attributes.SequenceEqual(other.Attributes);
        public override int GetHashCode() => HashCode.Combine(Quantity, Price, Attributes.Count);
    }

    public sealed record VariantAttribute(string Definition, string Value);

    public static decimal Price(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static int Quantity(decimal available) => (int)Math.Clamp(decimal.Floor(Math.Max(0, available)), 0, MaxQuantity);

    public static decimal? VatRate(decimal? rate) => rate is { } value && VatRates.Contains(value) ? value : null;

    public static string PriceHash(decimal salePrice, decimal? vatRate, long priceVersion) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{salePrice:0.00}|{vatRate?.ToString("0", CultureInfo.InvariantCulture) ?? "-"}|TRY|{priceVersion}"))));

    // One product per linked barcode: {"items":[{barcode, priceWithVat, vatRate?, quantity}]}.
    public static string Payload(IReadOnlyList<(PriceInventoryPushLine Line, decimal? VatRate)> lines) =>
        Payload(lines.Select(x => new Item(x.Line.Barcode, Price(x.Line.SalePrice), VatRate(x.VatRate), Quantity(x.Line.Quantity))).ToList());

    // {"items":[{barcode, priceWithVat, vatRate?, quantity?, variants?:[{attributes:[{definition, value}], quantity, price}]}]}.
    public static string Payload(IReadOnlyList<Item> items)
    {
        var list = new JsonArray();
        foreach (var item in items)
        {
            var entry = new JsonObject { ["barcode"] = item.Barcode, ["priceWithVat"] = Price(item.PriceWithVat) };
            if (VatRate(item.VatRate) is { } rate) entry["vatRate"] = rate;
            if (item.Quantity is { } quantity) entry["quantity"] = quantity;
            if (item.Variants is { Count: > 0 } variants)
            {
                var variantList = new JsonArray();
                foreach (var variant in variants)
                {
                    var attributes = new JsonArray();
                    foreach (var attribute in variant.Attributes) attributes.Add(new JsonObject { ["definition"] = attribute.Definition, ["value"] = attribute.Value });
                    variantList.Add(new JsonObject { ["attributes"] = attributes, ["quantity"] = variant.Quantity, ["price"] = Price(variant.Price) });
                }
                entry["variants"] = variantList;
            }
            list.Add(entry);
        }
        return new JsonObject { ["items"] = list }.ToJsonString();
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
                var hasVariants = entry.TryGetProperty("variants", out var variantValue);
                var variants = hasVariants && variantValue.ValueKind == JsonValueKind.Array ? Variants(variantValue) : null;
                // PTT AVM refuses a price of 1 or less; a product without variants carries its stock, one with variants does not.
                if (string.IsNullOrEmpty(barcode) || barcode.Length > MaxBarcodeLength || price is not > 1 || vat is not null && VatRate(vat) is null) return null;
                if (hasVariants && variants is null) return null;
                if (variants is null ? quantity is not (>= 0 and <= MaxQuantity) : quantity is not null) return null;
                if (variants is not null && (variants.Sum(x => x.Quantity) > MaxQuantity || variants.Any(x => price.Value + x.Price <= 1))) return null;
                items.Add(new(barcode, Price(price.Value), vat, quantity, variants));
            }
            return items.Count is > 0 and <= MaxItemsPerRequest && items.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count ? items : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<VariantItem>? Variants(JsonElement list)
    {
        var variants = new List<VariantItem>();
        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("attributes", out var attributeList) || attributeList.ValueKind != JsonValueKind.Array) return null;
            var attributes = new List<VariantAttribute>();
            foreach (var attribute in attributeList.EnumerateArray())
            {
                var definition = attribute.TryGetProperty("definition", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()?.Trim() : null;
                var value = attribute.TryGetProperty("value", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(definition) || string.IsNullOrEmpty(value) || definition.Length > 100 || value.Length > 100) return null;
                attributes.Add(new(definition, value));
            }
            var quantity = entry.TryGetProperty("quantity", out var quantityValue) && quantityValue.TryGetInt32(out var parsedQuantity) ? parsedQuantity : (int?)null;
            var price = entry.TryGetProperty("price", out var priceValue) && priceValue.TryGetDecimal(out var parsedPrice) ? parsedPrice : (decimal?)null;
            if (attributes.Count == 0 || quantity is not (>= 0 and <= MaxQuantity) || price is null) return null;
            variants.Add(new(attributes, quantity.Value, Price(price.Value)));
        }
        var keys = variants.Select(x => string.Join("|", x.Attributes.Select(a => $"{a.Definition}={a.Value}".ToUpperInvariant()))).ToList();
        return variants.Count is > 0 and <= MaxVariantsPerProduct && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count ? variants : null;
    }

    // ProductStockPriceRequest fields in the data-contract (alphabetical) order of the service schema:
    // Barcode, PriceWithVAT, Quantity, VATRate, Variants{Variant{Attributes{Attribute{Definition, Value}}, Price, Quantity}}.
    internal static string ItemsXml(IReadOnlyList<Item> items)
    {
        var xml = new StringBuilder();
        foreach (var item in items)
        {
            xml.Append($"""<ept:ProductStockPriceRequest xmlns:ept="{PttAvmHttpClient.RequestsNamespace}">""");
            xml.Append("<ept:Barcode>").Append(SecurityElement.Escape(item.Barcode)).Append("</ept:Barcode>");
            xml.Append("<ept:PriceWithVAT>").Append(item.PriceWithVat.ToString("0.00", CultureInfo.InvariantCulture)).Append("</ept:PriceWithVAT>");
            if (item.Quantity is { } quantity) xml.Append("<ept:Quantity>").Append(quantity.ToString(CultureInfo.InvariantCulture)).Append("</ept:Quantity>");
            if (item.VatRate is { } rate) xml.Append("<ept:VATRate>").Append(rate.ToString("0", CultureInfo.InvariantCulture)).Append("</ept:VATRate>");
            if (item.Variants is { Count: > 0 } variants)
            {
                xml.Append("<ept:Variants>");
                foreach (var variant in variants)
                {
                    xml.Append("<ept:Variant><ept:Attributes>");
                    foreach (var attribute in variant.Attributes)
                        xml.Append("<ept:Attribute><ept:Definition>").Append(SecurityElement.Escape(attribute.Definition)).Append("</ept:Definition><ept:Value>").Append(SecurityElement.Escape(attribute.Value)).Append("</ept:Value></ept:Attribute>");
                    xml.Append("</ept:Attributes>");
                    xml.Append("<ept:Price>").Append(variant.Price.ToString("0.00", CultureInfo.InvariantCulture)).Append("</ept:Price>");
                    xml.Append("<ept:Quantity>").Append(variant.Quantity.ToString(CultureInfo.InvariantCulture)).Append("</ept:Quantity>");
                    xml.Append("</ept:Variant>");
                }
                xml.Append("</ept:Variants>");
            }
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
