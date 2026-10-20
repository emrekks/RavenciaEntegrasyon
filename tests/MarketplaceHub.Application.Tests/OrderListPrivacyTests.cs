using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-19 S5: the order list carries no customer e-mail (the shipping label reads it from
// the order detail at print time), and a list search never matches an e-mail address.
public sealed class OrderListPrivacyTests
{
    private const string Snapshot = """{"customerFirstName":"Ayşe","customerLastName":"Yılmaz","customerEmail":"ayse.yilmaz@example.com","invoiceAddress":{"city":"Kadıköy","email":"fatura@example.com"},"buyerMail":"buyer@example.com"}""";

    [Fact]
    public void Order_list_view_has_no_customer_email_and_the_detail_keeps_it()
    {
        Assert.Null(typeof(OrderListView).GetProperty("CustomerEmail"));
        Assert.NotNull(typeof(OrderDetailView).GetProperty("CustomerEmail"));
        // The label prints the delivery address from the detail in the list's shape.
        Assert.Equal(typeof(OrderAddressView), typeof(OrderDetailView).GetProperty("ShipmentAddress")?.PropertyType);
    }

    [Theory]
    [InlineData("Ayşe")]
    [InlineData("Yılmaz")]
    [InlineData("Kadıköy")]
    public void Customer_snapshot_search_matches_names_and_places(string term)
    {
        Assert.Matches(MarketplaceSalesService.CustomerSnapshotSearchPattern(term), Snapshot);
    }

    [Theory]
    [InlineData("example")]
    [InlineData("ayse.yilmaz")]
    [InlineData("fatura@")]
    [InlineData("buyer")]
    public void Customer_snapshot_search_never_matches_an_email_address(string term)
    {
        Assert.DoesNotMatch(MarketplaceSalesService.CustomerSnapshotSearchPattern(term), Snapshot);
    }

    [Fact]
    public void Customer_snapshot_search_treats_the_term_literally()
    {
        var pattern = MarketplaceSalesService.CustomerSnapshotSearchPattern("A.e (x)");

        Assert.DoesNotMatch(pattern, """{"customerFirstName":"Axe (x)"}""");
        Assert.Matches(pattern, """{"customerFirstName":"A.e (x)"}""");
        Assert.Matches(MarketplaceSalesService.CustomerSnapshotSearchPattern("a\\b"), "{\"name\":\"a\\b\"}");
    }

    [Fact]
    public void Order_list_search_uses_the_email_excluding_expression()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=metadata-only;Username=metadata-only;Password=metadata-only")
            .Options;
        using var db = new AppDbContext(options);
        var service = new MarketplaceSalesService(db, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        IQueryable<Order> query = db.Orders;

        service.ApplyOrderFilters(ref query, new OrderListQuery(Search: "Ayşe"), Guid.NewGuid());
        var sql = query.ToQueryString();

        Assert.Contains("\"CustomerSnapshotJson\" ~", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("strpos(o.\"CustomerSnapshotJson\"", sql, StringComparison.Ordinal);
    }
}
