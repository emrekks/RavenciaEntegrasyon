using MarketplaceHub.Domain;

namespace MarketplaceHub.Application.Tests;

public sealed class IntegrationRuntimePolicyTests
{
    private static readonly AdapterContext Manual = new(Guid.NewGuid(), Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow);
    private static readonly AdapterContext Automatic = Manual with { Operation = IntegrationOperation.Automatic };

    [Fact]
    public void Active_stage_manual_write_bypasses_evidence_and_write_switches()
    {
        var connection = Connection("STAGE", "ACTIVE");
        Assert.True(IntegrationRuntimePolicy.AllowsManualRead(connection));
        Assert.True(IntegrationRuntimePolicy.AllowsManualWrite(connection, Manual, globalWritesEnabled: false, connectionWritesEnabled: false));
        Assert.False(IntegrationRuntimePolicy.AllowsManualWrite(connection, Automatic, globalWritesEnabled: true, connectionWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection));
    }

    [Fact]
    public void Verified_stage_connection_is_operational_only_for_manual_stage_flows()
    {
        var stage = Connection("STAGE", "VERIFIED");
        var production = Connection("PRODUCTION", "VERIFIED");

        Assert.True(IntegrationRuntimePolicy.AllowsManualRead(stage));
        Assert.True(IntegrationRuntimePolicy.AllowsManualWrite(stage, Manual, globalWritesEnabled: false, connectionWritesEnabled: false));
        Assert.False(IntegrationRuntimePolicy.AllowsManualWrite(stage, Automatic, globalWritesEnabled: true, connectionWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsManualRead(production));
        Assert.False(IntegrationRuntimePolicy.AllowsManualWrite(production, Manual, globalWritesEnabled: true, connectionWritesEnabled: true));
    }

    [Fact]
    public void Production_remains_fail_closed_without_all_write_gates()
    {
        var connection = Connection("PRODUCTION", "ACTIVE");
        Assert.True(IntegrationRuntimePolicy.AllowsManualWrite(connection, Manual, true, true));
        Assert.False(IntegrationRuntimePolicy.AllowsManualWrite(connection, Manual, true, false));
        Assert.True(IntegrationRuntimePolicy.AllowsManualWrite(connection, Manual, true, true));
        Assert.True(IntegrationRuntimePolicy.RequiresSensitiveConfirmation(connection));
    }

    [Fact]
    public void Endpoint_resolution_rejects_unknown_or_crossed_environment()
    {
        var stage = new Uri("https://stage.example.test/");
        var production = new Uri("https://api.example.test/");
        Assert.True(IntegrationRuntimePolicy.TryResolveBaseAddress("STAGE", stage, production, out var resolved));
        Assert.Equal(stage, resolved);
        Assert.False(IntegrationRuntimePolicy.TryResolveBaseAddress("UNKNOWN", stage, production, out _));
        Assert.False(IntegrationRuntimePolicy.TryResolveBaseAddress("STAGE", stage, stage, out _));
    }

    private static PlatformConnection Connection(string environment, string status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        PublicId = Guid.NewGuid(),
        PlatformCode = "TRENDYOL",
        Environment = environment,
        DisplayName = "test",
        ExternalStoreId = "test",
        Status = status,
        ApiVersion = "V2"
    };
}
