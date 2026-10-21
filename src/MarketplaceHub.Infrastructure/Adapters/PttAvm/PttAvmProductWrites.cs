using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Product send (phase 7). UpdateProductsV3 upserts a product by its barcode, queues the work and answers a tracking id
// read later through GetProductsTrackingResult (the "t:" operations of the price/stock phase). A product sent
// without variants loses every variant it has on PTT AVM, so the panel sends only single-variant products (the
// composer checks the product's links) and, right before the send, reads the barcode on PTT AVM: a product PTT AVM
// lists with variants is never sent. Every send passes the adapter's write gate.
public sealed partial class PttAvmHttpClient
{
    internal const string ProductUpsert = "UpdateProductsV3";

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var draft = PttAvmProductPayloadRules.Read(publication.PayloadJson, out var invalid);
        if (draft is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRODUCT_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün içeriği geçersiz.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOperationRef>(load);
        if (WriteBlocked<RemoteOperationRef>(load.Account, "ürün") is { } blocked) return blocked;

        // The variant-deletion guard on PTT AVM's side: the barcode must not be a product PTT AVM lists with variants.
        var current = await ReadProductPageAsync(load.Account, null, draft.Barcode, cancellationToken);
        if (!current.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(current.Error!, current.RateLimit);
        if (PttAvmProductPayloadRules.RemoteVariantRisk(draft.Barcode, current.Value!.Items) is { } risk)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, "PTTAVM_VARIANT_DELETION_RISK", risk, HttpStatusCode.Conflict);

        var result = await SendSoapAsync(load.Account, ProductUpsert, [new("items", PttAvmProductPayloadRules.ItemsXml(draft), Raw: true)], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        var trackingId = ContractValue(result.Value!, "TrackingId");
        if (!string.Equals(ContractValue(result.Value!, "Success"), "true", StringComparison.OrdinalIgnoreCase) || trackingId is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRODUCT_REJECTED", $"PTT AVM ürünü kabul etmedi: {ContractValue(result.Value!, "Message") ?? "gerekçe verilmedi"}.", HttpStatusCode.UnprocessableEntity);
        return AdapterResult<RemoteOperationRef>.Success(new(TrackingOperationPrefix + trackingId, "PRODUCT_UPSERT", timeProvider.GetUtcNow()), result.RateLimit);
    }

    // The barcode on PTT AVM's product list: active is live, passive is still waiting, absent is not found yet.
    public async Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken)
    {
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemotePublicationStatus>(load);
        var code = barcode.Trim();
        var page = await ReadProductPageAsync(load.Account, null, code, cancellationToken);
        if (!page.IsSuccess) return AdapterResult<RemotePublicationStatus>.Failure(page.Error!, page.RateLimit);
        return AdapterResult<RemotePublicationStatus>.Success(PttAvmProductPayloadRules.PublicationStatus(code, page.Value!.Items), page.RateLimit);
    }
}

public static class PttAvmProductPayloadRules
{
    public const int MaxNameLength = 200;
    public const int MaxImages = 8;

    public sealed record ProductDraft(string Barcode, string Name, string LongDescription, string CategoryId, string ProductCode, decimal PriceWithVat, decimal VatRate, int Quantity, decimal Desi, IReadOnlyList<string> Images);

    public static string Payload(ProductDraft draft) => new JsonObject
    {
        ["barcode"] = draft.Barcode,
        ["name"] = draft.Name,
        ["longDescription"] = draft.LongDescription,
        ["categoryId"] = draft.CategoryId,
        ["productCode"] = draft.ProductCode,
        ["priceWithVat"] = PttAvmPriceInventoryRules.Price(draft.PriceWithVat),
        ["vatRate"] = draft.VatRate,
        ["quantity"] = draft.Quantity,
        ["desi"] = draft.Desi,
        ["images"] = new JsonArray([.. draft.Images.Select(url => (JsonNode)url)])
    }.ToJsonString();

    // Null (with the reason) when a field breaks PTT AVM's rules for a new product.
    public static ProductDraft? Read(string payloadJson, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            decimal? Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : null;
            var images = root.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null).OfType<string>().ToList()
                : [];
            var draft = new ProductDraft(Text("barcode") ?? "", Text("name") ?? "", Text("longDescription") ?? "", Text("categoryId") ?? "", Text("productCode") ?? "",
                Number("priceWithVat") ?? 0, Number("vatRate") ?? -1, (int)(Number("quantity") ?? -1), Number("desi") ?? 0, images);
            error = draft switch
            {
                { Barcode.Length: 0 or > PttAvmPriceInventoryRules.MaxBarcodeLength } => "Barkod boş veya çok uzun.",
                { Name.Length: 0 or > MaxNameLength } => $"Ürün adı 1-{MaxNameLength} karakter olmalıdır.",
                { CategoryId.Length: 0 } => "Kategori zorunludur.",
                { PriceWithVat: <= 1 } => "Satış fiyatı 1'den büyük olmalıdır.",
                { Quantity: < 0 or > PttAvmPriceInventoryRules.MaxQuantity } => "Stok 0-9999 arasında olmalıdır.",
                { Desi: <= 0 or > 300 } => "Desi 0'dan büyük ve en fazla 300 olmalıdır.",
                { Images.Count: 0 } => "En az bir görsel gerekir.",
                _ when PttAvmPriceInventoryRules.VatRate(draft.VatRate) is null => "KDV oranı 0, 1, 10 veya 20 olmalıdır.",
                _ when draft.Images.Count > MaxImages || draft.Images.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) => "Görseller en çok 8 adet https adresi olmalıdır.",
                _ when !long.TryParse(draft.CategoryId, NumberStyles.None, CultureInfo.InvariantCulture, out _) => "Kategori kimliği sayı olmalıdır.",
                _ => null
            };
            return error is null ? draft : null;
        }
        catch (JsonException)
        {
            error = "PTT AVM ürün içeriği okunamadı.";
            return null;
        }
    }

    // ProductV3Request in the data-contract (alphabetical) order of the documented sample. No Variants element: the
    // composer and the read before the send have made sure PTT AVM holds no variants under this barcode.
    internal static string ItemsXml(ProductDraft draft)
    {
        var xml = new StringBuilder($"""<ept:ProductV3Request xmlns:ept="{PttAvmHttpClient.RequestsNamespace}">""");
        void Field(string name, string value) => xml.Append("<ept:").Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</ept:").Append(name).Append('>');
        Field("Active", "true");
        Field("Barcode", draft.Barcode);
        Field("CategoryId", draft.CategoryId);
        Field("Desi", draft.Desi.ToString("0.##", CultureInfo.InvariantCulture));
        xml.Append("<ept:Images>");
        foreach (var url in draft.Images) xml.Append("<ept:ProductImageV3><ept:Url>").Append(SecurityElement.Escape(url)).Append("</ept:Url></ept:ProductImageV3>");
        xml.Append("</ept:Images>");
        Field("LongDescription", draft.LongDescription);
        Field("Name", draft.Name);
        Field("NoShippingProduct", "false");
        Field("PriceWithVat", draft.PriceWithVat.ToString("0.00", CultureInfo.InvariantCulture));
        Field("ProductCode", draft.ProductCode);
        Field("Quantity", draft.Quantity.ToString(CultureInfo.InvariantCulture));
        Field("VATRate", draft.VatRate.ToString("0", CultureInfo.InvariantCulture));
        return xml.Append("</ept:ProductV3Request>").ToString();
    }

    // The panel side of the guard: the product's links on PTT AVM must be the barcode being sent and nothing else.
    public static string? VariantDeletionRisk(string barcode, IReadOnlyCollection<string> linkedBarcodes)
    {
        var others = linkedBarcodes.Select(x => x.Trim()).Where(x => !string.Equals(x, barcode, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return others.Count == 0
            ? null
            : $"Bu ürün PTT AVM'de başka barkodlara da bağlı ({string.Join(", ", others.Take(5))}); varyantsız gönderim PTT AVM'deki varyantları silebileceği için gönderilmedi. Varyantlı ürünler PTT AVM satıcı panelinden güncellenmelidir.";
    }

    // PTT AVM's side of the guard: a product listed under this barcode must have no variants of its own (a variant
    // read from VariantListesi carries its options; the plain product carries none).
    public static string? RemoteVariantRisk(string barcode, IReadOnlyList<RemoteCatalogProduct> listed)
    {
        foreach (var product in listed)
        {
            var holdsBarcode = product.Variants.Any(x => string.Equals(x.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
            if (holdsBarcode && (product.Variants.Count > 1 || product.Variants[0].Options.Count > 0))
                return $"'{barcode}' PTT AVM'de varyantlı bir ürüne ait; varyantsız gönderim varyantları sileceği için ürün gönderilmedi. Bu ürünü PTT AVM satıcı panelinden güncelleyin.";
        }
        return null;
    }

    public static RemotePublicationStatus PublicationStatus(string barcode, IReadOnlyList<RemoteCatalogProduct> listed)
    {
        var match = listed.SelectMany(product => product.Variants.Select(variant => (product, variant)))
            .FirstOrDefault(x => string.Equals(x.variant.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
        if (match.product is null) return new(barcode, "NOT_FOUND", null, null, null, "{}");
        return new(barcode, match.variant.Archived ? "PENDING_APPROVAL" : "APPROVED", match.product.ExternalProductId, match.variant.Barcode, null, match.product.RawJson);
    }
}
