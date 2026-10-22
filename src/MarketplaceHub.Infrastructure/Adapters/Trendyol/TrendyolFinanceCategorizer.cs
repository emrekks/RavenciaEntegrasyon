using System.Globalization;
using System.Text;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Sorts Trendyol's accounting records into cost categories and gives each its cost to the seller. The type a record was
// asked for decides where the documentation is clear; deduction invoices and corrections, whose kinds Trendyol does
// not list, are read by keywords in their Turkish name and description. Nothing matched is OTHER.
// https://developers.trendyol.com/v2.0/docs/current-account-statement-integration.md
internal static class TrendyolFinanceCategorizer
{
    public const string PlatformServiceFee = "PlatformServiceFee";
    public const string DeductionInvoices = "DeductionInvoices";

    // The settlement types read, one request each (a type Trendyol refuses is skipped and reported, not guessed).
    public static readonly IReadOnlyList<string> SettlementTypes =
    [
        "Sale", "Return", "Discount", "DiscountCancel", "Coupon", "CouponCancel", "ProvisionPositive", "ProvisionNegative",
        "ManuelRefund", "ManualRefundCancel", "TYDiscount", "TYDiscountCancel", "TYCoupon", "TYCouponCancel",
        "SellerRevenuePositive", "SellerRevenueNegative", "CommissionPositive", "CommissionNegative",
        "SellerRevenuePositiveCancel", "SellerRevenueNegativeCancel", "CommissionPositiveCancel", "CommissionNegativeCancel"
    ];

    // The other financial types read. Payments and transfers (PaymentOrder, WireTransfer, IncomingTransfer) and early
    // payment advances (CashAdvance) move money and are not costs, so they are not read at all.
    public static readonly IReadOnlyList<string> OtherFinancialTypes = [DeductionInvoices, "Stoppage", "CommissionAgreementInvoice", "FinancialItem", "ReturnInvoice"];

    // The sign of commissionAmount for the seller, from the documentation's table: +1 the seller pays it, -1 it comes
    // back. Commission corrections are read from debt and credit instead (see CommissionCost).
    private static readonly Dictionary<string, int> CommissionSigns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sale"] = 1,
        ["Return"] = -1,
        ["Discount"] = -1,
        ["DiscountCancel"] = 1,
        ["Coupon"] = -1,
        ["CouponCancel"] = 1,
        ["ProvisionPositive"] = 1,
        ["ProvisionNegative"] = -1,
        ["TYDiscount"] = -1,
        ["TYDiscountCancel"] = 1,
        ["TYCoupon"] = -1,
        ["TYCouponCancel"] = 1,
        ["ManuelRefund"] = -1,
        ["ManualRefundCancel"] = 1,
        ["ManuelRefundCancel"] = 1
    };

    private static readonly HashSet<string> CommissionCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        "CommissionPositive", "CommissionNegative", "CommissionPositiveCancel", "CommissionNegativeCancel", "CommissionAgreementInvoice"
    };

    public static string Categorize(string source, string requestedType, string? transactionSubType, string? transactionType, string? description)
    {
        if (string.Equals(transactionSubType, PlatformServiceFee, StringComparison.OrdinalIgnoreCase)) return MarketplaceFinanceCategories.ServiceFee;
        switch (requestedType)
        {
            case "Sale": return MarketplaceFinanceCategories.Sale;
            case "Return" or "ManuelRefund" or "ManualRefundCancel" or "ManuelRefundCancel": return MarketplaceFinanceCategories.Return;
            case "Discount" or "DiscountCancel" or "TYDiscount" or "TYDiscountCancel": return MarketplaceFinanceCategories.Discount;
            case "Coupon" or "CouponCancel" or "TYCoupon" or "TYCouponCancel": return MarketplaceFinanceCategories.Coupon;
            case "Stoppage": return MarketplaceFinanceCategories.Stoppage;
        }
        if (CommissionCorrections.Contains(requestedType)) return MarketplaceFinanceCategories.Commission;
        // Provisions and progress payment corrections are neither a sale nor a cost by themselves.
        if (source == TrendyolFinanceRequestContract.Settlements) return MarketplaceFinanceCategories.Other;
        return ByKeyword(transactionType, description);
    }

    // The deduction invoice kinds are not documented; the order matters ("Erken Ödeme Hizmet Bedeli" is an early
    // payment fee, "Reklam Hizmet Bedeli" advertising, before the generic "hizmet").
    internal static string ByKeyword(string? transactionType, string? description)
    {
        var text = Fold($"{transactionType} {description}");
        if (text.Contains("kargo", StringComparison.Ordinal)) return MarketplaceFinanceCategories.Cargo;
        if (text.Contains("erken odeme", StringComparison.Ordinal)) return MarketplaceFinanceCategories.EarlyPayment;
        if (text.Contains("reklam", StringComparison.Ordinal)) return MarketplaceFinanceCategories.Advertising;
        if (text.Contains("ceza", StringComparison.Ordinal) || text.Contains("gecikme", StringComparison.Ordinal)) return MarketplaceFinanceCategories.Penalty;
        if (text.Contains("stopaj", StringComparison.Ordinal)) return MarketplaceFinanceCategories.Stoppage;
        if (text.Contains("komisyon", StringComparison.Ordinal)) return MarketplaceFinanceCategories.Commission;
        if (text.Contains("hizmet", StringComparison.Ordinal) || text.Contains("platform", StringComparison.Ordinal)) return MarketplaceFinanceCategories.ServiceFee;
        return MarketplaceFinanceCategories.Other;
    }

    // A cargo invoice's id is the serial number its lines are read by ("Kargo Faturası" or "Kargo Fatura").
    public static bool IsCargoInvoice(string source, string requestedType, string category) =>
        source == TrendyolFinanceRequestContract.OtherFinancials && string.Equals(requestedType, DeductionInvoices, StringComparison.OrdinalIgnoreCase)
        && category == MarketplaceFinanceCategories.Cargo;

    // The commission a record charges the seller (negative: given back). Settlement lines carry it in commissionAmount,
    // signed by their type; commission corrections and commission agreement invoices are charged through debt and credit.
    public static decimal CommissionCost(string requestedType, decimal? commissionAmount, decimal debt, decimal credit)
    {
        if (CommissionCorrections.Contains(requestedType)) return debt - credit;
        return commissionAmount is { } amount && CommissionSigns.TryGetValue(requestedType, out var sign) ? sign * Math.Abs(amount) : 0m;
    }

    // What a record costs the seller in its own category: debt lowers the seller's balance, credit raises it.
    public static decimal NetCost(decimal debt, decimal credit) => debt - credit;

    public static bool IsCost(string category) => MarketplaceFinanceCategories.Costs.Contains(category);

    // Lower case without Turkish marks ("Kargo Faturası" -> "kargo faturasi", "ÖDEME" -> "odeme").
    internal static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var lower = value.ToLower(CultureInfo.GetCultureInfo("tr-TR")).Replace('ı', 'i');
        var builder = new StringBuilder(lower.Length);
        foreach (var character in lower.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
