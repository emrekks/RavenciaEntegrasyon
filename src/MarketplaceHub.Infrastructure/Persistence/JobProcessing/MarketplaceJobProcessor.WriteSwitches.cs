using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3a (16 Nov 2026): the write switches live in JobWriteSwitches; the write jobs still in the
// processor call them through these names.
public sealed partial class MarketplaceJobProcessor
{
    private JobWriteSwitches? writeSwitches;

    private JobWriteSwitches WriteSwitches => writeSwitches ??= new JobWriteSwitches(db, configuration);

    private bool WritesEnabled(string settingsJson) => WriteSwitches.WritesEnabled(settingsJson);

    private Task<bool> ExternalWriteMasterAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WriteSwitches.ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> PttAvmManualWritesAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WriteSwitches.PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> HasHepsiburadaPublicationEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WriteSwitches.HasHepsiburadaPublicationEvidenceAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        WriteSwitches.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);

    private Task<bool> StrictWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        WriteSwitches.StrictWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);

    private Task<int> ExternalWriteDelaySecondsAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        WriteSwitches.ExternalWriteDelaySecondsAsync(tenantId, connectionId, resourceType, cancellationToken);
}
