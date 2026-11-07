using System.Text.Json;

namespace MarketplaceHub.Application;

// One item's answer in a write job that is not about one product card: a price/stock line, a package action, a return
// decision, an invoice submission or delivery. Status is the stored code (the send log's results, or ACCEPTED,
// VERIFIED, UNRESOLVED, OPEN and the invoice tables' own states); nothing is inferred, a missing record is not listed.
public sealed record JobSentItemView(string Reference, string? Detail, string Status, string? Reason, DateTimeOffset? At = null);

// What the other write jobs carried to the marketplace or to E-Faturam, read only from the job's stored payload: one part
// per request with the field names its items held (never their values), how many items, and the request ids the job
// kept. The per-item answers are added from the job's own result tables by JobOperationsService.
public static class JobSentPayloadReader
{
    public static bool AppliesTo(string jobType) => jobType is MarketplaceJobTypes.PriceInventorySync or MarketplaceJobTypes.StockProjectionDispatch
        or MarketplaceJobTypes.ShipmentAction or MarketplaceJobTypes.ReturnAction or MarketplaceJobTypes.CommonLabel or MarketplaceJobTypes.StageTestOrder
        or InvoicingJobTypes.InvoiceSubmit or InvoicingJobTypes.InvoiceCancellation or InvoicingJobTypes.MarketplaceDelivery or InvoicingJobTypes.ManualOrderInvoiceDelivery;

    // Pazarama's sale open/close travels in a PRODUCT_ARCHIVE job; told apart by its barcodes.
    public static bool IsSaleStatus(string jobType, string payloadJson) =>
        jobType == MarketplaceJobTypes.ProductArchive && payloadJson.Contains("\"SaleStatusBarcodes\"", StringComparison.Ordinal);

    public static JobWriteSummaryView? Read(string jobType, string payloadJson)
    {
        if (!AppliesTo(jobType) && !IsSaleStatus(jobType, payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return jobType switch
            {
                MarketplaceJobTypes.PriceInventorySync => PriceStock(root),
                MarketplaceJobTypes.StockProjectionDispatch => Summary([], [], null,
                    "Bu iş pazaryerine istek göndermez: stok değişikliğini kontrol eder, gerekiyorsa ayrı bir Fiyat ve Stok gönderimi kuyruğa alır."),
                MarketplaceJobTypes.ShipmentAction => Shipment(root),
                MarketplaceJobTypes.ReturnAction => Summary([Fixed("RETURN_DECISION", Count(root, "returnLineIds"), ["decision", "returnLines"])], [], null, null),
                MarketplaceJobTypes.CommonLabel => Summary([Fixed("COMMON_LABEL", 1, ["boxQuantity", "volumetricHeight", "format"])], [], null, null),
                MarketplaceJobTypes.StageTestOrder => Summary([Fixed("STAGE_TEST_ORDER", 1, ["barcode"])], [], null, null),
                InvoicingJobTypes.InvoiceSubmit => Summary([Fixed("INVOICE_SUBMIT", 1, ["invoiceLines", "recipient", "totals"])], [], null,
                    "İstek gövdesi saklanmaz (alıcı bilgisi içerir); yalnız özeti tutulur. Sonuç her deneme için aşağıda."),
                InvoicingJobTypes.InvoiceCancellation => Summary([Fixed("INVOICE_CANCEL", 1, ["ettn"])], [], null, null),
                InvoicingJobTypes.MarketplaceDelivery => Summary([Fixed("INVOICE_DELIVERY", 1, ["invoiceLink", "invoiceNumber", "invoiceDateTime", "package"])], [], null, null),
                InvoicingJobTypes.ManualOrderInvoiceDelivery => Summary([Fixed("INVOICE_DELIVERY", 1, ["invoiceLink", "invoiceNumber", "package"])], [], null, null),
                _ => SaleStatus(root)
            };
        }
        catch (JsonException) { return null; }
    }

    private static JobWriteSummaryView PriceStock(JsonElement root)
    {
        var parts = new List<JobWritePartView>();
        var body = Text(root, "PayloadJson");
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var inner = JsonDocument.Parse(body);
                var request = inner.RootElement;
                // Pazarama sends prices and stocks in two requests.
                if (request.ValueKind == JsonValueKind.Object && (Property(request, "price") is { ValueKind: JsonValueKind.Array } || Property(request, "stock") is { ValueKind: JsonValueKind.Array }))
                {
                    foreach (var (key, name) in new[] { ("PRICE", "price"), ("STOCK", "stock") })
                        if (Property(request, name) is { ValueKind: JsonValueKind.Array } list)
                            if (JobWritePayloadReader.Part(key, list.GetRawText()) is { } listPart) parts.Add(listPart);
                }
                else if (JobWritePayloadReader.Part("PRICE_STOCK", body) is { } part) parts.Add(part);
            }
            catch (JsonException) { }
        }
        var requests = new List<JobWriteRequestView>();
        string? note = null;
        if (Text(root, "ExternalOperationId") is { } operation)
        {
            if (operation.StartsWith("shopify-completed:", StringComparison.Ordinal)) note = "Shopify isteği anında yanıtladı; ayrı bir istek numarası yok.";
            else requests.Add(new("PRICE_STOCK", OperationId(operation)));
        }
        return Summary(parts, requests, Text(root, "Trigger"), note);
    }

    // "LISTING_INVENTORY:123" (Hepsiburada) and "t:123" (PTT AVM) carry the marketplace's id after the prefix.
    private static string OperationId(string value)
    {
        if (value.StartsWith("LISTING_INVENTORY:", StringComparison.Ordinal)) return value["LISTING_INVENTORY:".Length..];
        if (value.StartsWith("t:", StringComparison.Ordinal)) return value[2..];
        return value;
    }

    private static readonly string[] ListProperties = ["lines", "orderLineIds", "items", "packages", "splitGroups"];

    private static JobWriteSummaryView Shipment(JsonElement root)
    {
        var action = (Text(root, "Action") ?? "UNKNOWN").ToUpperInvariant();
        var fields = new List<string>();
        var count = 1;
        if (Text(root, "PayloadJson") is { } body)
        {
            try
            {
                using var inner = JsonDocument.Parse(body);
                if (inner.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in inner.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any()) continue;
                        if (!fields.Contains(property.Name, StringComparer.Ordinal)) fields.Add(property.Name);
                    }
                    foreach (var name in ListProperties)
                        if (Property(inner.RootElement, name) is { ValueKind: JsonValueKind.Array } list) { count = list.GetArrayLength(); break; }
                }
            }
            catch (JsonException) { }
        }
        return Summary([new JobWritePartView($"SHIPMENT_{action}", count, fields, [], 0, [], [])], [], null, null);
    }

    private static JobWriteSummaryView SaleStatus(JsonElement root)
    {
        var barcodes = Property(root, "SaleStatusBarcodes") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
            : [];
        var close = Property(root, "Close") is { ValueKind: JsonValueKind.True };
        return Summary([new JobWritePartView(close ? "SALE_CLOSE" : "SALE_OPEN", barcodes.Count, ["barcode", "archived"], barcodes, 0, [], [])], [], null, null);
    }

    private static JobWritePartView Fixed(string key, int count, IReadOnlyList<string> fields) => new(key, count, fields, [], 0, [], []);

    private static int Count(JsonElement root, string name) => Property(root, name) is { ValueKind: JsonValueKind.Array } list ? list.GetArrayLength() : 1;

    private static JobWriteSummaryView Summary(IReadOnlyList<JobWritePartView> parts, IReadOnlyList<JobWriteRequestView> requests, string? trigger, string? note) =>
        new(null, [], null, null, parts, requests) { Note = note, Trigger = trigger };

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}

// Small readings the job detail makes of what a write job stored.
public static class JobSentRequestId
{
    // A marketplace or provider request id is shown when it is an id: short, without spaces or braces. Some failures keep
    // a provider's problem body in the same column; that is never shown.
    public static string? Displayable(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var value = id.Trim();
        if (value.Length > 100) return null;
        return value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.' or '/' or '|' or '=' or '!') ? value : null;
    }

    // "Stok 4 · Satış 699,00 TRY · Liste 799,00 TRY"; a value left out of the request is not listed.
    public static string? PriceStockValues(decimal? quantity, decimal? salePrice, decimal? listPrice, string currency)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var parts = new List<string>();
        if (quantity is { } stock) parts.Add($"Stok {stock.ToString("0.##", culture)}");
        if (salePrice is { } sale) parts.Add($"Satış {sale.ToString("N2", culture)} {currency}");
        if (listPrice is { } list) parts.Add($"Liste {list.ToString("N2", culture)} {currency}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // A package action's or label's external-effect record: the marketplace accepted the call (CompletedAt), a later read
    // confirmed it (APPLIED) or could not (UNRESOLVED); open while the call's answer is not settled.
    public static string EffectStatus(string? outcome, DateTimeOffset? completedAt) => outcome switch
    {
        "APPLIED" => "VERIFIED",
        "UNRESOLVED" => "UNRESOLVED",
        _ => completedAt is null ? "OPEN" : "ACCEPTED"
    };

    public static string DecisionLabel(string action) => action.ToUpperInvariant() switch
    {
        "APPROVE" => "Onay",
        "REJECT" => "Ret",
        "PREAPPROVAL_CONFIRM" => "Ön Onay Teyidi",
        _ => action
    };
}
