using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Product send (phase 7). UpdateProductsV3 upserts a product by its barcode, queues the work and answers a tracking id
// read later through GetProductsTrackingResult (the "t:" operations of the price/stock phase). A product sent
// without variants loses every variant it has on PTT AVM, so a single-variant product is sent only when the barcode
// is not a product PTT AVM lists with variants. A product with variants is the panel's model family under the model
// code, every variant of every colour card in one request; PTT AVM deletes the variants a request leaves out, so
// right before the send the family's variants on PTT AVM are read again and the request carries every one of them as
// PTT AVM holds it, plus the panel's variants PTT AVM lacks. Nothing to add ends the job without a write. The request
// is logged before it goes; PttAvmProductPayloadRules.VariantSendDryRun turns the send back into a dry run. Every
// send passes the adapter's write gate.
public sealed partial class PttAvmHttpClient
{
    internal const string ProductUpsert = "UpdateProductsV3";

    public async Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken)
    {
        var draft = PttAvmProductPayloadRules.Read(publication.PayloadJson, out var invalid);
        if (draft is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRODUCT_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün içeriği geçersiz.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOperationRef>(load);
        if (draft.Variants is { Count: > 0 }) return await CreateVariantProductAsync(load.Account, draft, cancellationToken);
        if (WriteBlocked<RemoteOperationRef>(load.Account, "ürün") is { } blocked) return blocked;

        // The variant-deletion guard on PTT AVM's side: the barcode must not be a product PTT AVM lists with variants.
        var current = await ReadProductPageAsync(load.Account, null, draft.Barcode, cancellationToken);
        if (!current.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(current.Error!, current.RateLimit);
        if (PttAvmProductPayloadRules.RemoteVariantRisk(draft.Barcode, current.Value!.Items) is { } risk)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, "PTTAVM_VARIANT_DELETION_RISK", risk, HttpStatusCode.Conflict);

        return await SendProductAsync(load.Account, draft, cancellationToken);
    }

    // A model family with its variants. The variant list is read fresh (not from the scan's cache), then the parent
    // barcode's product. The request carries every variant PTT AVM holds under it unchanged, so the upsert deletes
    // nothing, plus the panel's variants PTT AVM lacks; an entry that cannot be read or carried stops the send
    // (unknown is not "absent"), and a family PTT AVM already holds whole is not sent at all.
    internal async Task<AdapterResult<RemoteOperationRef>> CreateVariantProductAsync(PttAvmRequestContext account, PttAvmProductPayloadRules.ProductDraft draft, CancellationToken cancellationToken)
    {
        var variantList = await VariantListAsync(account, refresh: true, cancellationToken);
        if (!variantList.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(variantList.Error!, variantList.RateLimit);
        var current = await ReadProductPageAsync(account, null, draft.Barcode, cancellationToken);
        if (!current.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(current.Error!, current.RateLimit);
        if ((current.Value!.Issues ?? []).Any(issue => issue.Code == "PTTAVM_VARIANT_LIST_UNAVAILABLE"
                || issue.Code == "PRODUCT_VARIANT_INVALID" && issue.Identity.StartsWith(draft.Barcode + ":", StringComparison.OrdinalIgnoreCase)))
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantUnreadableCode, $"PTT AVM'de '{draft.Barcode}' ürününün varyantlarının tamamı okunamadı; silinecek varyant olup olmadığı bilinmediği için istek gönderilmedi.", HttpStatusCode.Conflict);
        var remote = PttAvmProductPayloadRules.RemoteVariants(draft.Barcode, current.Value.Items);
        var merge = PttAvmProductPayloadRules.MergeVariants(draft.Variants!, remote);
        if (merge.Unreadable.Count > 0)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantUnreadableCode, PttAvmProductPayloadRules.RemoteVariantUnreadableMessage(merge.Unreadable), HttpStatusCode.Conflict);
        if (merge.Added.Count == 0)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.VariantsAlreadyPresentCode, PttAvmProductPayloadRules.VariantsAlreadyPresentMessage, HttpStatusCode.Conflict);
        // Checked after "nothing to add": with nothing to send, nothing on PTT AVM can be changed either.
        if (merge.NotCarriable.Count > 0)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantNotCarriableCode, PttAvmProductPayloadRules.RemoteVariantNotCarriableMessage(merge.NotCarriable), HttpStatusCode.Conflict);
        if (PttAvmProductPayloadRules.MergeRefusal(merge.Variants) is { } refusal)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, refusal.Code, refusal.Message, HttpStatusCode.UnprocessableEntity);
        var request = draft with { Variants = merge.Variants, Quantity = merge.Variants.Sum(x => x.Quantity) };

        // The request as it goes (or would go), for the operator to read in the worker log.
        logger.LogInformation("PTT AVM variant product request. DryRun: {DryRun}, ConnectionId: {ConnectionId}, Barcode: {Barcode}, Variants: {Variants}, RemoteVariants: {RemoteVariants}, Added: {Added}, Operation: {Operation}, Items: {Items}",
            PttAvmProductPayloadRules.VariantSendDryRun, account.Connection.Id, draft.Barcode, merge.Variants.Count, remote.Count, merge.Added.Count, ProductUpsert, PttAvmProductPayloadRules.ItemsXml(request));
        if (PttAvmProductPayloadRules.VariantSendDryRun)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, PttAvmProductPayloadRules.DryRunCode, PttAvmProductPayloadRules.DryRunMessage(request), HttpStatusCode.UnprocessableEntity);
        if (WriteBlocked<RemoteOperationRef>(account, "ürün") is { } blocked) return blocked;
        return await SendProductAsync(account, request, cancellationToken);
    }

    private async Task<AdapterResult<RemoteOperationRef>> SendProductAsync(PttAvmRequestContext account, PttAvmProductPayloadRules.ProductDraft draft, CancellationToken cancellationToken)
    {
        var result = await SendSoapAsync(account, ProductUpsert, [new("items", PttAvmProductPayloadRules.ItemsXml(draft), Raw: true)], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(result.Error!, result.RateLimit);
        var trackingId = ContractValue(result.Value!, "TrackingId");
        if (!string.Equals(ContractValue(result.Value!, "Success"), "true", StringComparison.OrdinalIgnoreCase) || trackingId is null)
        {
            var message = ContractValue(result.Value!, "Message");
            // "Aynı isteği ancak 5 dakika sonra tekrardan gönderebilirsiniz": a wait, not a refusal of the product.
            if (PttAvmProductPayloadRules.IsRepeatRequestRefusal(message))
                return AdapterResult<RemoteOperationRef>.Failure(PttAvmProductPayloadRules.RepeatRequestError((int)HttpStatusCode.TooManyRequests), new RateLimitMetadata(null, null, PttAvmProductPayloadRules.RepeatRequestWait));
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRODUCT_REJECTED", $"PTT AVM ürünü kabul etmedi: {message ?? "gerekçe verilmedi"}.", HttpStatusCode.UnprocessableEntity);
        }
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

    public const int MaxVariantValueLength = 100;
    public const string RemoteVariantUnreadableCode = "PTTAVM_REMOTE_VARIANT_UNREADABLE";
    public const string VariantsAlreadyPresentCode = "PTTAVM_VARIANTS_ALREADY_PRESENT";
    public const string VariantsAlreadyPresentMessage = "PTT AVM'de bu modelin bütün varyantları zaten var; gönderim yapılmadı.";
    public const string DryRunCode = "PTTAVM_VARIANT_SEND_DRY_RUN";

    // The one switch of the variant product send: while it is true the merged request is built, checked against
    // PTT AVM's variants and logged, and never sent. It is off since the request carries every variant PTT AVM holds
    // (nothing can be deleted); turning it on again stops the send without touching the rest
    // (docs/PTTAVM-ENTEGRASYON-PLANI.md §6.1, phase 7).
    public static readonly bool VariantSendDryRun = false;

    // Variants is null for a product without variants. A model family's variants each carry their barcode, their
    // colour and size (PTT AVM's attributes "Renk" and "Beden") and their stock; the price is the product's.
    public sealed record ProductDraft(string Barcode, string Name, string LongDescription, string CategoryId, string ProductCode, decimal PriceWithVat, decimal VatRate, int Quantity, decimal Desi, IReadOnlyList<string> Images, IReadOnlyList<VariantDraft>? Variants = null);

    public sealed record VariantDraft(string Barcode, string Color, string Size, int Quantity);

    // A variant PTT AVM holds: its barcode, or else only its name ("Lacivert / L") with GetProductsWithVariants, and
    // its stock as PTT AVM reports it. Price is its price field (a difference to the product price unless
    // PriceIsDifference is false) and Active its own active flag; null when PTT AVM did not say.
    public sealed record RemoteVariant(string? Barcode, string? Color, string? Size, string? Name, decimal? Quantity = null, decimal? Price = null, bool? PriceIsDifference = null, bool? Active = null);

    // A PTT AVM variant that reads fine but carries something the request cannot repeat, with the reason.
    public sealed record NotCarriableVariant(RemoteVariant Variant, string Reason);

    // The request's variants (PTT AVM's own first, then the panel's it lacks), the panel variants added, the PTT AVM
    // variants that cannot be read and those whose price or passive state the request would lose.
    public sealed record VariantMerge(IReadOnlyList<VariantDraft> Variants, IReadOnlyList<VariantDraft> Added, IReadOnlyList<RemoteVariant> Unreadable, IReadOnlyList<NotCarriableVariant> NotCarriable);

    public const string RemoteVariantNotCarriableCode = "PTTAVM_REMOTE_VARIANT_NOT_CARRIABLE";

    // PTT AVM takes one request only once in five minutes and answers a repeat with "Aynı isteği ancak 5 dakika sonra
    // tekrardan gönderebilirsiniz" (as Success=false or as a fault). Nothing was refused: the request did not run, and
    // it may go again once the five minutes are over (six, so the clocks need not agree to the second).
    public const string RepeatRequestWaitCode = "PTTAVM_REPEAT_REQUEST_WAIT";
    public static readonly TimeSpan RepeatRequestWait = TimeSpan.FromMinutes(6);

    public static bool IsRepeatRequestRefusal(string? message)
    {
        var folded = PttAvmOrderMapper.Fold(message?.Replace('İ', 'I'));
        return folded.Contains("ayni iste", StringComparison.Ordinal) && folded.Contains("dakika", StringComparison.Ordinal);
    }

    public static AdapterError RepeatRequestError(int? status) => new(AdapterErrorClass.RateLimit, RepeatRequestWaitCode,
        "PTT AVM aynı isteği beş dakika dolmadan yeniden almıyor; istek işlenmedi ve 6 dakika sonra yeniden denenecek.", status, RepeatRequestWait, null);

    public static string Payload(ProductDraft draft)
    {
        var payload = new JsonObject
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
        };
        if (draft.Variants is { Count: > 0 } variants)
            payload["variants"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject
            {
                ["barcode"] = variant.Barcode,
                ["color"] = variant.Color,
                ["size"] = variant.Size,
                ["quantity"] = variant.Quantity
            })]);
        return payload.ToJsonString();
    }

    public static bool IsVariantPayload(string payloadJson) => Read(payloadJson, out _)?.Variants is { Count: > 0 };

    // The same parent barcode and the same variant barcodes: prices and stock may have moved since, the variant set has not.
    public static bool SameVariantBarcodes(string queuedPayloadJson, string currentPayloadJson)
    {
        var queued = Read(queuedPayloadJson, out _);
        var current = Read(currentPayloadJson, out _);
        if (queued is null || current is null || !string.Equals(queued.Barcode, current.Barcode, StringComparison.OrdinalIgnoreCase)) return false;
        static string[] Barcodes(ProductDraft draft) => (draft.Variants ?? []).Select(x => x.Barcode.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray();
        return Barcodes(queued).SequenceEqual(Barcodes(current), StringComparer.Ordinal);
    }

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
            var hasVariants = root.TryGetProperty("variants", out var variantList);
            var variants = hasVariants && variantList.ValueKind == JsonValueKind.Array ? ReadVariants(variantList) : null;
            var draft = new ProductDraft(Text("barcode") ?? "", Text("name") ?? "", Text("longDescription") ?? "", Text("categoryId") ?? "", Text("productCode") ?? "",
                Number("priceWithVat") ?? 0, Number("vatRate") ?? -1, (int)(Number("quantity") ?? -1), Number("desi") ?? 0, images, variants);
            error = draft switch
            {
                _ when hasVariants && variants is null => $"Varyantların her biri barkod, renk, beden ve 0-9999 arası stok taşımalı; barkodlar ve renk/beden çiftleri tekrarlanmamalı, en fazla {PttAvmPriceInventoryRules.MaxVariantsPerProduct} varyant olmalıdır.",
                _ when variants is not null && variants.Any(x => string.Equals(x.Barcode, draft.Barcode, StringComparison.OrdinalIgnoreCase)) => "Varyant barkodu ürün barkoduyla aynı olamaz.",
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

    private static List<VariantDraft>? ReadVariants(JsonElement list)
    {
        var variants = new List<VariantDraft>();
        foreach (var entry in list.EnumerateArray())
        {
            string? Text(string name) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            var quantity = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("quantity", out var number) && number.TryGetInt32(out var parsed) ? parsed : (int?)null;
            var (barcode, color, size) = (Text("barcode"), Text("color"), Text("size"));
            if (string.IsNullOrEmpty(barcode) || barcode.Length > PttAvmPriceInventoryRules.MaxBarcodeLength
                || string.IsNullOrEmpty(color) || color.Length > MaxVariantValueLength || string.IsNullOrEmpty(size) || size.Length > MaxVariantValueLength
                || quantity is not (>= 0 and <= PttAvmPriceInventoryRules.MaxQuantity)) return null;
            variants.Add(new(barcode, color, size, quantity.Value));
        }
        var combinations = variants.Select(x => VariantLabel(x.Color, x.Size)).Distinct(StringComparer.Ordinal).Count();
        return variants.Count is > 0 and <= PttAvmPriceInventoryRules.MaxVariantsPerProduct
            && variants.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == variants.Count
            && combinations == variants.Count
            ? variants
            : null;
    }

    // ProductV3Request in the data-contract (alphabetical) order of the documented sample. A product without variants
    // has no Variants element: the composer and the read before the send have made sure PTT AVM holds no variants
    // under its barcode. A model family's Variants follow VATRate (ordinal order puts "VATRate" before "Variants").
    // UNVERIFIED: the repo holds no ProductV3Request sample with variants. The shape follows the service schema's
    // write-side Variant (VariantBarkod, Miktar, Attributes{VariantAttr{Tanim, Deger}}), members in alphabetical
    // order, without Combination and without a price difference (the schema's Variant has no price; the family's
    // variants share the product price). The first real send is the check; VariantSendDryRun stops it again.
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
        if (draft.Variants is { Count: > 0 } variants)
        {
            xml.Append("<ept:Variants>");
            foreach (var variant in variants)
            {
                xml.Append("<ept:Variant><ept:Attributes>");
                foreach (var (definition, value) in new[] { ("Renk", variant.Color), ("Beden", variant.Size) })
                    xml.Append("<ept:VariantAttr><ept:Deger>").Append(SecurityElement.Escape(value)).Append("</ept:Deger><ept:Tanim>").Append(definition).Append("</ept:Tanim></ept:VariantAttr>");
                xml.Append("</ept:Attributes>");
                Field("Miktar", variant.Quantity.ToString(CultureInfo.InvariantCulture));
                Field("VariantBarkod", variant.Barcode);
                xml.Append("</ept:Variant>");
            }
            xml.Append("</ept:Variants>");
        }
        return xml.Append("</ept:ProductV3Request>").ToString();
    }

    // The variants PTT AVM holds under the parent barcode, from the product read before the send. The product found
    // under its own barcode with no options is the parent itself as a product without variants, not a variant.
    public static IReadOnlyList<RemoteVariant> RemoteVariants(string parentBarcode, IReadOnlyList<RemoteCatalogProduct> listed)
    {
        var variants = new List<RemoteVariant>();
        foreach (var product in listed.Where(x => string.Equals(x.ProductMainId?.Trim(), parentBarcode, StringComparison.OrdinalIgnoreCase)))
            foreach (var variant in product.Variants)
            {
                if (variant.Options.Count == 0 && string.Equals(variant.Barcode, parentBarcode, StringComparison.OrdinalIgnoreCase)) continue;
                string? Option(params string[] names) => names.Select(name => variant.Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null).FirstOrDefault(x => x is not null);
                var (price, priceIsDifference, active) = PriceAndActive(variant.RawJson);
                variants.Add(new(string.IsNullOrWhiteSpace(variant.Barcode) ? null : variant.Barcode.Trim(), Option("Renk", "Varyant 1"), Option("Beden", "Varyant 2"), Option(PttAvmVariantMapper.VariantNameOption), variant.StockQuantity,
                    price, priceIsDifference, active));
            }
        return variants;
    }

    // The variant's own fields from its snapshot (PttAvmProductMapper.Snapshot of the PTT AVM element): Fiyat or
    // FiyatFarki, FiyatFarkiMi and Aktif. A field PTT AVM did not send stays null.
    private static (decimal? Price, bool? PriceIsDifference, bool? Active) PriceAndActive(string rawJson)
    {
        try
        {
            if (JsonNode.Parse(rawJson) is not JsonObject snapshot) return (null, null, null);
            string? Text(params string[] names) => names.Select(name => snapshot[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            decimal? price = decimal.TryParse(Text("Fiyat", "FiyatFarki", "PriceDifference", "Price"), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
            bool? Flag(string name) => bool.TryParse(Text(name), out var flag) ? flag : null;
            return (price, Flag("FiyatFarkiMi"), Flag("Aktif"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    // The merge of the variant send (pure, so it can be tested). PTT AVM's upsert deletes the variants a request leaves
    // out, so every variant PTT AVM holds is carried as it is there (its barcode, colour, size and stock), a panel
    // variant PTT AVM already holds is not sent with the panel's data (price and stock sync update it), and a panel
    // variant whose barcode PTT AVM lacks (case-insensitive) is added. A PTT AVM variant without a barcode, colour,
    // size or a readable stock, or one whose barcode repeats, cannot be carried unchanged: it is listed as unreadable.
    public static VariantMerge MergeVariants(IReadOnlyList<VariantDraft> panel, IReadOnlyList<RemoteVariant> remote)
    {
        var carried = new List<VariantDraft>();
        var unreadable = new List<RemoteVariant>();
        var notCarriable = new List<NotCarriableVariant>();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in remote)
        {
            if (variant is not { Barcode: { Length: > 0 and <= PttAvmPriceInventoryRules.MaxBarcodeLength } barcode, Color: { Length: > 0 and <= MaxVariantValueLength } color, Size: { Length: > 0 and <= MaxVariantValueLength } size, Quantity: { } quantity }
                || quantity != decimal.Truncate(quantity) || quantity is < 0 or > PttAvmPriceInventoryRules.MaxQuantity
                || !held.Add(barcode))
            {
                unreadable.Add(variant);
                continue;
            }
            carried.Add(new(barcode, color, size, (int)quantity));
            // The write-side Variant has no price and no active flag: a variant priced apart or passive on PTT AVM would
            // not stay as it is. A field PTT AVM did not send is unknown, not a reason to stop.
            var reasons = new List<string>();
            if (variant.Price is { } price && price != 0)
                reasons.Add(variant.PriceIsDifference == false
                    ? $"kendi fiyatı {price.ToString("0.##", CultureInfo.InvariantCulture)} TL"
                    : $"{price.ToString("0.##", CultureInfo.InvariantCulture)} TL fiyat farkı");
            if (variant.Active == false) reasons.Add("pasif");
            if (reasons.Count > 0) notCarriable.Add(new(variant, string.Join(", ", reasons)));
        }
        var added = panel.Where(x => !held.Contains(x.Barcode)).ToList();
        return new([.. carried, .. added], added, unreadable, notCarriable);
    }

    public static string RemoteVariantNotCarriableMessage(IReadOnlyList<NotCarriableVariant> variants)
    {
        const int shown = 8;
        var names = variants.Take(shown).Select(x => $"{x.Variant.Barcode}: {x.Reason}");
        var more = variants.Count > shown ? $" ve {variants.Count - shown} varyant daha" : "";
        return $"PTT AVM'deki bazı varyantlar olduğu gibi taşınamaz; gönderim fiyat farklarını veya pasif durumlarını silebilir: {string.Join("; ", names)}{more}. İstek gönderilmedi.";
    }

    // The merged request against PTT AVM's limits; null when it may go.
    public static (string Code, string Message)? MergeRefusal(IReadOnlyList<VariantDraft> merged)
    {
        if (merged.Count > PttAvmPriceInventoryRules.MaxVariantsPerProduct)
            return ("PTTAVM_VARIANT_LIMIT_EXCEEDED", $"PTT AVM'deki varyantlarla birlikte {merged.Count} varyant oluyor; PTT AVM bir üründe en fazla {PttAvmPriceInventoryRules.MaxVariantsPerProduct} varyant tutar. İstek gönderilmedi.");
        // A panel variant with another barcode under a colour and size PTT AVM already holds would make two of one variant.
        var duplicate = merged.GroupBy(x => VariantLabel(x.Color, x.Size), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            return ("PTTAVM_VARIANT_OPTION_CONFLICT", $"PTT AVM'de '{duplicate.First().Color} / {duplicate.First().Size}' varyantı başka bir barkodla var ({string.Join(", ", duplicate.Select(x => x.Barcode))}); aynı renk ve beden iki kez gönderilemez. İstek gönderilmedi.");
        var total = merged.Sum(x => x.Quantity);
        if (total > PttAvmPriceInventoryRules.MaxQuantity)
            return ("PTTAVM_STOCK_TOTAL_TOO_HIGH", $"PTT AVM bir ürünün stoğunu en fazla {PttAvmPriceInventoryRules.MaxQuantity} tutar; PTT AVM'deki varyantlarla birlikte toplam {total} adet oluyor. İstek gönderilmedi.");
        return null;
    }

    public static string RemoteVariantUnreadableMessage(IReadOnlyList<RemoteVariant> unreadable)
    {
        const int shown = 8;
        var names = unreadable.Take(shown).Select(variant =>
        {
            var options = variant.Color is not null && variant.Size is not null ? $"{variant.Color} / {variant.Size}" : variant.Name;
            return variant.Barcode is null ? options ?? "adı okunamayan varyant" : options is null ? variant.Barcode : $"{variant.Barcode} ({options})";
        });
        var more = unreadable.Count > shown ? $" ve {unreadable.Count - shown} varyant daha" : "";
        return $"PTT AVM'deki bazı varyantların barkodu, renk/beden değeri veya stoğu okunamadı: {string.Join(", ", names)}{more}. Bu varyantlar olduğu gibi taşınamadığı ve gönderim onları silebileceği için istek gönderilmedi.";
    }

    public static string DryRunMessage(ProductDraft draft) =>
        $"Kuru çalıştırma: {draft.Variants?.Count ?? 0} varyantlı PTT AVM isteği hazırlandı, gönderilmedi. İstek çalışan (worker) günlüğünde '{draft.Barcode}' barkoduyla kayıtlı.";

    // Colour and size folded for comparison: case, Turkish letters and the spacing around a dash do not matter.
    private static string VariantLabel(string color, string size) => $"{Fold(color)}/{Fold(size)}";

    private static string Fold(string value) =>
        Regex.Replace(Regex.Replace(PttAvmOrderMapper.Fold(value.Replace('İ', 'I')), @"\s*-\s*", "-"), @"\s+", " ");

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
