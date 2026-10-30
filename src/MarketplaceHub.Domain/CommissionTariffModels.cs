namespace MarketplaceHub.Domain;

// A marketplace's commission tariff for one period, as the seller uploaded it (Trendyol's weekly "komisyon tarifesi"
// export). Each row gives a product's price bands and the commission rate of each band for the period; a sale's
// commission is the rate of the band its commission-basis price falls in. PeriodEnd is exclusive.
public sealed class CommissionTariff
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string PlatformCode { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public required string PeriodText { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public int RowCount { get; set; }
}

public sealed class CommissionTariffRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid TariffId { get; set; }
    public required string Barcode { get; set; }
    public string? SellerSku { get; set; }
    public string? ModelCode { get; set; }
    public string? ProductName { get; set; }
    public string? Size { get; set; }
    // Band 1: price >= Tier1Min; band 2: Tier2Min..Tier2Max; band 3: Tier3Min..Tier3Max; band 4: price <= Tier4Max.
    public decimal? Tier1Min { get; set; }
    public decimal? Tier2Max { get; set; }
    public decimal? Tier2Min { get; set; }
    public decimal? Tier3Max { get; set; }
    public decimal? Tier3Min { get; set; }
    public decimal? Tier4Max { get; set; }
    public decimal? Rate1 { get; set; }
    public decimal? Rate2 { get; set; }
    public decimal? Rate3 { get; set; }
    public decimal? Rate4 { get; set; }
    public decimal? BasisPrice { get; set; }
    public decimal? CurrentRate { get; set; }
    public decimal? CurrentSalePrice { get; set; }
    public string? TariffGroup { get; set; }
    // The file's KATEGORİ, MARKA and STOK: what the list is filtered by (null in a tariff uploaded before they were read).
    public string? Category { get; set; }
    public string? Brand { get; set; }
    public decimal? Stock { get; set; }
}

// The tariff file exactly as the seller uploaded it, so the Excel of the seller's picks is Trendyol's own workbook with
// only the price and tariff choice cells filled in. Kept apart from the tariff so listing tariffs never reads it.
public sealed class CommissionTariffSourceFile
{
    public Guid TariffId { get; set; }
    public Guid TenantId { get; set; }
    public required byte[] Content { get; set; }
}

// The seller's picks on a tariff, kept until they are taken back or the tariff is deleted: the pick for every row
// (BulkKind "tier" with BulkTier, "amount" or "rate", with its profit limits; null for none) and the row by row picks.
public sealed class CommissionTariffSelection
{
    public Guid TariffId { get; set; }
    public Guid TenantId { get; set; }
    public string? BulkKind { get; set; }
    public int? BulkTier { get; set; }
    public decimal? MinRate { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxRate { get; set; }
    public decimal? MaxAmount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// One row's own pick: a band (Tier), a price of the seller's own (Price), or neither (the row left out on purpose
// while a pick for every row is on).
public sealed class CommissionTariffPick
{
    public Guid TariffId { get; set; }
    public Guid RowId { get; set; }
    public Guid TenantId { get; set; }
    public int? Tier { get; set; }
    public decimal? Price { get; set; }
}

public sealed record CommissionBand(int Tier, decimal? Min, decimal? Max, decimal Rate);

public static class CommissionTariffBands
{
    // The row's bands with a rate, from the highest price band down. A band without a lower bound reaches down to
    // zero and one without an upper bound has no ceiling; a middle band with neither bound is left out.
    public static IReadOnlyList<CommissionBand> Bands(CommissionTariffRow row)
    {
        var bands = new List<CommissionBand>(4);
        if (row.Rate1 is { } rate1 && row.Tier1Min is not null) bands.Add(new(1, row.Tier1Min, null, rate1));
        if (row.Rate2 is { } rate2 && (row.Tier2Min is not null || row.Tier2Max is not null)) bands.Add(new(2, row.Tier2Min, row.Tier2Max, rate2));
        if (row.Rate3 is { } rate3 && (row.Tier3Min is not null || row.Tier3Max is not null)) bands.Add(new(3, row.Tier3Min, row.Tier3Max, rate3));
        if (row.Rate4 is { } rate4 && row.Tier4Max is not null) bands.Add(new(4, null, row.Tier4Max, rate4));
        return bands;
    }

    // The band a commission-basis price falls in, the price rounded to kuruş as the marketplace's limits are. Null when
    // the price falls in no band (a gap or no bands): the commission is then not known, never guessed.
    public static CommissionBand? BandFor(CommissionTariffRow row, decimal basisPrice)
    {
        if (basisPrice < 0) return null;
        var price = decimal.Round(basisPrice, 2, MidpointRounding.AwayFromZero);
        return Bands(row).FirstOrDefault(band => (band.Min is null || price >= band.Min) && (band.Max is null || price <= band.Max));
    }

    // The nearest band with a lower rate than the one the price is in: the highest price that still earns it.
    public static CommissionBand? NextLowerBand(CommissionTariffRow row, decimal basisPrice)
    {
        var current = BandFor(row, basisPrice);
        if (current is null) return null;
        return Bands(row)
            .Where(band => band.Rate < current.Rate && band.Max is not null && band.Max < decimal.Round(basisPrice, 2, MidpointRounding.AwayFromZero))
            .OrderByDescending(band => band.Max)
            .FirstOrDefault();
    }

    // The price a band is priced at when the seller picks it: the highest price that still earns it (its upper limit);
    // for the open top band the current price when it already earns it, else the band's lower limit.
    public static decimal? TargetPrice(CommissionBand band, decimal? currentPrice)
    {
        if (band.Max is { } max) return max;
        if (currentPrice is { } price && band.Min is { } min && price >= min) return decimal.Round(price, 2, MidpointRounding.AwayFromZero);
        return band.Min;
    }
}
