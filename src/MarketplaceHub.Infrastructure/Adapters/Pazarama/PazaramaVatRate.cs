using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// VAT rate update (Ürün KDV Oranı Güncelleme): PUT product/vatRate/bulk with {"listingVatRates":[{productCode, vatRate}]},
// productCode being the barcode. It is the one product field Pazarama lets an integration change after the create, and it
// answers at once ({"data":true,"success":true,...}), so the result travels in a completed operation id ("c:").
public sealed partial class PazaramaHttpClient
{
    internal const string VatRateBulk = "product/vatRate/bulk";

    // The product update's variant request carries {"items":[{productCode, vatRate}]} (PazaramaProductUpdateComposer).
    public async Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken)
    {
        var rates = PazaramaVatRateRules.Read(publication?.ApprovedVariantPayloadJson);
        if (rates is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PAZARAMA_VAT_RATE_PAYLOAD_INVALID", $"Pazarama KDV oranı içeriği beklenen yapıda değil (1-{PazaramaVatRateRules.MaxItemsPerRequest} barkod; KDV 0, 1, 10 veya 20).", HttpStatusCode.UnprocessableEntity);
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<RemoteOperationRef>();
        if (WriteBlocked<RemoteOperationRef>(account, "KDV oranı güncellemesi") is { } blocked) return blocked;

        var result = await SendAsync(account, HttpMethod.Put, VatRateBulk, cancellationToken, Json(PazaramaVatRateRules.Body(rates)));
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        var lines = PazaramaVatRateRules.Lines(document.RootElement, rates);
        return AdapterResult<RemoteOperationRef>.Success(new(PazaramaOperationId.Completed(lines), "VAT_RATE_UPDATE", timeProvider.GetUtcNow()), result.RateLimit);
    }
}

public static class PazaramaVatRateRules
{
    // The document names no per-request limit; one product's variants stay well under this.
    public const int MaxItemsPerRequest = 1000;
    public static readonly IReadOnlySet<int> ValidRates = new HashSet<int> { 0, 1, 10, 20 };

    public static string Payload(IEnumerable<(string Barcode, int VatRate)> rates) =>
        new JsonObject { ["items"] = new JsonArray(rates.Select(rate => (JsonNode)new JsonObject { ["productCode"] = rate.Barcode, ["vatRate"] = rate.VatRate }).ToArray()) }.ToJsonString();

    // The barcodes and rates of a payload, or null when Pazarama could not take it.
    public static IReadOnlyList<(string Barcode, int VatRate)>? Read(string? payloadJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payloadJson) || JsonNode.Parse(payloadJson) is not JsonObject root || root["items"] is not JsonArray { Count: >= 1 and <= MaxItemsPerRequest } items) return null;
            var rates = new List<(string, int)>(items.Count);
            foreach (var node in items)
            {
                if (node is not JsonObject item || item["productCode"] is not JsonValue code || !code.TryGetValue<string>(out var barcode) || string.IsNullOrWhiteSpace(barcode) || barcode.Trim().Length > PazaramaPriceInventoryRules.MaxCodeLength
                    || item["vatRate"] is not JsonValue rate || !rate.TryGetValue<int>(out var vatRate) || !ValidRates.Contains(vatRate)) return null;
                rates.Add((barcode.Trim(), vatRate));
            }
            return rates.Select(x => x.Item1).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rates.Count ? rates : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static object Body(IReadOnlyList<(string Barcode, int VatRate)> rates) =>
        new { listingVatRates = rates.Select(rate => new { productCode = rate.Barcode, vatRate = rate.VatRate }).ToArray() };

    // A refused request comes back as an error before this (success:false); data:true applied every barcode. Any other
    // answer is not taken as applied: the barcodes stay unconfirmed instead of being reported as updated.
    public static IReadOnlyList<RemoteOperationLine> Lines(JsonElement root, IReadOnlyList<(string Barcode, int VatRate)> rates)
    {
        var applied = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.True;
        return rates.Select(rate => applied
            ? new RemoteOperationLine(rate.Barcode, true, null, null, false, "KDV oranı güncellendi.")
            : new RemoteOperationLine(rate.Barcode, false, null, "PAZARAMA_VAT_RATE_UNCONFIRMED", false, "Pazarama yanıtı KDV oranının uygulandığını bildirmedi.")).ToList();
    }
}
