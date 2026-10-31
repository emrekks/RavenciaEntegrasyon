using System.Text.Json;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public sealed record InvoiceIssueRefusal(string Code, string Message);

/// <summary>
/// Orders the automatic domestic SALE flow must not invoice. Each refusal is a fiscal decision:
/// the wrong document type would have to be cancelled and issued again at the tax authority.
/// </summary>
public static class InvoiceIssuePolicy
{
    public const string EInvoiceRecipientCode = "EINVOICE_RECIPIENT_REQUIRES_EFATURA";
    public const string EInvoiceRecipientReviewCode = "EINVOICE_RECIPIENT_REVIEW_REQUIRED";
    public const string AdjustmentFlowUnavailableCode = "INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE";
    public const string ExportFlowRequiredCode = "INVOICE_EXPORT_FLOW_REQUIRED";
    public const string RecipientTaxIdInvalidCode = "EFATURAM_RECIPIENT_TAX_ID_INVALID";
    public const string EInvoiceTargetAliasUnresolvedCode = "EINVOICE_TARGET_ALIAS_UNRESOLVED";
    public const string EInvoiceTaxpayerConflictCode = "EINVOICE_TAXPAYER_CONFLICT";
    public const string EInvoiceTaxpayerChangedCode = "EINVOICE_TAXPAYER_STATUS_CHANGED";

    // Invoices stopped before the taxpayer lookup existed keep this code; no new invoice gets it.
    public static readonly InvoiceIssueRefusal EInvoiceRecipient = new(EInvoiceRecipientCode,
        "Alıcı e-Fatura mükellefi. Bu siparişe e-Arşiv fatura kesilemez; o tarihte otomatik e-Fatura kapalıydı. Önizlemeyi yeniden açın; mükellef sorgusu faturayı e-Fatura olarak hazırlar.");

    public static readonly InvoiceIssueRefusal EInvoiceRecipientReview = new(EInvoiceRecipientReviewCode,
        "Alıcı bir şirket ve E-Faturam e-Fatura mükellef sorgusu yanıt vermedi veya yanıtı doğrulanamadı. Türü bilinmeyen fatura kesilmedi. Birkaç dakika sonra önizlemeyi yeniden açın; sorun sürerse faturayı E-Faturam portalında doğru türde (mükellefse e-Fatura, değilse e-Arşiv) düzenleyip panele manuel belge olarak ekleyin.");

    public static readonly InvoiceIssueRefusal EInvoiceTargetAliasUnresolved = new(EInvoiceTargetAliasUnresolvedCode,
        "Alıcı e-Fatura mükellefi ancak birden fazla e-Fatura posta kutusu etiketi var ve varsayılan (defaultpk) etiket tek olarak seçilemedi. Fatura kesilmedi; faturayı E-Faturam portalında alıcının istediği etikete e-Fatura olarak düzenleyip panele manuel belge olarak ekleyin.");

    public static readonly InvoiceIssueRefusal EInvoiceTaxpayerConflict = new(EInvoiceTaxpayerConflictCode,
        "Pazaryeri alıcıyı e-Fatura mükellefi olarak bildirdi, ancak E-Faturam mükellef sorgusu alıcıyı e-Fatura kayıtlı kullanıcı listesinde bulamadı. Kaynaklar çeliştiği için fatura kesilmedi; alıcının durumunu kontrol edip faturayı E-Faturam portalında doğru türde düzenleyin.");

    public static readonly InvoiceIssueRefusal EInvoiceTaxpayerChanged = new(EInvoiceTaxpayerChangedCode,
        "Faturanın türü, güncel e-Fatura mükellef sorgusunun sonucuyla eşleşmiyor (alıcının mükellefiyeti değişmiş olabilir). Fatura gönderilmedi; önizlemeyi yeniden açıp güncel türle onaylayın.");

    public static readonly InvoiceIssueRefusal AdjustmentFlowUnavailable = new(AdjustmentFlowUnavailableCode,
        "İade/düzeltme faturası akışı henüz hazır değil. Orijinal faturaya bağlı yeni mali belge oluşturulmadı; iade faturasını E-Faturam portalında düzenleyin.");

    public static readonly InvoiceIssueRefusal ExportFlowRequired = new(ExportFlowRequiredCode,
        "Bu sipariş mikro ihracat siparişi veya TRY dışı para birimiyle geldi. Yurt içi e-Arşiv satış faturası kesilemez; ihracat faturası akışı gerekli.");

    /// <summary>The first reason the automatic SALE flow must not issue this invoice, or null.</summary>
    public static InvoiceIssueRefusal? Refusal(string invoiceType, string? currency, string? customerSnapshotJson)
    {
        if (RequiresExportFlow(currency, customerSnapshotJson)) return ExportFlowRequired;
        return null;
    }

    /// <summary>
    /// <see cref="Refusal(string, string, string)"/> plus the taxpayer lookup (<see cref="EInvoiceRecipientPolicy"/>):
    /// its own refusal, or an invoice whose saved type is not the type the lookup asks for now.
    /// </summary>
    public static InvoiceIssueRefusal? Refusal(string invoiceType, string? currency, string? customerSnapshotJson, EInvoiceRecipientDecision recipient)
    {
        if (Refusal(invoiceType, currency, customerSnapshotJson) is { } refusal) return refusal;
        if (recipient.Refusal is { } recipientRefusal) return recipientRefusal;
        return string.Equals(invoiceType, recipient.InvoiceType, StringComparison.Ordinal) ? null : EInvoiceTaxpayerChanged;
    }

    public const string LineVatRateUnknownCode = "ORDER_LINE_VAT_RATE_UNKNOWN";

    /// <summary>The refusal without the marketplace's name, for a stored validation code.</summary>
    public static readonly InvoiceIssueRefusal LineVatRateUnknown = LineVatRateUnknownFor(null);

    public static InvoiceIssueRefusal LineVatRateUnknownFor(string? platformCode) => new(LineVatRateUnknownCode,
        $"{PlatformName(platformCode)} siparişin en az bir ürün satırında KDV oranını bildirmedi. KDV oranı bilinmeyen satırla fatura kesilmez; siparişi yeniden eşitleyin ya da faturayı E-Faturam portalında doğru KDV ile düzenleyip siparişe manuel belge olarak ekleyin.");

    private static string PlatformName(string? platformCode) => platformCode switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PTT AVM",
        _ => "Pazaryeri"
    };

    // Where each marketplace reports a line's VAT rate inside the raw line JSON the order line keeps (alternatives,
    // each a property path). Shopify reports none (its lines carry 0), so its lines are not judged here.
    private static string[][]? VatRatePaths(string? platformCode) => platformCode switch
    {
        "TRENDYOL" or "N11" => [["vatRate"]],
        "HEPSIBURADA" => [["vatRate"], ["VatRate"]],
        "PAZARAMA" => [["product", "vatRate"]],
        "PTTAVM" => [["KdvOrani"]],
        _ => null
    };

    /// <summary>
    /// True when the marketplace did not report the line's VAT rate. Order lines keep their raw JSON; a line whose
    /// JSON has no numeric rate was kept with an inferred rate (Trendyol, G30), with 0, or with the rate an earlier
    /// read reported (T-2), none of which may reach a fiscal invoice. Lines without a raw snapshot, and Shopify lines,
    /// carry the rate they were stored with (0 included).
    /// </summary>
    public static bool IsLineVatRateUnknown(string? platformCode, string? lineSourceSnapshotJson)
    {
        if (VatRatePaths(platformCode) is not { } paths || string.IsNullOrWhiteSpace(lineSourceSnapshotJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(lineSourceSnapshotJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return false;
            return !paths.Any(path => IsReportedRate(root, path));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsReportedRate(JsonElement root, string[] path)
    {
        var vatRate = root;
        foreach (var name in path)
            if (vatRate.ValueKind != JsonValueKind.Object || !vatRate.TryGetProperty(name, out vatRate)) return false;
        return vatRate.ValueKind switch
        {
            JsonValueKind.Number => true,
            JsonValueKind.String => decimal.TryParse(vatRate.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
            _ => false
        };
    }

    private static readonly System.Text.RegularExpressions.Regex GibInvoiceNumberPattern = new(@"^[A-Za-z0-9]{3}\d{13}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The GİB invoice number format (three letters/digits and thirteen digits) Trendyol accepts with a link.</summary>
    public static bool IsGibInvoiceNumber(string? invoiceNumber) => invoiceNumber is not null && GibInvoiceNumberPattern.IsMatch(invoiceNumber.Trim());

    public static bool RequiresExportFlow(string? currency, string? customerSnapshotJson) =>
        !string.Equals(currency?.Trim(), "TRY", StringComparison.OrdinalIgnoreCase) || IsMicroExport(customerSnapshotJson);

    public static bool IsMicroExport(string? customerSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(customerSnapshotJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(customerSnapshotJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True) return true;
            if (root.TryGetProperty("microExport", out var microExport) && microExport.ValueKind == JsonValueKind.True) return true;
            if (root.TryGetProperty("3pByTrendyol", out var thirdParty) && thirdParty.ValueKind == JsonValueKind.True) return true;
            foreach (var name in new[] { "shipmentPackageType", "orderType" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && (value.GetString()?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true
                        || value.GetString()?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true)) return true;
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Turkish text for the validation codes an operator sees after a failed check.</summary>
    public static string DescribeValidationCode(string? code) => code switch
    {
        EInvoiceRecipientCode => EInvoiceRecipient.Message,
        EInvoiceRecipientReviewCode => EInvoiceRecipientReview.Message,
        EInvoiceTargetAliasUnresolvedCode => EInvoiceTargetAliasUnresolved.Message,
        EInvoiceTaxpayerConflictCode => EInvoiceTaxpayerConflict.Message,
        EInvoiceTaxpayerChangedCode => EInvoiceTaxpayerChanged.Message,
        ExportFlowRequiredCode => ExportFlowRequired.Message,
        AdjustmentFlowUnavailableCode => AdjustmentFlowUnavailable.Message,
        LineVatRateUnknownCode => LineVatRateUnknown.Message,
        RecipientTaxIdInvalidCode => "Alıcının vergi numarası veya T.C. kimlik numarası geçersiz (kontrol hanesi tutmuyor). Sipariş bilgisini pazaryerinde düzeltip yeniden eşitleyin.",
        null or "" => "Fatura doğrulanamadı.",
        _ => code
    };
}
