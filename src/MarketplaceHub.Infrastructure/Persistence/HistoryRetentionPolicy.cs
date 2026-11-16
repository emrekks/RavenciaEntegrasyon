namespace MarketplaceHub.Infrastructure.Persistence;

// Retention periods of the history tables (VERITABANI-DAGITIM-13, docs/SAKLAMA-SURELERI.md). Every table is kept forever
// unless the owner sets Worker:HistoryRetention:<Key>Days; a period shorter than the table's floor is raised to the floor
// (keeping more is always safe), and nothing is kept longer than ten years on purpose.
public static class HistoryRetentionPolicy
{
    public sealed record Table(string Key, string Name, int MinimumDays);

    public static readonly Table AuditLogs = new("AuditLog", "ops.audit_logs", 730);
    public static readonly Table AiUsageLogs = new("AiUsageLog", "ops.ai_usage_logs", 90);
    public static readonly Table ChannelPriceHistory = new("ChannelPriceHistory", "inventory.channel_price_history", 365);
    public static readonly Table ProductChangeEntries = new("ProductChangeEntry", "catalog.product_change_entries", 365);
    public static readonly Table OrderStatusHistory = new("OrderStatusHistory", "sales.order_status_history", 1095);

    public static readonly IReadOnlyList<Table> Tables = [AuditLogs, AiUsageLogs, ChannelPriceHistory, ProductChangeEntries, OrderStatusHistory];

    public const int MaximumDays = 3650;

    public static string SettingName(Table table) => $"Worker:HistoryRetention:{table.Key}Days";

    // null (unset, empty, 0 or negative): keep forever.
    public static TimeSpan? Period(Table table, int? configuredDays) =>
        configuredDays is > 0 ? TimeSpan.FromDays(Math.Clamp(configuredDays.Value, table.MinimumDays, MaximumDays)) : null;
}
