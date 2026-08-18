using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Application.Tests;

public sealed class F4StageCapabilityProbeTests
{
    [Fact]
    public void Local_payload_failure_without_external_reference_can_be_retried_safely()
    {
        Assert.True(F4BillingService.CanRetryLocalPayloadFailure(InvoiceStatus.Rejected, "EFATURAM_FISCAL_PAYLOAD_INVALID", null));
        Assert.False(F4BillingService.CanRetryLocalPayloadFailure(InvoiceStatus.Rejected, "EFATURAM_REQUEST_REJECTED", null));
        Assert.False(F4BillingService.CanRetryLocalPayloadFailure(InvoiceStatus.Rejected, "EFATURAM_FISCAL_PAYLOAD_INVALID", "remote-reference"));
    }

    [Fact]
    public void Authentication_failure_without_external_reference_can_be_retried_safely()
    {
        Assert.True(F4BillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", null));
        Assert.True(F4BillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, "EFATURAM_AUTHENTICATION_FAILED", null));
        Assert.False(F4BillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", "remote-reference"));
        Assert.False(F4BillingService.CanRetryPreProviderFailure(InvoiceStatus.Submitted, null, null));
    }

    [Fact]
    public void Safe_no_external_reference_authentication_failures_are_replayable_only_on_the_pinned_stage_account()
    {
        var stage = Connection("STAGE", "Ravencia - Ravencia");

        Assert.True(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.ManualReview, "EFATURAM_TOKEN_SCOPE_MISSING", null, "EARSIVFATURA", stage));
        Assert.True(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", null, "EARSIVFATURA", stage));
        Assert.True(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Submitting, "EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING", null, "EARSIVFATURA", stage));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", "remote-reference", "EARSIVFATURA", stage));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.ManualReview, "EFATURAM_TOKEN_SCOPE_MISSING", "remote-reference", "EARSIVFATURA", stage));
    }

    [Fact]
    public void Production_or_non_pinned_connections_never_receive_the_stage_probe_action()
    {
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Ready, null, null, "EARSIVFATURA", Connection("PRODUCTION", "Ravencia - Ravencia")));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Ready, null, null, "EARSIVFATURA", Connection("STAGE", "other-store")));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", null, "EARSIVFATURA", Connection("PRODUCTION", "Ravencia - Ravencia")));
        Assert.False(F4BillingService.AllowsStageCapabilityProbe(InvoiceStatus.Submitting, "EFATURAM_ACCESS_TOKEN_REJECTED", null, "EARSIVFATURA", Connection("STAGE", "other-store")));
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
