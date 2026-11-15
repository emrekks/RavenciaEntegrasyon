namespace MarketplaceHub.Application;

// A page of tariff rows in the short form the panel asks for (rows?shape=compact): the same values without their names
// and without what repeats, which the panel puts back as it was (commission-tariff-rows.ts expandRowPage). A model's
// rows repeat its name and picture, and every band priced for a row repeats its product cost, cargo, service fee and
// VAT rate and its band's rate: 641 rows of a live tariff were 1.04 MB in the long form.
//
//   { strings: [text…], rows: [row…], totalCount, page, pageSize }
//   row:    [id, barcode, sellerSku, modelCode, productName#, size, basisPrice, currentSalePrice, currentRate, currentTier,
//            currentBandRate, bands, nextLowerBand, matchesPanel, costPrice, choices, recommendedTier, imageUrl#, color,
//            stock, shared]
//   band:   [tier, min, max, rate]
//   choice: [tier, price, commission, remaining, costs] plus rate when it is not its band's rate
//   costs:  null, [stoppage, netVat] over the row's shared [cost, cargo, serviceFee, vatRate], else
//           [cost, cargo, serviceFee, stoppage, netVat, vatRate]
// productName# and imageUrl#: the index of the text in strings, or null.
// The cursor members the long form has (nextCursor, hasMore) follow at the end, as there.

/// <summary>The short form of a tariff row page: { strings, rows, totalCount, page, pageSize, nextCursor, hasMore }.</summary>
public sealed record CommissionTariffCompactRowPage(IReadOnlyList<string> Strings, IReadOnlyList<object?[]> Rows, int TotalCount, int Page, int PageSize)
{
    /// <summary>The cursor of the next page (unified paging), null on the last page or where the order is not total.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Whether a page follows this one.</summary>
    public bool HasMore { get; init; }
}

public static class CommissionTariffCompactRows
{
    public static CommissionTariffCompactRowPage Of(CommissionTariffRowPage page)
    {
        var strings = new List<string>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        int? Text(string? value)
        {
            if (value is null) return null;
            if (!indexes.TryGetValue(value, out var index))
            {
                index = strings.Count;
                strings.Add(value);
                indexes[value] = index;
            }
            return index;
        }
        var rows = page.Items.Select(row => Row(row, Text)).ToList();
        return new(strings, rows, page.TotalCount, page.Page, page.PageSize) { NextCursor = page.NextCursor, HasMore = page.HasMore };
    }

    private static object?[] Row(CommissionTariffRowView row, Func<string?, int?> text)
    {
        var priced = row.Choices?.Where(choice => choice.Costs is not null).Select(choice => choice.Costs!).ToList() ?? [];
        var first = priced.FirstOrDefault();
        var shared = first is not null && priced.All(costs => costs.Cost == first.Cost && costs.Cargo == first.Cargo && costs.ServiceFee == first.ServiceFee && costs.VatRate == first.VatRate)
            ? new object[] { first.Cost, first.Cargo, first.ServiceFee, first.VatRate }
            : null;
        return
        [
            row.Id, row.Barcode, row.SellerSku, row.ModelCode, text(row.ProductName), row.Size, row.BasisPrice, row.CurrentSalePrice, row.CurrentRate, row.CurrentTier,
            row.CurrentBandRate, row.Bands.Select(Band).ToList(), row.NextLowerBand is null ? null : Band(row.NextLowerBand), row.MatchesPanel, row.CostPrice,
            row.Choices?.Select(choice => Choice(choice, row.Bands, shared is not null)).ToList(), row.RecommendedTier, text(row.ImageUrl), row.Color, row.Stock, shared
        ];
    }

    private static object?[] Band(CommissionBandView band) => [band.Tier, band.Min, band.Max, band.Rate];

    private static object?[] Choice(CommissionChoiceView choice, IReadOnlyList<CommissionBandView> bands, bool shared)
    {
        object? costs = choice.Costs is not { } value ? null
            : shared ? new object[] { value.Stoppage, value.NetVat }
            : new object[] { value.Cost, value.Cargo, value.ServiceFee, value.Stoppage, value.NetVat, value.VatRate };
        var bandRate = bands.FirstOrDefault(band => band.Tier == choice.Tier)?.Rate;
        return bandRate == choice.Rate
            ? [choice.Tier, choice.Price, choice.Commission, choice.Remaining, costs]
            : [choice.Tier, choice.Price, choice.Commission, choice.Remaining, costs, choice.Rate];
    }
}
