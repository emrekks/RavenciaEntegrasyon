namespace MarketplaceHub.Application;

// Ayarlar › Sistem Ayarları › Veri: the tenant's records go category by category; choosing every category is Paneli
// Tamamen Sıfırla (owner request, 12 Nov 2026), the tenant starting again from zero. The tenant, its users and their
// sign-in, the marketplace and e-invoice connections with their credentials and settings, and the panel's configuration
// stay. A reset runs as one background job (PANEL_FACTORY_RESET) the panel follows step by step.
public static class PanelJobTypes
{
    public const string FactoryReset = "PANEL_FACTORY_RESET";
}

// Categories null or empty: every category (Paneli Tamamen Sıfırla, confirmed with PANELİ SIFIRLA); otherwise the chosen
// ones with what they require (confirmed with Verileri sil, or PANELİ SIFIRLA).
public sealed record PanelFactoryResetCommand(string? Confirmation, IReadOnlyList<string>? Categories = null);

// One category: what goes, where the page lists it, and how many records of it the tenant has now.
public sealed record PanelFactoryResetGroupView(string Key, string Label, string Description, string Section, IReadOnlyList<string> Requires, long Count);

public sealed record PanelFactoryResetJobView(Guid JobId, string Status, bool Full, IReadOnlyList<string> Categories, IReadOnlyList<string> Steps, int Step, string? ProgressLabel, int? ProgressPercent, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

// Steps: a full reset's steps; Kept: what stays.
public sealed record PanelFactoryResetView(IReadOnlyList<PanelFactoryResetGroupView> Groups, long Total, IReadOnlyList<string> Steps, IReadOnlyList<string> Kept, string ConfirmationWord, string PartialConfirmationWord, PanelFactoryResetJobView? Job);

public interface IPanelFactoryResetService
{
    Task<ServiceResult<PanelFactoryResetView>> GetAsync(Guid tenantId, CancellationToken cancellationToken);
    // currentSessionId: the session that asked, which stays signed in after a full reset; every other session ends.
    Task<ServiceResult<PanelFactoryResetView>> RequestAsync(Guid tenantId, Guid actorUserId, Guid? currentSessionId, PanelFactoryResetCommand command, string correlationId, CancellationToken cancellationToken);
    // The job's work; jobId is the running job, which stays (the panel follows it).
    Task<JobExecutionResult> RunAsync(Guid tenantId, Guid jobId, string payloadJson, CancellationToken cancellationToken);
}
