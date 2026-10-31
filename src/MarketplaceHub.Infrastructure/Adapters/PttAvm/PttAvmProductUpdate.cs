using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// "Platformlarda Güncelle" on PTT AVM (the ProductUpdate job's single phase, SUBMIT_UNAPPROVED). UpdateProductsV3 is
// the same upsert the publication uses, keyed by barcode; a product field left out of the request stays as it is, so
// the request carries the barcode and the picked fields only: no price, no stock, no active flag, no category. Two
// rules of the upsert make it a write that can destroy: a barcode PTT AVM does not hold opens a new product, and the
// variants a request leaves out are deleted. So right before the send the product is read again by its barcode (the
// variant list fresh): it must be there, and every variant PTT AVM holds under it goes in the request as PTT AVM holds
// it (barcode, colour, size, stock). A variant that cannot be carried unchanged (unreadable, priced apart, passive)
// stops the send; nothing is added. The result is read through the tracking id like the publication's.
public sealed partial class PttAvmHttpClient
{
    public async Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken)
    {
        var draft = PttAvmProductUpdateRules.Read(publication.UnapprovedPayloadJson, out var invalid);
        if (draft is null) return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, "PTTAVM_PRODUCT_UPDATE_PAYLOAD_INVALID", invalid ?? "PTT AVM ürün güncelleme içeriği geçersiz.", HttpStatusCode.UnprocessableEntity);
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<RemoteOperationRef>(load);
        var account = load.Account;
        if (WriteBlocked<RemoteOperationRef>(account, "ürün güncellemesi") is { } blocked) return blocked;

        // The variant list first, read fresh: a list read only in part may leave this product out, and a product left out
        // reads as one without variants.
        var variantList = await VariantListAsync(account, refresh: true, cancellationToken);
        if (!variantList.IsSuccess && variantList.Error!.Code == VariantListIncompleteCode)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantUnreadableCode, $"PTT AVM'de '{draft.Barcode}' ürününün varyantlarının tamamı okunamadı ({variantList.Error.SafeMessage}); silinecek varyant olup olmadığı bilinmediği için güncelleme gönderilmedi.", HttpStatusCode.Conflict);
        if (!variantList.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(variantList.Error!, variantList.RateLimit);
        var current = await ReadProductPageAsync(account, null, draft.Barcode, cancellationToken);
        if (!current.IsSuccess) return AdapterResult<RemoteOperationRef>.Failure(current.Error!, current.RateLimit);
        if ((current.Value!.Issues ?? []).Any(issue => issue.Code == "PTTAVM_VARIANT_LIST_UNAVAILABLE"
                || issue.Code == "PRODUCT_VARIANT_INVALID" && issue.Identity.StartsWith(draft.Barcode + ":", StringComparison.OrdinalIgnoreCase)))
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantUnreadableCode, $"PTT AVM'de '{draft.Barcode}' ürününün varyantlarının tamamı okunamadı; silinecek varyant olup olmadığı bilinmediği için güncelleme gönderilmedi.", HttpStatusCode.Conflict);
        var listed = current.Value.Items;
        // The upsert opens a new product under a barcode PTT AVM does not hold: an update goes only to a product it lists.
        if (PttAvmProductPayloadRules.FamilyProduct(draft.Barcode, listed) is null)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductUpdateRules.ProductNotFoundCode, $"PTT AVM'de '{draft.Barcode}' barkodlu ürün bulunamadı; güncelleme yeni ürün açacağı için gönderilmedi. Ürünü önce yayınlayın veya eşleştirin.", HttpStatusCode.Conflict);
        // A product the panel holds without variants must not be a variant of a model on PTT AVM either.
        if (draft.Variants is null && PttAvmProductPayloadRules.RemoteVariantRisk(draft.Barcode, listed) is { } risk)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, "PTTAVM_VARIANT_DELETION_RISK", risk, HttpStatusCode.Conflict);

        var remote = PttAvmProductPayloadRules.RemoteVariants(draft.Barcode, listed);
        var carry = PttAvmProductUpdateRules.CarryVariants(remote, draft.Variants ?? []);
        if (carry.Unreadable.Count > 0)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantUnreadableCode, PttAvmProductPayloadRules.RemoteVariantUnreadableMessage(carry.Unreadable), HttpStatusCode.Conflict);
        if (carry.NotCarriable.Count > 0)
            return Failure<RemoteOperationRef>(AdapterErrorClass.BusinessConflict, PttAvmProductPayloadRules.RemoteVariantNotCarriableCode, PttAvmProductPayloadRules.RemoteVariantNotCarriableMessage(carry.NotCarriable), HttpStatusCode.Conflict);
        if (carry.Variants.Count > 0 && PttAvmProductPayloadRules.MergeRefusal(carry.Variants) is { } refusal)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, refusal.Code, refusal.Message, HttpStatusCode.UnprocessableEntity);

        var itemsXml = PttAvmProductUpdateRules.ItemsXml(draft, carry.Variants);
        logger.LogInformation("PTT AVM product update request. DryRun: {DryRun}, ConnectionId: {ConnectionId}, Barcode: {Barcode}, Fields: {Fields}, RemoteVariants: {RemoteVariants}, CarriedVariants: {CarriedVariants}, Operation: {Operation}, Items: {Items}",
            carry.Variants.Count > 0 && PttAvmProductPayloadRules.VariantSendDryRun, account.Connection.Id, draft.Barcode, string.Join(",", draft.Fields), remote.Count, carry.Variants.Count, ProductUpsert, itemsXml);
        if (carry.Variants.Count > 0 && PttAvmProductPayloadRules.VariantSendDryRun)
            return Failure<RemoteOperationRef>(AdapterErrorClass.Validation, PttAvmProductPayloadRules.DryRunCode, $"Kuru çalıştırma: {carry.Variants.Count} varyantlı PTT AVM güncellemesi hazırlandı, gönderilmedi.", HttpStatusCode.UnprocessableEntity);
        return await SendUpsertAsync(account, itemsXml, cancellationToken);
    }
}

public static class PttAvmProductUpdateRules
{
    public const string ProductNotFoundCode = "PTTAVM_PRODUCT_NOT_FOUND";
    public const string Mode = "PTTAVM";
    public const int MaxWarrantyMonths = 24;
    public const int MaxCourierDeliveryDays = 30;
    public const decimal MaxDesi = 300;

    // A panel variant of the family (its barcode, colour and size): what a variant PTT AVM lists only by its name
    // ("Gri / L") is matched to, so it can be carried under its barcode.
    public sealed record PanelVariant(string Barcode, string Color, string Size);

    // The update of one PTT AVM product: its barcode (the model code of a family), the picked fields and their values
    // (null when not picked), and the family's panel variants (null for a product without variants).
    public sealed record UpdateDraft(string Barcode, IReadOnlyList<string> Fields, string? Name = null, string? LongDescription = null, IReadOnlyList<string>? Images = null,
        int? WarrantyDuration = null, decimal? Desi = null, decimal? VatRate = null, int? EstimatedCourierDelivery = null, IReadOnlyList<PanelVariant>? Variants = null);

    // The variants the request carries (every one PTT AVM holds), those that cannot be read or matched, and those whose
    // price difference or passive state the request would lose.
    public sealed record VariantCarry(IReadOnlyList<PttAvmProductPayloadRules.VariantDraft> Variants, IReadOnlyList<PttAvmProductPayloadRules.RemoteVariant> Unreadable, IReadOnlyList<PttAvmProductPayloadRules.NotCarriableVariant> NotCarriable);

    // {"items":[{...}]}: one item, so the update job's "has items" check sees a request to send.
    public static string Payload(UpdateDraft draft)
    {
        var item = new JsonObject
        {
            ["barcode"] = draft.Barcode,
            ["fields"] = new JsonArray([.. draft.Fields.Select(field => (JsonNode)field)])
        };
        if (draft.Name is not null) item["name"] = draft.Name;
        if (draft.LongDescription is not null) item["longDescription"] = draft.LongDescription;
        if (draft.Images is not null) item["images"] = new JsonArray([.. draft.Images.Select(url => (JsonNode)url)]);
        if (draft.WarrantyDuration is { } warranty) item["warrantyDuration"] = warranty;
        if (draft.Desi is { } desi) item["desi"] = desi;
        if (draft.VatRate is { } vatRate) item["vatRate"] = vatRate;
        if (draft.EstimatedCourierDelivery is { } delivery) item["estimatedCourierDelivery"] = delivery;
        if (draft.Variants is { Count: > 0 } variants)
            item["variants"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject { ["barcode"] = variant.Barcode, ["color"] = variant.Color, ["size"] = variant.Size })]);
        return new JsonObject { ["items"] = new JsonArray(item) }.ToJsonString();
    }

    // The product barcode of an update payload; null when it cannot be read.
    public static string? Barcode(string payloadJson) => Read(payloadJson, out _)?.Barcode;

    // Null (with the reason) when the payload breaks PTT AVM's rules: each picked field must carry its value, in range.
    public static UpdateDraft? Read(string payloadJson, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1 || items[0].ValueKind != JsonValueKind.Object)
            {
                error = "PTT AVM ürün güncellemesi tek ürün taşımalıdır.";
                return null;
            }
            var root = items[0];
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            decimal? Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : null;
            int? Whole(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            List<string>? Texts(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null).OfType<string>().ToList() : null;
            var fields = root.TryGetProperty("fields", out var fieldList) ? Texts(fieldList) ?? [] : [];
            var images = root.TryGetProperty("images", out var imageList) ? Texts(imageList) : null;
            List<PanelVariant>? variants = null;
            if (root.TryGetProperty("variants", out var variantList) && variantList.ValueKind == JsonValueKind.Array)
                variants = variantList.EnumerateArray()
                    .Select(entry => entry.ValueKind == JsonValueKind.Object ? new PanelVariant(Field(entry, "barcode"), Field(entry, "color"), Field(entry, "size")) : new PanelVariant("", "", ""))
                    .ToList();
            var draft = new UpdateDraft(Text("barcode") ?? "", fields, Text("name"), Text("longDescription"), images, Whole("warrantyDuration"), Number("desi"), Number("vatRate"), Whole("estimatedCourierDelivery"), variants);
            bool Picked(string field) => fields.Contains(field, StringComparer.Ordinal);
            var imageLimit = PttAvmProductPayloadRules.MaxImagesPerColour * Math.Max(1, variants?.Select(x => PttAvmProductPayloadRules.VariantLabel(x.Color, "")).Distinct(StringComparer.Ordinal).Count() ?? 0);
            error = draft switch
            {
                { Barcode.Length: 0 or > PttAvmPriceInventoryRules.MaxBarcodeLength } => "Barkod boş veya çok uzun.",
                { Fields.Count: 0 } => "Gönderilecek en az bir ürün bilgisi alanı seçilmelidir.",
                _ when fields.Any(field => !PttAvmProductUpdateFields.Supported.Contains(field)) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count => "PTT AVM'ye gönderilemeyen bir ürün bilgisi alanı seçilmiş.",
                _ when variants is not null && (variants.Count == 0 || variants.Any(x => x.Barcode.Length == 0 || x.Color.Length == 0 || x.Size.Length == 0)) => "Model ailesinin varyantları barkod, renk ve beden taşımalıdır.",
                _ when Picked(ProductUpdateFields.Title) && draft.Name is not { Length: > 0 and <= PttAvmProductPayloadRules.MaxNameLength } => $"Ürün adı 1-{PttAvmProductPayloadRules.MaxNameLength} karakter olmalıdır.",
                _ when Picked(ProductUpdateFields.Description) && string.IsNullOrEmpty(draft.LongDescription) => "Ürün açıklaması boş olamaz.",
                _ when Picked(ProductUpdateFields.Images) && (images is not { Count: > 0 } || images.Count > imageLimit || images.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)) => $"Görseller 1-{imageLimit} adet (renk başına {PttAvmProductPayloadRules.MaxImagesPerColour}) https adresi olmalıdır.",
                _ when Picked(ProductUpdateFields.Warranty) && draft.WarrantyDuration is not (>= 0 and <= MaxWarrantyMonths) => $"PTT AVM garanti süresi 0-{MaxWarrantyMonths} ay olmalıdır.",
                _ when Picked(ProductUpdateFields.Desi) && draft.Desi is not (> 0 and <= MaxDesi) => "Desi 0'dan büyük ve en fazla 300 olmalıdır.",
                _ when Picked(ProductUpdateFields.VatRate) && PttAvmPriceInventoryRules.VatRate(draft.VatRate) is null => "KDV oranı 0, 1, 10 veya 20 olmalıdır.",
                _ when Picked(ProductUpdateFields.Delivery) && draft.EstimatedCourierDelivery is not (>= 0 and <= MaxCourierDeliveryDays) => $"PTT AVM tahmini kargoya teslim süresi 0-{MaxCourierDeliveryDays} gün olmalıdır.",
                _ => null
            };
            return error is null ? draft : null;
        }
        catch (JsonException)
        {
            error = "PTT AVM ürün güncelleme içeriği okunamadı.";
            return null;
        }
    }

    private static string Field(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    // Every variant PTT AVM holds under the product, as it holds it. A variant listed with its barcode is carried with
    // its barcode, colour, size and stock. One listed only by its name ("Gri / L": colour, then size) is carried under
    // the barcode of the one panel variant of the family with that colour and size (folded: case, Turkish letters and
    // spacing do not matter), with PTT AVM's stock. Anything else is unreadable: a name of one part ("S", the
    // single-value list PTT AVM gives for a family opened through UpdateProductsV3), a name no panel variant or two of
    // them carry, a stock that is not a whole number in 0-9999, a barcode carried twice. Unreadable stops the send, as
    // does a price difference or a passive flag the request (VariantV3 has neither) would lose. No panel variant is added.
    public static VariantCarry CarryVariants(IReadOnlyList<PttAvmProductPayloadRules.RemoteVariant> remote, IReadOnlyList<PanelVariant> panel)
    {
        var carried = new List<PttAvmProductPayloadRules.VariantDraft>();
        var unreadable = new List<PttAvmProductPayloadRules.RemoteVariant>();
        var notCarriable = new List<PttAvmProductPayloadRules.NotCarriableVariant>();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var panelByLabel = panel.GroupBy(x => PttAvmProductPayloadRules.VariantLabel(x.Color, x.Size), StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        foreach (var variant in remote)
        {
            PttAvmProductPayloadRules.VariantDraft? draft = null;
            if (variant.Quantity is { } quantity && quantity == decimal.Truncate(quantity) && quantity is >= 0 and <= PttAvmPriceInventoryRules.MaxQuantity)
            {
                if (variant is { Barcode: { Length: > 0 and <= PttAvmPriceInventoryRules.MaxBarcodeLength } barcode, Color: { Length: > 0 and <= PttAvmProductPayloadRules.MaxVariantValueLength } color, Size: { Length: > 0 and <= PttAvmProductPayloadRules.MaxVariantValueLength } size })
                    draft = new(barcode, color, size, (int)quantity);
                else if (variant.Barcode is null && variant.Name is { } name
                    && name.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [var nameColor, var nameSize]
                    && panelByLabel.TryGetValue(PttAvmProductPayloadRules.VariantLabel(nameColor, nameSize), out var matches) && matches.Count == 1)
                    draft = new(matches[0].Barcode, matches[0].Color, matches[0].Size, (int)quantity);
            }
            if (draft is null || !held.Add(draft.Barcode))
            {
                unreadable.Add(variant);
                continue;
            }
            carried.Add(draft);
            var reasons = new List<string>();
            if (variant.Price is { } price && price != 0)
                reasons.Add(variant.PriceIsDifference == false
                    ? $"kendi fiyatı {price.ToString("0.##", CultureInfo.InvariantCulture)} TL"
                    : $"{price.ToString("0.##", CultureInfo.InvariantCulture)} TL fiyat farkı");
            if (variant.Active == false) reasons.Add("pasif");
            if (reasons.Count > 0) notCarriable.Add(new(variant, string.Join(", ", reasons)));
        }
        return new(carried, unreadable, notCarriable);
    }

    // ProductV3Request with the barcode, the picked fields and the carried variants, in the data-contract (alphabetical)
    // order of the service schema (?singleWsdl, read 31 October 2026: every member is optional and nillable but
    // Discount). Left out: Active, NoShippingProduct, PriceWithVat, Quantity and CategoryId, so the update changes
    // neither the product's state nor its price, stock or category. Variants is left out only when PTT AVM holds none.
    internal static string ItemsXml(UpdateDraft draft, IReadOnlyList<PttAvmProductPayloadRules.VariantDraft> variants)
    {
        var xml = new StringBuilder($"""<ept:ProductV3Request xmlns:ept="{PttAvmHttpClient.RequestsNamespace}">""");
        void Field(string name, string value) => xml.Append("<ept:").Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</ept:").Append(name).Append('>');
        bool Picked(string field) => draft.Fields.Contains(field, StringComparer.Ordinal);
        Field("Barcode", draft.Barcode);
        if (Picked(ProductUpdateFields.Desi) && draft.Desi is { } desi) Field("Desi", desi.ToString("0.##", CultureInfo.InvariantCulture));
        if (Picked(ProductUpdateFields.Delivery) && draft.EstimatedCourierDelivery is { } delivery) Field("EstimatedCourierDelivery", delivery.ToString(CultureInfo.InvariantCulture));
        if (Picked(ProductUpdateFields.Images) && draft.Images is { } images)
        {
            xml.Append("<ept:Images>");
            foreach (var url in images) xml.Append("<ept:ProductImageV3><ept:Url>").Append(SecurityElement.Escape(url)).Append("</ept:Url></ept:ProductImageV3>");
            xml.Append("</ept:Images>");
        }
        if (Picked(ProductUpdateFields.Description) && draft.LongDescription is { } description) Field("LongDescription", description);
        if (Picked(ProductUpdateFields.Title) && draft.Name is { } name) Field("Name", name);
        if (Picked(ProductUpdateFields.VatRate) && draft.VatRate is { } vatRate) Field("VATRate", vatRate.ToString("0", CultureInfo.InvariantCulture));
        if (variants.Count > 0)
        {
            xml.Append("<ept:Variants>");
            foreach (var variant in variants)
            {
                xml.Append("<ept:VariantV3><ept:Attributes>");
                foreach (var (definition, value) in new[] { ("Renk", variant.Color), ("Beden", variant.Size) })
                    xml.Append("<ept:VariantAttrV3><ept:Definition>").Append(definition).Append("</ept:Definition><ept:Value>").Append(SecurityElement.Escape(value)).Append("</ept:Value></ept:VariantAttrV3>");
                xml.Append("</ept:Attributes>");
                Field("Quantity", variant.Quantity.ToString(CultureInfo.InvariantCulture));
                Field("VariantBarcode", variant.Barcode);
                xml.Append("</ept:VariantV3>");
            }
            xml.Append("</ept:Variants>");
        }
        if (Picked(ProductUpdateFields.Warranty) && draft.WarrantyDuration is { } warranty) Field("WarrantyDuration", warranty.ToString(CultureInfo.InvariantCulture));
        return xml.Append("</ept:ProductV3Request>").ToString();
    }

    // The card's state after PTT AVM accepted the update. PTT AVM has no approval step and the update changes no
    // product state, so each listing keeps what it was; one an earlier update left refused is linked again (accepted
    // now, its sale state not read). The card is what its listings share, partly live when only some are live.
    public static string CompletedListingStatus(string? current) =>
        current is null or "UPDATE_REJECTED" or "UPDATE_BLOCKED" ? "LINKED" : current;

    public static string CompletedProfileStatus(IReadOnlyList<string> listingStatuses)
    {
        var distinct = listingStatuses.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1) return distinct[0];
        return listingStatuses.Any(status => status == "LIVE") ? "PARTIAL_LIVE" : "LINKED";
    }
}
