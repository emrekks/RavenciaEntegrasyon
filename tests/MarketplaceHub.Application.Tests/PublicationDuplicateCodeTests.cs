using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The connection-wide barcode check compares codes without regard to case (T2-D29); the database side is covered by
// StockPricePostgreSqlTests.Publication_RefusesABarcodeInAnotherCaseOrHeldByAnImportedLink.
public sealed class PublicationDuplicateCodeTests
{
    [Fact]
    public void UpperCodes_TrimsUpperCasesAndDropsBlanksAndRepeats()
    {
        Assert.Equal(["AB-1000-X", "IMP-77"], ProductPublicationComposer.UpperCodes([" ab-1000-x ", "AB-1000-X", "imp-77", " ", ""]));
    }
}
