using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Ai;

// API-GUVENLIK-3: every size chart is a billed Claude skill run (30-180 s, plus the code container). Nothing stopped a
// second press, a second tab or another user from starting the same run again, nor a runaway client from spending
// without end.

/// <summary>
/// Which Claude runs are going on now, in this API process (the API runs as one instance; the worker never calls
/// Claude). One run per subject (a product's size chart) at a time, and at most <see cref="PerTenant"/> runs of a tenant
/// at once.
/// </summary>
public sealed class AiRunGate
{
    public const int PerTenant = 2;

    private readonly object sync = new();
    private readonly HashSet<(Guid TenantId, string Subject)> running = [];
    private readonly Dictionary<Guid, int> perTenant = [];

    public enum Refusal { None, SubjectBusy, TenantBusy }

    /// <summary>A lease to dispose when the run ends; null with the reason when the run may not start now.</summary>
    public IDisposable? TryEnter(Guid tenantId, string subject, out Refusal refusal)
    {
        lock (sync)
        {
            if (running.Contains((tenantId, subject))) { refusal = Refusal.SubjectBusy; return null; }
            var count = perTenant.GetValueOrDefault(tenantId);
            if (count >= PerTenant) { refusal = Refusal.TenantBusy; return null; }
            running.Add((tenantId, subject));
            perTenant[tenantId] = count + 1;
            refusal = Refusal.None;
            return new Lease(this, tenantId, subject);
        }
    }

    private void Leave(Guid tenantId, string subject)
    {
        lock (sync)
        {
            if (!running.Remove((tenantId, subject))) return;
            var left = perTenant.GetValueOrDefault(tenantId) - 1;
            if (left <= 0) perTenant.Remove(tenantId); else perTenant[tenantId] = left;
        }
    }

    private sealed class Lease(AiRunGate gate, Guid tenantId, string subject) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Leave(tenantId, subject);
        }
    }
}

/// <summary>
/// The most a tenant's Claude skill runs may cost in any 24 hours (the cost the usage log records). Set with
/// <c>Ai:DailySpendLimitUsd</c>; 0 or less turns the ceiling off.
/// </summary>
public sealed record AiSpendLimit(decimal DailyUsd)
{
    public const string ConfigurationKey = "Ai:DailySpendLimitUsd";

    // About 60-120 size charts a day at the measured 0.20-0.40 USD each: far above normal use, low enough that a
    // runaway client or a stolen session stops within a day.
    public const decimal DefaultDailyUsd = 25m;

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public bool Enabled => DailyUsd > 0;

    public static AiSpendLimit From(IConfiguration configuration) =>
        new(decimal.TryParse(configuration[ConfigurationKey], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : DefaultDailyUsd);
}
