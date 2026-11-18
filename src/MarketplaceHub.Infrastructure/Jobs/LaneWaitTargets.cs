using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Jobs;

// Worker lanes step 1 (19 Nov 2026): how long a job of each lane may wait in the queue before it is leased (the target its
// p95 wait is held to), and the alert raised when the target keeps being missed.
//
// Targets (Worker:LaneTargets:<lane> in seconds, 1..86400, overrides one). Measured on the live week 13-18 Nov 2026
// (first-attempt wait as İşlem Takibi shows it; replay with today's 20 slots):
//   user 5 s                    a user waits for it; p95 2.4 s, at most 3 s (replay with two tenants p95 6.3 s);
//   hot and the platform order lanes 60 s   new orders, webhooks and writes; p95 0-0.1 s, at most 28 s (order polling
//                                           runs every 3 minutes, so a minute's wait still shows a new order the same poll);
//   order-status, hepsiburada-status, returns-read 300 s   periodic scans of open orders and returns; p95 under 1 s, at most 30 s;
//   product-catalog 60 s        p95 18 s, at most 26 s with three slots (one slot gave 291 s / 392 s on 17 Nov);
//   initial-data-sync 120 s     a new store's first import; at most 0 s with three slots (one slot: 203 s);
//   background 600 s            reconciliations and other scheduled reads; p95 0.1 s; the longest live wait (513 s) was
//                               a lock wait behind the same connection's reference read, not a slot wait.
// Replayed with the alert rule below, the live week opens no alert; with the old single catalog slot the 17 Nov catalog wave
// opens one for 11 minutes, and a marketplace quota full for 30 minutes opens one per lane it holds.
public static class LaneWaitTargets
{
    public static TimeSpan DefaultFor(string lane) => TimeSpan.FromSeconds(lane switch
    {
        "user" => 5,
        "hot" or "hepsiburada-orders" or "pazarama-orders" or "pttavm-orders" => 60,
        "order-status" or "hepsiburada-status" or "returns-read" => 300,
        "product-catalog" => 60,
        "initial-data-sync" => 120,
        "background" => 600,
        _ => 600
    });

    public static TimeSpan For(IConfiguration? configuration, string lane)
    {
        var fallback = DefaultFor(lane);
        var seconds = configuration?.GetValue($"Worker:LaneTargets:{lane}", (int)fallback.TotalSeconds) ?? (int)fallback.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86_400));
    }

    // The operational issue a lane's sustained miss opens (ops.operational_issues), one per tenant and lane.
    public const string IssueCode = "QUEUE_WAIT_TARGET_EXCEEDED";
    public static string IssueKey(string lane) => $"queue-wait:{lane}";

    public static string DisplayName(string lane) => lane switch
    {
        "user" => "Kullanıcı İşlemleri",
        "initial-data-sync" => "İlk Aktarım",
        "order-status" => "Sipariş Durumu Taraması",
        "returns-read" => "İade Okuma",
        "product-catalog" => "Ürün Kataloğu",
        "hepsiburada-status" => "Hepsiburada Durum Taraması",
        "hepsiburada-orders" => "Hepsiburada Siparişleri",
        "pazarama-orders" => "Pazarama Siparişleri",
        "pttavm-orders" => "PTT AVM Siparişleri",
        "hot" => "Öncelikli İşler",
        "background" => "Arka Plan",
        _ => lane
    };

    public static string IssueSummary(string lane, TimeSpan target, double? recentP95Seconds, double? oldestWaitSeconds) =>
        $"{DisplayName(lane)} kuyruğunda bekleme hedefi ({Seconds(target.TotalSeconds)}) sürekli aşılıyor"
        + (recentP95Seconds is { } p95 ? $"; son işlerin %95'i {Seconds(p95)} içinde başladı" : "")
        + (oldestWaitSeconds is { } oldest && oldest > 0 ? $"; en uzun bekleyen iş {Seconds(oldest)}dır sırada" : "")
        + ". Bekleme hedefin altına indiğinde bu uyarı kendiliğinden kapanır.";

    private static string Seconds(double seconds) => seconds < 120
        ? $"{Math.Round(seconds).ToString(System.Globalization.CultureInfo.InvariantCulture)} sn"
        : $"{Math.Round(seconds / 60, 1).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} dk";
}

// The alert rule: a lane is breached at one look when the p95 wait of the jobs it leased in the recent window is over its
// target, or a job it could run has waited longer than the target. One slow job does not open an alert: the breach must hold
// at every look for SustainFor (a single late job leaves the recent window before that), and an open alert closes only after
// the lane has stayed within its target for ClearAfter. Whether the alert is open comes from the issue itself, so a worker
// restart neither forgets an open alert nor opens a second one.
public sealed class LaneWaitAlarm(TimeSpan sustainFor, TimeSpan clearAfter)
{
    private readonly Dictionary<(Guid TenantId, string Lane), (DateTimeOffset? BreachedSince, DateTimeOffset? HealthySince)> states = new();

    public TimeSpan SustainFor { get; } = sustainFor;
    public TimeSpan ClearAfter { get; } = clearAfter;

    public LaneAlarmTransition Observe(Guid tenantId, string lane, bool breached, bool open, DateTimeOffset now)
    {
        var key = (tenantId, lane);
        states.TryGetValue(key, out var state);
        if (breached)
        {
            state = (state.BreachedSince ?? now, null);
            states[key] = state;
            return !open && now - state.BreachedSince >= SustainFor ? LaneAlarmTransition.Open : LaneAlarmTransition.None;
        }
        state = (null, state.HealthySince ?? now);
        states[key] = state;
        return open && now - state.HealthySince >= ClearAfter ? LaneAlarmTransition.Resolve : LaneAlarmTransition.None;
    }
}

public enum LaneAlarmTransition
{
    None,
    Open,
    Resolve
}
