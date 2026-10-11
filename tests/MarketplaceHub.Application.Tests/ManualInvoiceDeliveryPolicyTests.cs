using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ManualInvoiceDeliveryPolicyTests
{
    [Theory]
    [InlineData("STARTED")]
    [InlineData("UNKNOWN")]
    [InlineData("MANUAL_REVIEW")]
    public void AmbiguousOrReviewedAttemptsMustBeInspectedWithoutResubmission(string status)
    {
        Assert.True(ManualInvoiceDeliveryPolicy.RequiresReadOnlyInspection(status));
        Assert.False(ManualInvoiceDeliveryPolicy.CanSubmitAfterPrecheck(status, MarketplaceInvoiceStatus.NotInvoiced));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("QUEUED")]
    [InlineData("FAILED")]
    [InlineData("RETRYABLE_FAILURE")]
    public void ClearOrKnownFailedAttemptsMayProceedOnlyAfterFreshNotInvoicedRead(string? status)
    {
        Assert.False(ManualInvoiceDeliveryPolicy.RequiresReadOnlyInspection(status));
        Assert.True(ManualInvoiceDeliveryPolicy.CanSubmitAfterPrecheck(status, MarketplaceInvoiceStatus.NotInvoiced));
        Assert.False(ManualInvoiceDeliveryPolicy.CanSubmitAfterPrecheck(status, MarketplaceInvoiceStatus.Invoiced));
        Assert.False(ManualInvoiceDeliveryPolicy.CanSubmitAfterPrecheck(status, MarketplaceInvoiceStatus.Unknown));
    }

    [Fact]
    public void ManualDocumentAndDeliveryStateAreUniqueAtOrderAndPackageScope()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=invoice-model-only;Username=none;Password=none")
            .Options;
        using var db = new AppDbContext(options);
        var documents = db.Model.FindEntityType(typeof(ManualOrderInvoiceDocument))!;
        var activeOrderIndex = Assert.Single(documents.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(ManualOrderInvoiceDocument.TenantId),
                nameof(ManualOrderInvoiceDocument.OrderId),
                nameof(ManualOrderInvoiceDocument.Status)
            }));
        Assert.Equal("IX_manual_order_invoice_documents_TenantId_OrderId_Active", activeOrderIndex.GetDatabaseName());
        Assert.Contains("ACTIVE", activeOrderIndex.GetFilter(), StringComparison.Ordinal);

        var deliveries = db.Model.FindEntityType(typeof(ManualOrderInvoiceDeliveryState))!;
        Assert.Contains(deliveries.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(ManualOrderInvoiceDeliveryState.TenantId),
                nameof(ManualOrderInvoiceDeliveryState.DocumentId),
                nameof(ManualOrderInvoiceDeliveryState.PackageId)
            }));
    }
}
