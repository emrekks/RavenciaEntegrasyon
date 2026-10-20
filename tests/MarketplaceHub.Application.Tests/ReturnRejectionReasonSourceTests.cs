using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReturnRejectionReasonSourceTests
{
    // İ11: neither marketplace has a reasons endpoint; the compiled lists follow their documentation.
    [Fact]
    public void HepsiburadaRejectionReasonsAreTheDocumentedListAndNeedNoEvidence()
    {
        string[] documented =
        [
            "CustomerReturnedWrongItem", "ProductIsDamaged", "MissingQuantity", "NoSuchAccessory", "BoxIsEmptyWithReport", "BoxIsEmptyWithoutReport",
            "SomePartsOrSomeAccessoriesOrSomePapersAreMissing", "ReturnedProductIsNotDelivered", "NewProductWillBeSent", "ExtraProductHasBeenReturned",
            "ProductNotWrong", "ProductNotDefective", "StockProblem", "ReturnedProductHasAccountOrPassword", "MarkedAsServiceProcess",
            "ProductSentComplete", "MissingItemOrPartCannotBeSupplied", "ClaimedComponentIsNotPartOfTheProduct", "InvoiceReplacesWarranty",
            "PartialShipmentMissingPackageWillBeDelivered", "CustomerProblemSolved", "Other"
        ];

        Assert.Equal(documented, HepsiburadaHttpClient.ClaimRejectionReasons.Select(reason => reason.Id));
        Assert.All(HepsiburadaHttpClient.ClaimRejectionReasons, reason => Assert.False(reason.EvidenceRequired));
    }

    [Fact]
    public void PazaramaRejectionReasonsAreTheDocumentedEnumAndOnlyEmptyOrUndeliveredPackagesSkipPhotos()
    {
        Assert.Equal(Enumerable.Range(1, 12).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)), PazaramaReturnMapper.RejectReasons.Select(reason => reason.Id));
        Assert.Equal(["10", "11"], PazaramaReturnMapper.RejectReasons.Where(reason => !reason.EvidenceRequired).Select(reason => reason.Id));
    }
}
