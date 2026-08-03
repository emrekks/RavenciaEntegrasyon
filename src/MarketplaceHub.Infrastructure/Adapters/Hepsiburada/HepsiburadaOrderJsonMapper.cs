using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

/// <summary>Maps only fields documented by the verified v1.0 order-list contract; incomplete rows are rejected as a whole.</summary>
public static class HepsiburadaOrderJsonMapper
{
    public static AdapterPageResult<RemoteOrder> Orders(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!HepsiburadaSitEnvelope.TryValidate(System.Text.Encoding.UTF8.GetBytes(json), out _)) throw new JsonException("Invalid order envelope.");
        var groups = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var orderNumber = RequiredText(item, "orderNumber");
            if (!groups.TryGetValue(orderNumber, out var lines)) groups[orderNumber] = lines = [];
            lines.Add(item.Clone());
        }

        var orders = new List<RemoteOrder>();
        foreach (var (orderNumber, items) in groups)
        {
            var first = items[0];
            var externalOrderId = RequiredText(first, "orderId");
            var orderedAt = RequiredInstant(first, "orderDate");
            var customerName = RequiredText(first, "customerName");
            var shippingAddress = RequiredObject(first, "shippingAddress");
            var lines = new List<RemoteOrderLine>();
            decimal gross = 0;
            string? currency = null;
            foreach (var item in items)
            {
                if (!string.Equals(externalOrderId, RequiredText(item, "orderId"), StringComparison.Ordinal) || !string.Equals(orderNumber, RequiredText(item, "orderNumber"), StringComparison.Ordinal)) throw new JsonException("Order group invariant failed.");
                var unitPrice = RequiredMoney(item, "unitPrice");
                var totalPrice = RequiredMoney(item, "totalPrice");
                if (!string.Equals(currency ?? unitPrice.Currency, unitPrice.Currency, StringComparison.Ordinal) || !string.Equals(unitPrice.Currency, totalPrice.Currency, StringComparison.Ordinal)) throw new JsonException("Currency invariant failed.");
                currency ??= unitPrice.Currency;
                var quantity = RequiredDecimal(item, "quantity");
                if (quantity <= 0 || totalPrice.Amount < 0 || unitPrice.Amount < 0) throw new JsonException("Invalid amount or quantity.");
                gross += totalPrice.Amount;
                lines.Add(new(RequiredText(item, "id"), RequiredText(item, "merchantSku", "merchantSKU"), null, RequiredText(item, "name"), quantity, unitPrice.Amount, RequiredDecimal(item, "vatRate"), RequiredText(item, "status")));
            }
            orders.Add(new(externalOrderId, orderNumber, orderedAt, orderedAt, currency ?? throw new JsonException("Currency missing."), gross, 0, gross,
                JsonSerializer.Serialize(new { customerName }), shippingAddress.GetRawText(), "{}", lines, [], JsonSerializer.Serialize(items)));
        }

        var offset = RequiredInt(root, "offset");
        var limit = RequiredInt(root, "limit");
        var total = RequiredInt(root, "totalCount");
        var nextOffset = offset + root.GetProperty("items").GetArrayLength();
        var hasMore = nextOffset < total;
        return new(orders, hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null, hasMore);
    }

    private static string RequiredText(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.String or JsonValueKind.Number && !string.IsNullOrWhiteSpace(property.ToString()))
                return property.ToString();
        throw new JsonException($"Required {string.Join("/", names)} missing.");
    }
    private static JsonElement RequiredObject(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object ? property : throw new JsonException($"Required {name} missing.");
    private static decimal RequiredDecimal(JsonElement value, string name) => value.TryGetProperty(name, out var property) && (property.TryGetDecimal(out var amount) || decimal.TryParse(property.ToString(), CultureInfo.InvariantCulture, out amount)) ? amount : throw new JsonException($"Required {name} missing.");
    private static int RequiredInt(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : throw new JsonException($"Required {name} missing.");
    private static DateTimeOffset RequiredInstant(JsonElement value, string name) => value.TryGetProperty(name, out var property) && DateTimeOffset.TryParse(property.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : throw new JsonException($"Required {name} missing.");
    private static Money RequiredMoney(JsonElement value, string name)
    {
        var money = RequiredObject(value, name);
        return new(RequiredDecimal(money, "amount"), RequiredText(money, "currency"));
    }
    private sealed record Money(decimal Amount, string Currency);
}
