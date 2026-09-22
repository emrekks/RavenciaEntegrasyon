using System.Globalization;
using System.Text;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ShopifyOrderCsvAddress(
    string? Name,
    string? Street,
    string? Address1,
    string? Address2,
    string? Company,
    string? City,
    string? Zip,
    string? Province,
    string? Country,
    string? Phone);

public sealed record ShopifyOrderCsvLine(string? Sku, string Title, decimal? UnitPrice);

public sealed record ShopifyOrderCsvOrder(
    string OrderNumber,
    string? Currency,
    decimal? Subtotal,
    decimal? Shipping,
    decimal? Taxes,
    decimal? Total,
    decimal? DiscountAmount,
    string? Email,
    string? Phone,
    string? FinancialStatus,
    string? FulfillmentStatus,
    string? PaymentMethod,
    ShopifyOrderCsvAddress BillingAddress,
    ShopifyOrderCsvAddress ShippingAddress,
    IReadOnlyList<ShopifyOrderCsvLine> Lines,
    string? ValidationIssue = null);

public sealed record ShopifyOrderCsvParseResult(int FileRows, int SkippedRows, IReadOnlyList<ShopifyOrderCsvOrder> Orders);

public static class ShopifyOrderCsvParser
{
    private const int MaximumRows = 50_000;
    private const int MaximumOrders = 10_000;
    private const int MaximumCellCharacters = 65_536;
    private static readonly string[] RequiredHeaders = ["Name", "Lineitem name"];

    public static ShopifyOrderCsvParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var records = ReadRecords(reader);
        if (records.Count < 2) throw new FormatException("Shopify CSV dosyasında başlık ve en az bir sipariş satırı bulunmalı.");

        var headerColumns = records[0].Select((value, index) => (Name: value.Trim().TrimStart('\uFEFF'), Index: index))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
        var duplicateHeader = headerColumns.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateHeader is not null) throw new FormatException($"CSV dosyasında '{duplicateHeader.Key}' başlığı birden fazla kez kullanılmış.");
        var headers = headerColumns.ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        foreach (var required in RequiredHeaders)
            if (!headers.ContainsKey(required)) throw new FormatException($"Shopify CSV dosyasında '{required}' sütunu bulunamadı.");

        if (records.Count - 1 > MaximumRows) throw new FormatException("Shopify CSV dosyası tek seferde en fazla 50.000 satır içerebilir.");

        var grouped = new Dictionary<string, OrderAccumulator>(StringComparer.OrdinalIgnoreCase);
        var skippedRows = 0;
        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var record = records[rowIndex];
            if (record.All(string.IsNullOrWhiteSpace)) continue;
            if (record.Length > records[0].Length && record.Skip(records[0].Length).Any(value => !string.IsNullOrWhiteSpace(value)))
                throw new FormatException($"CSV {rowIndex + 1}. satırında başlıklarla eşleşmeyen ek alan var.");

            string Get(string name)
            {
                if (!headers.TryGetValue(name, out var index) || index >= record.Length) return "";
                return record[index].Trim();
            }

            var orderNumber = Get("Name");
            if (string.IsNullOrWhiteSpace(orderNumber)) { skippedRows++; continue; }
            var normalized = NormalizeOrderNumber(orderNumber);
            if (normalized.Length == 0) { skippedRows++; continue; }
            if (!grouped.TryGetValue(normalized, out var order))
            {
                order = new OrderAccumulator(orderNumber.Trim());
                grouped.Add(normalized, order);
            }

            order.SetText("Currency", Get("Currency"));
            order.SetText("Email", Get("Email"));
            order.SetText("Phone", Get("Phone"));
            order.SetText("Financial Status", Get("Financial Status"));
            order.SetText("Fulfillment Status", Get("Fulfillment Status"));
            order.SetText("Payment Method", Get("Payment Method"));
            order.SetAmount("Subtotal", Get("Subtotal"));
            order.SetAmount("Shipping", Get("Shipping"));
            order.SetAmount("Taxes", Get("Taxes"));
            order.SetAmount("Total", Get("Total"));
            order.SetAmount("Discount Amount", Get("Discount Amount"));
            order.SetAddress("Billing", Address(Get, "Billing"));
            order.SetAddress("Shipping", Address(Get, "Shipping"));

            var title = Get("Lineitem name");
            if (title.Length == 0) continue;
            var sku = NullIfEmpty(Get("Lineitem sku"));
            var price = ParseOptionalAmount(Get("Lineitem price"), order, "Lineitem price");
            order.Lines.Add(new(sku, title, price));
        }

        if (grouped.Count > MaximumOrders) throw new FormatException("Shopify CSV dosyası tek seferde en fazla 10.000 sipariş içerebilir.");
        var orders = grouped.Values.Select(x => x.ToOrder()).ToArray();
        return new(records.Count - 1, skippedRows, orders);
    }

    public static string NormalizeOrderNumber(string? value)
    {
        var normalized = (value ?? "").Trim().TrimStart('#').Trim();
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character)));
    }

    private static ShopifyOrderCsvAddress Address(Func<string, string> get, string prefix)
    {
        var fullName = NullIfEmpty(get($"{prefix} Name"));
        var address1 = NullIfEmpty(get($"{prefix} Address1"));
        var address2 = NullIfEmpty(get($"{prefix} Address2"));
        var street = NullIfEmpty(get($"{prefix} Street"));
        var company = NullIfEmpty(get($"{prefix} Company"));
        var city = NullIfEmpty(get($"{prefix} City"));
        var zip = NullIfEmpty(get($"{prefix} Zip"));
        var province = NullIfEmpty(get($"{prefix} Province Name")) ?? NullIfEmpty(get($"{prefix} Province"));
        var country = NullIfEmpty(get($"{prefix} Country"));
        var phone = NullIfEmpty(get($"{prefix} Phone"));
        return new(fullName, street, address1, address2, company, city, zip, province, country, phone);
    }

    private static decimal? ParseOptionalAmount(string value, OrderAccumulator order, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var amount))
        {
            order.AddIssue($"'{field}' alanında geçersiz tutar var.");
            return null;
        }
        return amount;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string[]> ReadRecords(TextReader reader)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var atCellStart = true;
        while (true)
        {
            var code = reader.Read();
            if (code < 0) break;
            var character = (char)code;
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); Append('"'); }
                    else inQuotes = false;
                }
                else Append(character);
                continue;
            }

            if (character == '"' && atCellStart) { inQuotes = true; atCellStart = false; continue; }
            if (character == ',') { EndCell(); continue; }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                EndRecord();
                continue;
            }
            Append(character);
        }
        if (inQuotes) throw new FormatException("CSV dosyasında kapanmamış tırnaklı alan var.");
        if (record.Count > 0 || cell.Length > 0) EndRecord();
        return records;

        void Append(char value)
        {
            if (cell.Length >= MaximumCellCharacters) throw new FormatException("CSV alanı izin verilen uzunluğu aşıyor.");
            cell.Append(value);
            atCellStart = false;
        }

        void EndCell()
        {
            record.Add(cell.ToString());
            cell.Clear();
            atCellStart = true;
        }

        void EndRecord()
        {
            EndCell();
            records.Add(record.ToArray());
            if (records.Count > MaximumRows + 1) throw new FormatException("Shopify CSV dosyası tek seferde en fazla 50.000 satır içerebilir.");
            record.Clear();
        }
    }

    private sealed class OrderAccumulator(string orderNumber)
    {
        private readonly Dictionary<string, string> text = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal?> amounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ShopifyOrderCsvAddress> addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> issues = [];
        public List<ShopifyOrderCsvLine> Lines { get; } = [];

        public void SetText(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (text.TryGetValue(field, out var existing) && !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                if (field.Equals("Currency", StringComparison.OrdinalIgnoreCase)) AddIssue("Para birimi sipariş satırlarında tutarsız.");
                return;
            }
            text.TryAdd(field, value);
        }

        public void SetAmount(string field, string value)
        {
            var parsed = ParseOptionalAmount(value, this, field);
            if (parsed is null) return;
            if (amounts.TryGetValue(field, out var existing) && existing != parsed)
            {
                AddIssue("Sipariş tutarları satırlarda tutarsız.");
                return;
            }
            amounts.TryAdd(field, parsed);
        }

        public void SetAddress(string key, ShopifyOrderCsvAddress address)
        {
            if (!addresses.TryGetValue(key, out var existing)) { addresses.Add(key, address); return; }
            addresses[key] = new(
                existing.Name ?? address.Name, existing.Street ?? address.Street, existing.Address1 ?? address.Address1,
                existing.Address2 ?? address.Address2, existing.Company ?? address.Company, existing.City ?? address.City,
                existing.Zip ?? address.Zip, existing.Province ?? address.Province, existing.Country ?? address.Country,
                existing.Phone ?? address.Phone);
        }

        public void AddIssue(string issue) { if (!issues.Contains(issue, StringComparer.Ordinal)) issues.Add(issue); }

        public ShopifyOrderCsvOrder ToOrder() => new(
            orderNumber,
            text.GetValueOrDefault("Currency"),
            amounts.GetValueOrDefault("Subtotal"), amounts.GetValueOrDefault("Shipping"), amounts.GetValueOrDefault("Taxes"),
            amounts.GetValueOrDefault("Total"), amounts.GetValueOrDefault("Discount Amount"),
            text.GetValueOrDefault("Email"), text.GetValueOrDefault("Phone"), text.GetValueOrDefault("Financial Status"),
            text.GetValueOrDefault("Fulfillment Status"), text.GetValueOrDefault("Payment Method"),
            addresses.GetValueOrDefault("Billing") ?? new(null, null, null, null, null, null, null, null, null, null),
            addresses.GetValueOrDefault("Shipping") ?? new(null, null, null, null, null, null, null, null, null, null),
            Lines, issues.Count == 0 ? null : string.Join(" ", issues));
    }
}
