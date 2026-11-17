using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters;

// "May this connection write to the marketplace now?" in one place. The answer is made of the server-wide switch
// (FeatureFlags:ExternalWrites, off by the owner's choice), the connection's own switch (SettingsJson
// "ExternalWritesEnabled": true) and the connection's environment and status.
// OWNER DECISION (ADAPTOR-DIGER-9, 16 Nov 2026): one write rule. On every marketplace a write needs the connection's
// own switch; a production connection must be ACTIVE (a VERIFIED production connection no longer writes on n11,
// Pazarama and PTT AVM either, as on Hepsiburada and Shopify).
//   ProductionWrite (n11, Pazarama, PTT AVM): both switches, PRODUCTION and ACTIVE, for manual and automatic work alike.
//   RuntimeWrite (Hepsiburada, Shopify; Trendyol through IntegrationRuntimePolicy with its own settings reader):
//     automatic work needs both switches, PRODUCTION and ACTIVE; manual work may also write to an operational STAGE
//     connection. Shopify also needs the verified write capability.
// The two agree on every production connection; they differ only where n11, Pazarama and PTT AVM have no STAGE writes.
// E-Faturam (manual STAGE, or PRODUCTION with both switches) keeps its own rule in its client: it already requires the
// connection's switch in production, and it is the invoice provider's gate, which this decision does not widen or move.
// Each adapter turns a refusal into its own error code (N11_EXTERNAL_WRITES_DISABLED, SHOPIFY_EXTERNAL_WRITE_CLOSED,
// ...). Amount rules such as PTT AVM's 9,999 stock cap are not part of this gate.
internal static class ExternalWriteGate
{
    internal const string GlobalFlag = "FeatureFlags:ExternalWrites";

    internal static bool GlobalWritesEnabled(IConfiguration configuration) => configuration.GetValue<bool>(GlobalFlag);

    // The connection's switch: only a JSON true counts; unreadable settings keep writes off. The one reader every layer
    // uses (ConnectionSettings.ExternalWritesEnabled, ADAPTOR-DIGER-9).
    internal static bool ConnectionWritesEnabled(string settingsJson) => ConnectionSettings.ExternalWritesEnabled(settingsJson);

    internal static bool AllowsProductionWrite(PlatformConnection connection, bool globalWritesEnabled) =>
        globalWritesEnabled
        && ConnectionWritesEnabled(connection.SettingsJson)
        && IntegrationRuntimePolicy.IsProduction(connection)
        && IntegrationRuntimePolicy.IsActive(connection);

    internal static bool AllowsRuntimeWrite(PlatformConnection connection, AdapterContext context, bool globalWritesEnabled) =>
        IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled, ConnectionWritesEnabled(connection.SettingsJson));
}
