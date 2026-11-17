using System.Text.Json;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-12 point 3 (16 Nov 2026): the load and save every resumable read state shared (Trendyol orders and returns,
// Hepsiburada orders, packages and returns, Shopify returns, Trendyol order reconciliation). Each state keeps its own
// version and its own rule for when a saved state may be resumed (the accept function: version, bounds, age); this
// class only reads the JSON (an empty cursor, one that is not the state's JSON or one the rule refuses is no state) and
// writes it back with the cursor's version moved.
internal static class ResumableCursor
{
    public static TState? Read<TState>(string? opaqueCursor, Func<TState, bool> accept) where TState : class
    {
        if (string.IsNullOrWhiteSpace(opaqueCursor)) return null;
        try
        {
            return JsonSerializer.Deserialize<TState>(opaqueCursor) is { } state && accept(state) ? state : null;
        }
        catch (JsonException) { return null; }
    }

    public static string Serialize<TState>(TState state) => JsonSerializer.Serialize(state);

    public static void Save<TState>(SyncCursor cursor, TState state)
    {
        cursor.OpaqueCursor = Serialize(state);
        cursor.Version++;
    }
}
