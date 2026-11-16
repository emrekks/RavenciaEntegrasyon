using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-3: the two micro-export rules side by side (OrderMicroExportRules). Neither is changed here; the owner
// decides which one holds. Each row is an order (marketplace, package snapshot, customer name) with what the order-type
// label (Label) and the Fatura Bölgesi / Mikro İhracat filter (Filter) say today. The rows where they differ are the
// orders a one-line switch to either rule would move.
public sealed class OrderMicroExportRulesTests
{
    public static TheoryData<string, string, string, string, bool, bool> Orders => new()
    {
        // Both agree.
        { "micro true", "TRENDYOL", """{"micro":true}""", "Ali Veli", true, true },
        { "micro true with a space", "TRENDYOL", """{"micro": true}""", "Ali Veli", true, true },
        { "domestic", "TRENDYOL", """{"micro":false}""", "Ali Veli", false, false },
        { "a company named İhracat (the fixed text search)", "TRENDYOL", """{"micro":false,"invoiceAddress":{"company":"ABC İTHALAT İHRACAT LTD"},"lines":[{"productName":"MICRO FIBER HAVLU"}]}""", "ABC İTHALAT İHRACAT LTD", false, false },
        { "micro true only on a line", "TRENDYOL", """{"lines":[{"micro":true}]}""", "Ali Veli", true, true },
        // They differ: the label finds an export the filter does not.
        { "micro as text", "TRENDYOL", """{"micro":"true"}""", "Ali Veli", true, false },
        { "micro as 1", "TRENDYOL", """{"micro":1}""", "Ali Veli", true, false },
        { "microExport true", "TRENDYOL", """{"microExport":true}""", "Ali Veli", true, false },
        { "3pByTrendyol true before micro false", "TRENDYOL", """{"3pByTrendyol":true,"micro":false}""", "Ali Veli", true, false },
        { "package type MICRO", "TRENDYOL", """{"shipmentPackageType":"MICRO_EXPORT"}""", "Ali Veli", true, false },
        { "order type İHRACAT", "TRENDYOL", """{"orderType":"İHRACAT"}""", "Ali Veli", true, false },
        { "old PM3 / Arvato partner", "TRENDYOL", """{"micro":false}""", "PM3 ARVATO LOGISTICS", true, false },
        { "micro true on another marketplace", "HEPSIBURADA", """{"micro":true}""", "Ali Veli", true, false },
        // The label reads the first of micro / microExport / 3pByTrendyol in the snapshot's order: the same fields in the
        // other order read as domestic (the filter never looks at 3pByTrendyol).
        { "micro false before 3pByTrendyol true", "TRENDYOL", """{"micro":false,"3pByTrendyol":true}""", "Ali Veli", false, false },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public void TheLabelAndTheFilter_SayWhatTheyDoToday(string order, string platformCode, string customerJson, string customerName, bool label, bool filter)
    {
        Assert.True(label == OrderMicroExportRules.Label(customerJson, customerName), $"{order}: label");
        Assert.True(filter == OrderMicroExportRules.Filter(platformCode, customerJson), $"{order}: filter");
    }

    // The label the order screens show is rule 1 as it stands.
    [Fact]
    public void TheCustomerReadOfTheScreens_IsTheLabelsRule()
    {
        foreach (var row in Orders)
        {
            var customerJson = (string)row[2];
            Assert.Equal(OrderMicroExportRules.Label(customerJson, OrderSnapshotReader.ResolveCustomerName(customerJson, "{}", "{}")), OrderSnapshotReader.Customer(customerJson, "{}", "{}").IsMicroExport);
        }
    }
}
