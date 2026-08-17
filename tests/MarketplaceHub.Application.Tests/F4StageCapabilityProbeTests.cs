using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Application.Tests;

public sealed class F4StageCapabilityProbeTests
{
    [Fact]
    public void Safe_pre_submit_scope_failure_is_replayable_only_on_the_pinned_stage_account()
    {
        var stage = Connection("STAGE", "Ravencia - Ravencia");

        Assert.True(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.ManualReview, "EFATURAM_TOKEN_SCOPE_MISSING", null, "EARSIVFATURA", stage));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.ManualReview, "EFATURAM_ACCESS_TOKEN_REJECTED", null, "EARSIVFATURA", stage));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.ManualReview, "EFATURAM_TOKEN_SCOPE_MISSING", "remote-reference", "EARSIVFATURA", stage));
    }

    [Fact]
    public void Production_or_non_pinned_connections_never_receive_the_stage_probe_action()
    {
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Ready, null, null, "EARSIVFATURA", Connection("PRODUCTION", "Ravencia - Ravencia")));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Ready, null, null, "EARSIVFATURA", Connection("STAGE", "other-store")));
    }

    private static PlatformConnection Connection(string environment, string store) => new()
    {
        PlatformCode = "TRENDYOL_EFATURAM",
        Environment = environment,
        DisplayName = "E-Faturam",
        ExternalStoreId = store,
        Status = "VERIFIED",
        ApiVersion = "1.0.0"
    };
}
