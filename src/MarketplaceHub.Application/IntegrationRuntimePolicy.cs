using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

/// <summary>
/// Keeps the environment boundary and the manual/automatic split in one place.
/// A STAGE connection can be exercised manually without release-evidence gates;
/// every other environment fails closed unless it is explicitly PRODUCTION.
/// </summary>
public static class IntegrationRuntimePolicy
{
    public static bool IsStage(PlatformConnection connection) => string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase);
    public static bool IsProduction(PlatformConnection connection) => string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupportedEnvironment(PlatformConnection connection) => IsStage(connection) || IsProduction(connection);
    public static bool IsActive(PlatformConnection connection) => string.Equals(connection.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    // A successful Stage connection test promotes DRAFT to VERIFIED. That is an
    // authenticated, operational Stage connection; Production remains stricter.
    public static bool IsOperationalStage(PlatformConnection connection) =>
        IsStage(connection)
        && string.IsNullOrWhiteSpace(connection.LastErrorCode)
        && (IsActive(connection) || string.Equals(connection.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase));
    public static bool IsManualStage(PlatformConnection connection, AdapterContext context) =>
        IsOperationalStage(connection) && context.Operation == IntegrationOperation.Manual;

    public static bool AllowsManualRead(PlatformConnection connection) =>
        IsOperationalStage(connection) || (IsProduction(connection) && IsActive(connection));

    public static bool AllowsManualWrite(PlatformConnection connection, AdapterContext context, bool globalWritesEnabled, bool connectionWritesEnabled) =>
        IsManualStage(connection, context)
        || (IsProduction(connection) && IsActive(connection) && globalWritesEnabled && connectionWritesEnabled);

    public static bool RequiresSensitiveConfirmation(PlatformConnection connection) => !IsStage(connection);

    public static bool TryResolveBaseAddress(string environment, Uri stageBaseAddress, Uri productionBaseAddress, out Uri baseAddress)
    {
        baseAddress = default!;
        if (!stageBaseAddress.IsAbsoluteUri || !productionBaseAddress.IsAbsoluteUri
            || stageBaseAddress.Scheme != Uri.UriSchemeHttps || productionBaseAddress.Scheme != Uri.UriSchemeHttps
            || Uri.Compare(stageBaseAddress, productionBaseAddress, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            return false;
        if (string.Equals(environment, "STAGE", StringComparison.OrdinalIgnoreCase)) { baseAddress = stageBaseAddress; return true; }
        if (string.Equals(environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)) { baseAddress = productionBaseAddress; return true; }
        return false;
    }
}

public enum IntegrationOperation { Manual, Automatic }
