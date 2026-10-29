using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// A connection's full reset (Entegrasyonlar › Verileri Sıfırla › Tam Sıfırlama): every operational record of the
// connection goes while the reset holds all of its sync locks (OperationalDataMaintenanceService.RunFullResetAsync).
public sealed class ConnectionFullResetJobHandler(IOperationalDataMaintenanceService maintenance) : IJobHandler
{
    public bool Handles(string jobType) => jobType == MarketplaceJobTypes.ConnectionFullReset;

    public Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken) =>
        maintenance.RunFullResetAsync(request.TenantId, request.ConnectionId, request.JobId, cancellationToken);
}
