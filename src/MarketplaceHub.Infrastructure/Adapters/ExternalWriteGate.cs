using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Adapters;

// "May this connection write to the marketplace now?" in one place. The answer is made of the server-wide switch
// (FeatureFlags:ExternalWrites, off by the owner's choice), the connection's own switch (SettingsJson
// "ExternalWritesEnabled": true) and the connection's environment and status. Two rules are in use today, kept as they
// were (a drift the owner can unify later):
//   ProductionWrite (n11, Pazarama, PTT AVM): both switches, PRODUCTION, and ACTIVE or VERIFIED, for manual and
//     automatic work alike. A VERIFIED production connection writes.
//   RuntimeWrite (Hepsiburada, Shopify; Trendyol through IntegrationRuntimePolicy with its own settings reader):
//     automatic work needs both switches, PRODUCTION and ACTIVE; manual work may also write to an operational STAGE
//     connection. A VERIFIED production connection does not write. Shopify also needs the verified write capability.
// E-Faturam (manual STAGE, or PRODUCTION with both switches) keeps its own rule in its client. Each adapter turns a
// refusal into its own error code (N11_EXTERNAL_WRITES_DISABLED, SHOPIFY_EXTERNAL_WRITE_CLOSED, ...). Amount rules such
// as PTT AVM's 9,999 stock cap are not part of this gate.
internal static class ExternalWriteGate
{
    internal const string GlobalFlag = "FeatureFlags:ExternalWrites";

    internal static bool GlobalWritesEnabled(IConfiguration configuration) => configuration.GetValue<bool>(GlobalFlag);

    // The connection's switch: only a JSON true counts; unreadable settings keep writes off. The one reader every layer
    // uses (ConnectionSettings.ExternalWritesEnabled, ADAPTOR-DIGER-9).
    internal static bool ConnectionWritesEnabled(string settingsJson) => ConnectionSettings.ExternalWritesEnabled(settingsJson);

    // OWNER DECISION (ADAPTOR-DIGER-9), kept as it is: the ProductionWrite rule (n11, Pazarama, PTT AVM) lets a VERIFIED
    // production connection write; the RuntimeWrite rule (Hepsiburada, Shopify) does not. Setting this to false makes
    // the ProductionWrite rule take ACTIVE connections only, the same status rule as RuntimeWrite's automatic work.
    // E-Faturam's own rule (TrendyolEFaturamHttpClient.CanWrite: a manual STAGE send, or PRODUCTION with both switches)
    // is the other owner decision and stays in that client.
    internal const bool ProductionRuleWritesOnVerifiedConnections = true;

    internal static bool AllowsProductionWrite(PlatformConnection connection, bool globalWritesEnabled) =>
        globalWritesEnabled
        && ConnectionWritesEnabled(connection.SettingsJson)
        && string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(connection.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
            || ProductionRuleWritesOnVerifiedConnections && string.Equals(connection.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase));

    internal static bool AllowsRuntimeWrite(PlatformConnection connection, AdapterContext context, bool globalWritesEnabled) =>
        IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled, ConnectionWritesEnabled(connection.SettingsJson));
}
