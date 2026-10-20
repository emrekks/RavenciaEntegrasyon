using System.Text.Json;

namespace MarketplaceHub.Application;

public sealed record InvoiceIssueRefusal(string Code, string Message);

/// <summary>
/// Orders the automatic domestic SALE flow must not invoice. Each refusal is a fiscal decision:
/// the wrong document type would have to be cancelled and issued again at the tax authority.
/// </summary>
public static class InvoiceIssuePolicy
{
    public const string EInvoiceRecipientCode = "EINVOICE_RECIPIENT_REQUIRES_EFATURA";
    public const string AdjustmentFlowUnavailableCode = "INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE";
    public const string ExportFlowRequiredCode = "INVOICE_EXPORT_FLOW_REQUIRED";
    public const string RecipientTaxIdInvalidCode = "EFATURAM_RECIPIENT_TAX_ID_INVALID";

    public static readonly InvoiceIssueRefusal EInvoiceRecipient = new(EInvoiceRecipientCode,
        "Alıcı e-Fatura mükellefi. Bu siparişe e-Arşiv fatura kesilemez; e-Fatura durum sorgusu henüz doğrulanmadığı için otomatik e-Fatura da kapalı. Faturayı E-Faturam portalında e-Fatura olarak düzenleyip panele manuel belge olarak ekleyin.");

    public static readonly InvoiceIssueRefusal AdjustmentFlowUnavailable = new(AdjustmentFlowUnavailableCode,
        "İade/düzeltme faturası akışı henüz hazır değil. Orijinal faturaya bağlı yeni mali belge oluşturulmadı; iade faturasını E-Faturam portalında düzenleyin.");

    public static readonly InvoiceIssueRefusal ExportFlowRequired = new(ExportFlowRequiredCode,
        "Bu sipariş mikro ihracat siparişi veya TRY dışı para birimiyle geldi. Yurt içi e-Arşiv satış faturası kesilemez; ihracat faturası akışı gerekli.");

    /// <summary>The first reason the automatic SALE flow must not issue this invoice, or null.</summary>
    public static InvoiceIssueRefusal? Refusal(string invoiceType, string? currency, string? customerSnapshotJson)
    {
        if (RequiresExportFlow(currency, customerSnapshotJson)) return ExportFlowRequired;
        // E-Invoice status cannot be read back (the provider's outgoing E-Invoice status path is not
        // verified), so an E-Invoice recipient is stopped instead of being sent an e-Archive document.
        if (string.Equals(invoiceType, "TEMELFATURA", StringComparison.Ordinal)) return EInvoiceRecipient;
        return null;
    }

    public const string LineVatRateUnknownCode = "ORDER_LINE_VAT_RATE_UNKNOWN";

    public static readonly InvoiceIssueRefusal LineVatRateUnknown = new(LineVatRateUnknownCode,
        "Trendyol siparişin en az bir ürün satırında KDV oranını bildirmedi. KDV oranı bilinmeyen satırla fatura kesilmez; siparişi yeniden eşitleyin ya da faturayı E-Faturam portalında doğru KDV ile düzenleyip siparişe manuel belge olarak ekleyin.");

    /// <summary>
    /// True when the marketplace did not report the line's VAT rate. Trendyol lines keep their raw JSON; a line
    /// without a numeric vatRate was kept with an inferred rate or 0 (G30), which must not reach a fiscal invoice.
    /// Lines of other platforms, and lines without a raw snapshot, carry the rate the platform reported (0 included).
    /// </summary>
    public static bool IsLineVatRateUnknown(string? platformCode, string? lineSourceSnapshotJson)
    {
        if (!string.Equals(platformCode, "TRENDYOL", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lineSourceSnapshotJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(lineSourceSnapshotJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return false;
            if (!root.TryGetProperty("vatRate", out var vatRate)) return true;
            return vatRate.ValueKind switch
            {
                JsonValueKind.Number => false,
                JsonValueKind.String => !decimal.TryParse(vatRate.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
                _ => true
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
        ExportFlowRequiredCode => ExportFlowRequired.Message,
        AdjustmentFlowUnavailableCode => AdjustmentFlowUnavailable.Message,
        LineVatRateUnknownCode => LineVatRateUnknown.Message,
        RecipientTaxIdInvalidCode => "Alıcının vergi numarası veya T.C. kimlik numarası geçersiz (kontrol hanesi tutmuyor). Sipariş bilgisini pazaryerinde düzeltip yeniden eşitleyin.",
        null or "" => "Fatura doğrulanamadı.",
        _ => code
    };
}
