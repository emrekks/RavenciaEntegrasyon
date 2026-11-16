namespace MarketplaceHub.Infrastructure.Persistence;

// Retention periods of the history tables (VERITABANI-DAGITIM-13, docs/SAKLAMA-SURELERI.md). Each table has a default the
// owner accepted on 16 Nov 2026 (DefaultDays; null = kept forever) that applies while Worker:HistoryRetention:<Key>Days is
// unset or empty. A whole number of days overrides it; "0" is the one way to say "never purge". A period shorter than the
// table's floor is raised to the floor (keeping more is always safe), and nothing is kept longer than ten years on purpose.
public static class HistoryRetentionPolicy
{
    public sealed record Table(string Key, string Name, int MinimumDays, int? DefaultDays);

    public static readonly Table AuditLogs = new("AuditLog", "ops.audit_logs", 730, DefaultDays: null);
    public static readonly Table AiUsageLogs = new("AiUsageLog", "ops.ai_usage_logs", 90, DefaultDays: 365);
    public static readonly Table ChannelPriceHistory = new("ChannelPriceHistory", "inventory.channel_price_history", 365, DefaultDays: 730);
    public static readonly Table ProductChangeEntries = new("ProductChangeEntry", "catalog.product_change_entries", 365, DefaultDays: 730);
    public static readonly Table OrderStatusHistory = new("OrderStatusHistory", "sales.order_status_history", 1095, DefaultDays: null);

    public static readonly IReadOnlyList<Table> Tables = [AuditLogs, AiUsageLogs, ChannelPriceHistory, ProductChangeEntries, OrderStatusHistory];

    public const int MaximumDays = 3650;

    // The value that keeps a table forever, whatever its default.
    public const string NeverPurge = "0";

    public static string SettingName(Table table) => $"Worker:HistoryRetention:{table.Key}Days";

    // null: keep forever.
    private static TimeSpan? Days(Table table, int? days) =>
        days is > 0 ? TimeSpan.FromDays(Math.Clamp(days.Value, table.MinimumDays, MaximumDays)) : null;

    // The setting as the owner typed it into production.env. Unset or empty (Compose passes an unset variable as an empty
    // string) takes the table's default; "0" keeps the table forever; a positive whole number of days is the period.
    // Anything else ("730gün", "2y", "-5") keeps the table forever and is reported as invalid: never purging is the safe
    // side, and a typo must not stop the worker (a typed GetValue<int?> throws on it while the worker starts).
    public static TimeSpan? Period(Table table, string? configured, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(configured)) return Days(table, table.DefaultDays);
        if (!int.TryParse(configured.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days))
        {
            invalid = true;
            return null;
        }
        return Days(table, days);
    }
}
