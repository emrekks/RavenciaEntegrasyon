using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// Errors that belong to no one marketplace: a connection whose stored secret cannot be used, closed external writes,
// an unverified capability and an answer outside the verified contract. Trendyol's mapper forwards to these.
internal static class AdapterErrors
{
    internal static AdapterError Configuration() => new(AdapterErrorClass.Authentication, "CONNECTION_CONFIGURATION_UNAVAILABLE", "Bağlantı veya şifreli credential kullanılamıyor.", null, null, null);
    internal static AdapterError WriteClosed() => new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITES_DISABLED", "Global ve connection dış yazma anahtarları kapalı.", null, null, null);
    internal static AdapterError Unsupported(string message) => new(AdapterErrorClass.NotSupported, "CAPABILITY_NOT_VERIFIED", message, null, null, null);
    internal static AdapterError Contract() => new(AdapterErrorClass.ContractViolation, "REMOTE_CONTRACT_INVALID", "Platform yanıtı doğrulanmış sözleşmeyle eşleşmedi.", null, null, null);
}
