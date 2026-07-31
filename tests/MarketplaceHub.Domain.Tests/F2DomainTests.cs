using MarketplaceHub.Domain;

namespace MarketplaceHub.Domain.Tests;

public sealed class F2DomainTests
{
    [Theory]
    [InlineData(10, 2, 8)]
    [InlineData(2, 10, 0)]
    public void Inventory_projection_never_publishes_negative_availability(decimal onHand, decimal reserved, decimal expected) =>
        Assert.Equal(expected, InventoryProjection.Available(onHand, reserved));

    [Fact]
    public void Safety_stock_is_subtracted_after_reservations() =>
        Assert.Equal(6m, InventoryProjection.ChannelPublishable(10m, 4m));

    [Fact]
    public void Money_uses_four_decimal_precision_and_uppercase_currency()
    {
        Assert.Equal(12.3457m, Money.Create(12.34567m, "TRY").Amount);
        Assert.Throws<ArgumentException>(() => Money.Create(1m, "try"));
    }

    [Fact]
    public void Import_state_machine_enforces_the_binding_allowlist()
    {
        Assert.True(ImportStateMachine.CanTransition(ImportSessionStatus.Created, ImportSessionStatus.Fetching));
        Assert.True(ImportStateMachine.CanTransition(ImportSessionStatus.Applying, ImportSessionStatus.PartiallyCompleted));
        Assert.False(ImportStateMachine.CanTransition(ImportSessionStatus.Created, ImportSessionStatus.Completed));
        Assert.False(ImportStateMachine.CanTransition(ImportSessionStatus.Completed, ImportSessionStatus.Applying));
    }

    [Fact]
    public void Typed_attribute_assignment_requires_exactly_one_value_column()
    {
        Assert.True(new ProductAttributeAssignment { TextValue = "Pamuk" }.HasExactlyOneTypedValue());
        Assert.False(new ProductAttributeAssignment { TextValue = "Pamuk", NumberValue = 1 }.HasExactlyOneTypedValue());
        Assert.False(new ProductAttributeAssignment().HasExactlyOneTypedValue());
    }
}
