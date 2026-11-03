using System.Globalization;
using System.Text;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Sorts Trendyol's accounting records into cost categories and gives each its cost to the seller. The type a record was
// asked for decides where the documentation is clear; deduction invoices and corrections, whose kinds Trendyol does
// not list, are read by keywords in their Turkish name and description. A deduction invoice nothing matches is
// OTHER_DEDUCTION (still a charge); anything else nothing matches is OTHER.
// https://developers.trendyol.com/v2.0/docs/current-account-statement-integration.md
internal static class TrendyolFinanceCategorizer
{
    public const string PlatformServiceFee = "PlatformServiceFee";
    public const string DeductionInvoices = "DeductionInvoices";
    public const string FinancialItem = "FinancialItem";

    // The settlement types read, one request each (a type Trendyol refuses is skipped and reported, not guessed). The
    // names are Trendyol's own spelling: "ManuelRefund", "TYDiscount" and "TYCoupon" were refused with 400 until 4 Nov 2026.
    public static readonly IReadOnlyList<string> SettlementTypes =
    [
        "Sale", "Return", "Discount", "DiscountCancel", "Coupon", "CouponCancel", "ProvisionPositive", "ProvisionNegative",
        "ManualRefund", "ManualRefundCancel", "TyDiscount", "TyDiscountCancel", "TyCoupon", "TyCouponCancel",
        "SellerRevenuePositive", "SellerRevenueNegative", "CommissionPositive", "CommissionNegative",
        "SellerRevenuePositiveCancel", "SellerRevenueNegativeCancel", "CommissionPositiveCancel", "CommissionNegativeCancel",
        "DeliveryFee", "DeliveryFeeCancel", "PayByLink"
    ];

    // The other financial types read as statement records.
    public static readonly IReadOnlyList<string> OtherFinancialTypes = [DeductionInvoices, "Stoppage", "CommissionAgreementInvoice", FinancialItem, "ReturnInvoice"];

    // Payouts, early payments taken and transfers: money moved, not a cost. Read from the same service under their own
    // source (MarketplaceFinanceSources.MoneyMovements) for the payout view only.
    public static readonly IReadOnlyList<string> MoneyMovementTypes = ["PaymentOrder", "CashAdvance", "WireTransfer", "IncomingTransfer"];

    // The sign of commissionAmount for the seller, from the documentation's table: +1 the seller pays it, -1 it comes
    // back. Commission corrections are read from debt and credit instead (see CommissionCost).
    private static readonly Dictionary<string, int> CommissionSigns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sale"] = 1,
        ["PayByLink"] = 1,
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
        ["ManualRefund"] = -1,
        ["ManualRefundCancel"] = 1,
        ["ManuelRefundCancel"] = 1
    };

    private static readonly HashSet<string> CommissionCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        "CommissionPositive", "CommissionNegative", "CommissionPositiveCancel", "CommissionNegativeCancel", "CommissionAgreementInvoice"
    };

    public static string Categorize(string source, string requestedType, string? transactionSubType, string? transactionType, string? description)
    {
        if (source == MarketplaceFinanceSources.MoneyMovements) return MarketplaceFinanceCategories.Transfer;
        if (string.Equals(transactionSubType, PlatformServiceFee, StringComparison.OrdinalIgnoreCase))
        {
            // The PlatformServiceFee filter also returns cargo invoices and advertising charges (live, 3 Nov 2026: 70 "Kargo
            // Fatura" and 9 "Reklam Bedeli" invoices carried the mark), so a deduction invoice's own name wins over the mark.
            var named = string.Equals(requestedType, DeductionInvoices, StringComparison.OrdinalIgnoreCase) ? ByKeyword(transactionType, description) : MarketplaceFinanceCategories.Other;
            return named is MarketplaceFinanceCategories.Other or MarketplaceFinanceCategories.ServiceFee ? MarketplaceFinanceCategories.ServiceFee : named;
        }
        switch (requestedType)
        {
            case "Sale" or "PayByLink": return MarketplaceFinanceCategories.Sale;
            case "Return" or "ManuelRefund" or "ManualRefund" or "ManualRefundCancel" or "ManuelRefundCancel": return MarketplaceFinanceCategories.Return;
            case "Discount" or "DiscountCancel" or "TYDiscount" or "TYDiscountCancel" or "TyDiscount" or "TyDiscountCancel": return MarketplaceFinanceCategories.Discount;
            case "Coupon" or "CouponCancel" or "TYCoupon" or "TYCouponCancel" or "TyCoupon" or "TyCouponCancel": return MarketplaceFinanceCategories.Coupon;
            case "Stoppage": return MarketplaceFinanceCategories.Stoppage;
        }
        if (CommissionCorrections.Contains(requestedType)) return MarketplaceFinanceCategories.Commission;
        // Provisions and progress payment corrections are neither a sale nor a cost by themselves.
        if (source == TrendyolFinanceRequestContract.Settlements) return MarketplaceFinanceCategories.Other;
        var category = ByKeyword(transactionType, description);
        return category == MarketplaceFinanceCategories.Other && string.Equals(requestedType, DeductionInvoices, StringComparison.OrdinalIgnoreCase)
            ? MarketplaceFinanceCategories.OtherDeduction
            : category;
    }

    // The category a record counts under: a deduction invoice no keyword names is a charge (OTHER_DEDUCTION), and so is
    // a financial item that takes more than it gives back. Applied when a record is stored and to every stored row the
    // report reads, so rows stored as OTHER before this rule count as costs without rewriting them; OTHER stays for
    // provisions, revenue corrections and items that give money back.
    public static string Charged(string source, string requestedType, string category, decimal debt, decimal credit) =>
        category == MarketplaceFinanceCategories.Other && source == TrendyolFinanceRequestContract.OtherFinancials
        && (string.Equals(requestedType, DeductionInvoices, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedType, FinancialItem, StringComparison.OrdinalIgnoreCase) && debt > credit)
            ? MarketplaceFinanceCategories.OtherDeduction
            : category;

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
