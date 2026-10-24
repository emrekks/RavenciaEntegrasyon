using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReportParcelDesiTests
{
    private static readonly Guid Sweater = Guid.CreateVersion7();
    private static readonly Guid Coat = Guid.CreateVersion7();
    private static readonly Guid Knitwear = Guid.CreateVersion7();

    [Fact]
    public void AUnitDesiIsTheMeanOfTheBilledDesiSpreadOverTheUnits_ZeroBandsIncluded()
    {
        // Seven single sweaters billed as 0 desi, three as 1: about 0.3 a unit, not the median's 0.
        var parcels = Enumerable.Repeat(Parcel(0m, (Sweater, Knitwear, 1m)), 7)
            .Concat(Enumerable.Repeat(Parcel(1m, (Sweater, Knitwear, 1m)), 3))
            // Two coats in one parcel of 4 desi: 2 a unit.
            .Concat(Enumerable.Repeat(Parcel(4m, (Coat, Knitwear, 2m)), 3));

        var learned = ReportParcelDesi.Learn(parcels);

        Assert.Equal(0.3m, learned.ByProduct[Sweater]);
        Assert.Equal(2m, learned.ByProduct[Coat]);
        Assert.Equal(13, learned.Parcels);
        Assert.Equal(Math.Round((0m * 7 + 1m * 3 + 2m * 3) / 13, 2), learned.ByCategory[Knitwear]);
        Assert.NotNull(learned.Overall);
    }

    [Fact]
    public void AProductWithTooFewParcelsTakesItsCategory_AndTooFewInAllLearnsNoStoreMean()
    {
        var other = Guid.CreateVersion7();
        var learned = ReportParcelDesi.Learn(
            Enumerable.Repeat(Parcel(1m, (Sweater, Knitwear, 1m)), 5).Append(Parcel(2m, (other, Knitwear, 1m))));

        Assert.False(learned.ByProduct.ContainsKey(other));
        Assert.Equal(learned.ByCategory[Knitwear], learned.For(other, Knitwear));
        Assert.Null(learned.Overall);
        Assert.Null(ReportParcelDesi.Learned.None.For(Sweater, Knitwear));
    }

    [Fact]
    public void TheOrdersDesiComesFromTheMarketplaceFirst_ThenTheEnteredDesi_ThenTheMeasuredOne()
    {
        var learned = ReportParcelDesi.Learn(Enumerable.Repeat(Parcel(1m, (Sweater, Knitwear, 2m)), 10));

        Assert.Equal((2.2m, ReportParcelDesi.Marketplace), ReportParcelDesi.ForOrder(2.2m, [(1.5m, Sweater, Knitwear, 2m)], learned));
        Assert.Equal((3m, ReportParcelDesi.Manual), ReportParcelDesi.ForOrder(null, [(1.5m, Sweater, Knitwear, 2m)], learned));
        // Two sweaters measured at 0.5 a unit make one parcel of 1 desi, not 1 + 1.
        Assert.Equal((1m, ReportParcelDesi.Measured), ReportParcelDesi.ForOrder(null, [(null, Sweater, Knitwear, 2m)], learned));
        Assert.Equal((2m, ReportParcelDesi.Measured), ReportParcelDesi.ForOrder(null, [(1.5m, Coat, null, 1m), (null, Sweater, Knitwear, 1m)], learned));
    }

    [Fact]
    public void AnOrderWithNothingKnownKeepsTheOneDesiParcel_AndAStoreMeanCountsAsTheDefault()
    {
        Assert.Equal(((decimal?)null, ReportParcelDesi.Default), ReportParcelDesi.ForOrder(null, [(null, Sweater, null, 2m)], ReportParcelDesi.Learned.None));
        // Only the entered line is known: the other counts as one desi, as before.
        Assert.Equal((3m, ReportParcelDesi.Default), ReportParcelDesi.ForOrder(null, [(2m, Coat, null, 1m), (null, Sweater, null, 1m)], ReportParcelDesi.Learned.None));

        var learned = ReportParcelDesi.Learn(Enumerable.Repeat(Parcel(1m, (Sweater, null, 2m)), 10));
        Assert.Equal((1.5m, ReportParcelDesi.Default), ReportParcelDesi.ForOrder(null, [(null, Coat, null, 3m)], learned));
    }

    [Theory]
    [InlineData("{\"deci\":1,\"totalDeci\":2,\"quantity\":2}", "2")]
    [InlineData("{\"deci\":1.4,\"quantity\":2}", "2.8")]
    [InlineData("{\"deci\":\"1.1\",\"totalDeci\":\"1.1\",\"quantity\":1}", "1.1")]
    public void HepsiburadasLineDesiIsItsTotalDeci_ElseDeciTimesTheQuantity(string snapshot, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ReportParcelDesi.HepsiburadaLineDesi(snapshot));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"deci\":0,\"totalDeci\":0}")]
    [InlineData("{\"quantity\":1}")]
    [InlineData("{broken")]
    public void AHepsiburadaLineWithoutADesiHasNone(string? snapshot) =>
        Assert.Null(ReportParcelDesi.HepsiburadaLineDesi(snapshot));

    private static ReportParcelDesi.Parcel Parcel(decimal desi, params (Guid Product, Guid? Category, decimal Units)[] lines) =>
        new(desi, lines.Select(line => new ReportParcelDesi.ParcelLine(line.Product, line.Category, line.Units)).ToList());
}
