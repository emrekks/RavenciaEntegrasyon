namespace MarketplaceHub.Application;

// Ayarlar › Sistem Ayarları › Veri › Paneli Tamamen Sıfırla (owner request, 12 Nov 2026): the tenant starts again from
// zero. Every record of its work goes (orders, products, invoices, stock, finance, jobs and all logs); the tenant, its
// users and their sign-in, the marketplace and e-invoice connections with their credentials and settings, and the
// panel's configuration stay. It runs as a background job (PANEL_FACTORY_RESET) the panel follows step by step.
public static class PanelJobTypes
{
    public const string FactoryReset = "PANEL_FACTORY_RESET";
}

public sealed record PanelFactoryResetCommand(string? Confirmation);

// One line of the list the panel shows: what goes, and how many records of it the tenant has now.
public sealed record PanelFactoryResetGroupView(string Key, string Label, long Count);

public sealed record PanelFactoryResetJobView(Guid JobId, string Status, int Step, string? ProgressLabel, int? ProgressPercent, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

// Steps: the job's steps in order (Step in the job view is the index of the running one); Kept: what stays.
public sealed record PanelFactoryResetView(IReadOnlyList<PanelFactoryResetGroupView> Groups, long Total, IReadOnlyList<string> Steps, IReadOnlyList<string> Kept, string ConfirmationWord, PanelFactoryResetJobView? Job);

public interface IPanelFactoryResetService
{
    Task<ServiceResult<PanelFactoryResetView>> GetAsync(Guid tenantId, CancellationToken cancellationToken);
    // currentSessionId: the session that asked, which stays signed in; every other session of the tenant ends.
    Task<ServiceResult<PanelFactoryResetView>> RequestAsync(Guid tenantId, Guid actorUserId, Guid? currentSessionId, PanelFactoryResetCommand command, string correlationId, CancellationToken cancellationToken);
    // The job's work; jobId is the running job, which stays (the panel follows it).
    Task<JobExecutionResult> RunAsync(Guid tenantId, Guid jobId, string payloadJson, CancellationToken cancellationToken);
}
