using Xunit;

namespace MarketplaceHub.Application.Tests;

// A model code belongs to one product: a new card may not take one the catalog already holds.
public sealed class ModelCodeUniquenessPolicyTests
{
    [Fact]
    public void Keys_IgnoreCaseSpacesAndSeparators_AndSkipEmptyCodes()
    {
        Assert.Equal(["MZ021Y-CD"], ModelCodeUniquenessPolicy.Keys(["MZ021Y-CD", " mz021y cd ", "Mz021Y_Cd", null, "", "   "]));
        Assert.Empty(ModelCodeUniquenessPolicy.Keys([null, " "]));
    }

    [Fact]
    public void FirstHolder_FindsTheCardWithTheSameKey_InTheGivenOrder()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var rows = new[]
        {
            new ModelCodeUniquenessPolicy.Holder(Guid.CreateVersion7(), "Başka", "AB-10"),
            new ModelCodeUniquenessPolicy.Holder(first, "Kadın Bluz Siyah", "mz021y-cd"),
            new ModelCodeUniquenessPolicy.Holder(second, "Kadın Bluz Beyaz", "MZ021Y CD")
        };

        Assert.Equal(first, ModelCodeUniquenessPolicy.FirstHolder(ModelCodeUniquenessPolicy.Keys(["MZ021Y_CD"]), rows)!.ProductId);
        Assert.Null(ModelCodeUniquenessPolicy.FirstHolder(ModelCodeUniquenessPolicy.Keys(["MZ021Y"]), rows));
        Assert.Null(ModelCodeUniquenessPolicy.FirstHolder(ModelCodeUniquenessPolicy.Keys([""]), rows));
    }

    [Fact]
    public void Message_NamesTheHolderAndTheWayToAddAColour()
    {
        Assert.Equal("Bu model kodu 'Kadın Bluz' ürününde kullanılıyor. Yeni renk veya beden eklemek için o ürünü düzenleyin.", ModelCodeUniquenessPolicy.Message("Kadın Bluz"));
    }
}
