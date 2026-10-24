namespace MarketplaceHub.Infrastructure.Persistence;

// What a sale costs on a platform whose statement the panel does not read (Hepsiburada, n11, Pazarama, PTT AVM,
// Shopify): the cargo from the platform's contracted tariff for the parcel's desi and carrier (or its low-basket
// "barem" price), the platform's service fees, the own store's card payment fees and the e-commerce withholding tax.
// Amounts include VAT. Sources and dates: docs/KARGO-TARIFELERI-2026.md.
internal static class ReportPlatformCosts
{
    private const decimal Vat = 1.20m;
    // n11 and Pazarama add the post service fee (Posta Hizmet Bedeli, 2.35 %) to the cargo before VAT.
    private const decimal PostFee = 1.0235m;
    // A product without a desi entered counts as one desi.
    internal const decimal DefaultDesi = 1m;
    internal const decimal StoppageRate = 0.01m;
    // The own store (24 Oct 2026, owner): Shopify Basic (2 % of every card payment taken outside Shopify Payments),
    // iyzico 4.29 % + 0.25 TL (BSMV included) and about 120 TL of cargo an order, paid by the store.
    internal const decimal ShopifyCargo = 120m;
    internal const decimal ShopifyFeeRate = 0.02m;
    internal const decimal IyzicoRate = 0.0429m;
    internal const decimal IyzicoFixed = 0.25m;

    public static bool Covers(string platformCode) => platformCode is "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM" or "SHOPIFY";

    // The costs of one order: amount is what the customer paid, keptVatFree the kept sale without its VAT, desi the
    // parcel's (null when no line has one entered), carrier the package's carrier name.
    public static Estimate For(string platformCode, decimal amount, decimal keptVatFree, bool returned, decimal? desi, string? carrier)
    {
        var parcel = desi is > 0 ? desi.Value : DefaultDesi;
        switch (platformCode)
        {
            case "HEPSIBURADA":
                {
                    // Barem below 400 TL for hepsiJET and Sürat only (43.99 / 75.99 + VAT from 21 Sep 2026), otherwise the
                    // 5 Oct 2026 desi table; a 10.99 + VAT service fee on every delivery.
                    var company = HepsiburadaCarrier(carrier);
                    var baremable = company is "HEPSIJET" or "SURAT" && parcel < 10m;
                    var cargo = baremable && amount < 200m ? 43.99m : baremable && amount < 400m ? 75.99m : Desi(HepsiburadaTable[company], parcel);
                    return new(Shipping(cargo * Vat, returned), 10.99m * Vat, Stoppage(keptVatFree), 0m);
                }
            case "N11":
                {
                    // Özel Kargo: the Şartlı Ödeme barem below 300 TL up to 10 desi, otherwise the desi table, plus the post
                    // fee; the pazarlama (1 %) and pazaryeri (0.67 %) service fees of the sale, plus VAT.
                    var company = N11Carrier(carrier);
                    var (low, high) = N11Barem[company];
                    var cargo = parcel <= 10m && amount < 150m ? low : parcel <= 10m && amount < 300m ? high : Desi(N11Table[company], parcel);
                    return new(Shipping(cargo * PostFee * Vat, returned), amount * 0.0167m * Vat, Stoppage(keptVatFree), 0m);
                }
            case "PAZARAMA":
                {
                    // The barem below 300 TL up to 10 desi (the standard template, not same-day), otherwise the 7 Oct 2026
                    // desi table, plus the post fee; an 8.99 + VAT platform fee an order.
                    var company = PazaramaCarrier(carrier);
                    var (low, high) = PazaramaBarem[company];
                    var cargo = parcel <= 10m && amount < 150m ? low : parcel <= 10m && amount < 300m ? high : Desi(PazaramaTable[company], parcel);
                    return new(Shipping(cargo * PostFee * Vat, returned), 8.99m * Vat, Stoppage(keptVatFree), 0m);
                }
            case "PTTAVM":
                // PTT Kargo at the contract's table (27 Mar 2026, VAT included); a 1.5 % infrastructure fee of the sale.
                return new(Shipping(PttAvm(parcel), returned), amount * 0.015m, Stoppage(keptVatFree), 0m);
            case "SHOPIFY":
                // The own store: no marketplace, so no withholding tax; the customer pays a return's cargo.
                return new(ShopifyCargo, 0m, 0m, amount > 0 ? amount * (IyzicoRate + ShopifyFeeRate) + IyzicoFixed : 0m);
            default:
                return new(0m, 0m, 0m, 0m);
        }
    }

    // A returned order's cargo comes back at the seller's cost too (on a withdrawal the seller pays the return).
    private static decimal Shipping(decimal oneWay, bool returned) => returned ? oneWay * 2m : oneWay;

    private static decimal Stoppage(decimal keptVatFree) => keptVatFree * StoppageRate;

    private static decimal Desi(decimal[] table, decimal desi) => table[Math.Clamp((int)Math.Ceiling(desi) - 1, 0, table.Length - 1)];

    private static decimal PttAvm(decimal desi) =>
        desi <= 0.5m ? 77.50m : desi <= 1m ? 78.75m : PttAvmTable[Math.Clamp((int)Math.Ceiling(desi) - 2, 0, PttAvmTable.Length - 1)];

    private static string Fold(string? carrier) =>
        (carrier ?? "").ToUpperInvariant().Replace('İ', 'I').Replace('Ü', 'U').Replace('Ç', 'C').Replace('Ş', 'S').Replace('Ö', 'O').Replace('Ğ', 'G');

    private static string HepsiburadaCarrier(string? carrier) => Fold(carrier) switch
    {
        var name when name.Contains("ARAS") => "ARAS",
        var name when name.Contains("PTT") => "PTT",
        var name when name.Contains("SURAT") => "SURAT",
        _ => "HEPSIJET"
    };

    private static string N11Carrier(string? carrier) => Fold(carrier) switch
    {
        var name when name.Contains("PTT") => "PTT",
        var name when name.Contains("SURAT") => "SURAT",
        var name when name.Contains("YURTICI") => "YURTICI",
        _ => "ARAS"
    };

    private static string PazaramaCarrier(string? carrier) => Fold(carrier).Contains("PTT") ? "PTT" : "ARAS";

    // TL without VAT, desi 1 to 30 (a parcel up to one desi takes the first); a heavier parcel takes the 30 desi price.
    private static readonly Dictionary<string, decimal[]> HepsiburadaTable = new()
    {
        ["HEPSIJET"] = [81.99m, 81.99m, 99.99m, 107.99m, 115.99m, 124.99m, 133.99m, 141.99m, 149.99m, 159.99m, 166.99m, 174.99m, 189.99m, 194.99m, 201.99m,
            212.99m, 223.99m, 235.99m, 253.99m, 260.99m, 270.99m, 285.99m, 295.99m, 305.99m, 311.99m, 325.99m, 337.99m, 348.99m, 361.99m, 372.99m],
        ["ARAS"] = [89.92m, 92.34m, 100.55m, 108.91m, 116.23m, 126.37m, 133.77m, 142.47m, 150.41m, 160.62m, 168.79m, 174.50m, 181.90m, 188.46m, 195.02m,
            205.21m, 215.36m, 225.55m, 235.74m, 239.78m, 251.52m, 262.21m, 272.95m, 282.38m, 291.78m, 305.20m, 317.41m, 328.36m, 340.86m, 350.40m],
        ["PTT"] = [93.95m, 93.95m, 115.09m, 115.09m, 119.79m, 126.83m, 133.87m, 147.97m, 162.07m, 183.21m, 192.60m, 201.99m, 211.39m, 220.78m, 230.18m,
            239.58m, 248.97m, 258.37m, 267.76m, 277.16m, 286.55m, 293.60m, 303.00m, 312.39m, 321.78m, 331.18m, 340.57m, 349.97m, 359.36m, 368.75m],
        ["SURAT"] = [97.55m, 97.55m, 110.05m, 120.00m, 125.99m, 138.57m, 147.83m, 157.24m, 166.64m, 175.90m, 187.76m, 195.66m, 203.56m, 211.47m, 219.23m,
            227.41m, 239.40m, 251.39m, 263.51m, 275.50m, 287.90m, 298.94m, 309.98m, 321.01m, 332.05m, 342.68m, 353.44m, 364.07m, 374.70m, 385.32m]
    };

    private static readonly Dictionary<string, decimal[]> N11Table = new()
    {
        ["ARAS"] = [90.50m, 92.14m, 102.81m, 113.82m, 122.05m, 132.97m, 141.02m, 150.45m, 159.07m, 170.09m, 179.27m, 185.87m, 194.30m, 201.80m, 209.24m,
            220.97m, 232.63m, 244.37m, 256.06m, 261.08m, 274.24m, 286.25m, 298.24m, 308.90m, 319.50m, 334.36m, 347.93m, 360.06m, 373.97m, 384.58m],
        ["PTT"] = [81.82m, 81.82m, 101.29m, 102.88m, 102.88m, 106.25m, 112.16m, 123.97m, 135.78m, 153.48m, 161.34m, 169.22m, 177.09m, 184.95m, 192.83m,
            200.70m, 208.57m, 216.44m, 224.31m, 232.18m, 240.05m, 245.96m, 253.83m, 261.70m, 269.57m, 277.45m, 285.31m, 293.19m, 301.05m, 308.92m],
        ["SURAT"] = [95.32m, 95.32m, 107.52m, 117.25m, 121.56m, 133.13m, 142.44m, 151.10m, 160.55m, 169.74m, 183.45m, 191.17m, 198.89m, 206.61m, 214.20m,
            222.19m, 233.91m, 245.62m, 257.46m, 269.18m, 281.30m, 292.08m, 302.86m, 313.64m, 324.43m, 334.81m, 345.33m, 355.71m, 366.09m, 376.48m],
        ["YURTICI"] = [117.84m, 120.83m, 128.50m, 131.04m, 146.32m, 151.43m, 171.10m, 178.31m, 188.91m, 197.37m, 210.20m, 223.36m, 230.64m, 248.87m, 262.11m,
            269.32m, 284.17m, 298.72m, 305.07m, 311.44m, 329.33m, 343.78m, 352.24m, 359.05m, 383.82m, 419.55m, 439.02m, 459.06m, 474.79m, 479.50m]
    };

    // The Şartlı Ödeme barem: below 150 TL, and 150 to 300 TL.
    private static readonly Dictionary<string, (decimal Low, decimal High)> N11Barem = new()
    {
        ["PTT"] = (38.74m, 70.41m),
        ["ARAS"] = (48.33m, 79.16m),
        ["SURAT"] = (54.58m, 85.41m),
        ["YURTICI"] = (83.33m, 113.33m)
    };

    private static readonly Dictionary<string, decimal[]> PazaramaTable = new()
    {
        ["ARAS"] = [91.46m, 101.36m, 113.34m, 125.82m, 137.94m, 147.51m, 157.11m, 166.69m, 176.28m, 185.85m, 190.13m, 198.10m, 206.03m, 214.00m, 221.90m,
            234.31m, 246.70m, 259.12m, 271.52m, 278.48m, 295.29m, 306.63m, 317.89m, 329.25m, 340.58m, 351.49m, 362.40m, 373.31m, 384.22m, 395.14m],
        ["PTT"] = [93.59m, 93.59m, 113.30m, 122.32m, 134.43m, 142.72m, 156.59m, 164.53m, 175.22m, 196.05m, 204.92m, 215.75m, 225.61m, 235.46m, 245.31m,
            255.17m, 265.01m, 274.87m, 285.70m, 294.57m, 304.33m, 314.27m, 324.13m, 333.98m, 343.77m, 350.72m, 363.53m, 373.38m, 382.75m, 535.94m]
    };

    private static readonly Dictionary<string, (decimal Low, decimal High)> PazaramaBarem = new()
    {
        ["PTT"] = (68.74m, 74.16m),
        ["ARAS"] = (80.83m, 86.24m)
    };

    // VAT included: index 0 is 1 to 2 desi, up to 29 to 30 desi (up to one desi is priced apart).
    private static readonly decimal[] PttAvmTable =
    [
        81.25m, 82.50m, 85.00m, 86.25m, 90.00m, 103.75m, 125.00m, 131.25m, 138.75m, 143.75m, 153.75m, 158.75m, 165.00m, 153.75m, 156.25m,
        157.50m, 168.75m, 168.75m, 168.75m, 169.90m, 169.90m, 169.90m, 169.90m, 169.90m, 194.90m, 194.90m, 194.90m, 194.90m, 194.90m, 199.90m
    ];

    // One order's costs, VAT included: Cargo (out, and back for a return), ServiceFee (the platform's fees), Stoppage,
    // PaymentFee (the own store's card payment fees: BSMV, no VAT to deduct).
    internal sealed record Estimate(decimal Cargo, decimal ServiceFee, decimal Stoppage, decimal PaymentFee);
}
