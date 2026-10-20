using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Marketplace;

// A marketplace's HTTP status describes the call between the panel and the marketplace, not the
// user's request to the panel. Passed through as it is, the store's rejected credentials (401/403)
// read as the user's own session ending (the edge clears the browser cache on every API 401 and the
// client drops its CSRF token), and the marketplace's quota (429) reads as the panel's own limit.
// Every adapter error that reaches the panel's API goes through this single mapping.
public static class MarketplaceErrorStatus
{
    public static int ToPanelStatus(int? marketplaceStatus, AdapterErrorClass? errorClass = null) => marketplaceStatus switch
    {
        401 or 403 => StatusCodes.Status502BadGateway,
        429 => StatusCodes.Status503ServiceUnavailable,
        404 => StatusCodes.Status404NotFound,
        >= 400 and < 500 => StatusCodes.Status422UnprocessableEntity,
        >= 500 and < 600 => StatusCodes.Status502BadGateway,
        // No usable HTTP status (network failure, contract violation): the error class decides.
        _ => errorClass switch
        {
            AdapterErrorClass.RateLimit => StatusCodes.Status503ServiceUnavailable,
            AdapterErrorClass.NotFound => StatusCodes.Status404NotFound,
            AdapterErrorClass.Validation or AdapterErrorClass.BusinessConflict => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status502BadGateway
        }
    };

    public static int ToPanelStatus(AdapterError? error) => ToPanelStatus(error?.HttpStatus, error?.Class);

    public static ServiceError ToServiceError(AdapterError? error, string fallbackCode = "ADAPTER_ERROR", string fallbackMessage = "Pazaryeri yanıtı alınamadı.") =>
        new(error?.Code ?? fallbackCode, error?.SafeMessage ?? fallbackMessage, ToPanelStatus(error));

    // For service results that still carry a marketplace's status as their own. Only the statuses that
    // would read as the panel's (401/403 session, 429 rate limit) are moved; the service's own 404, 409
    // and 422 answers keep their meaning.
    public static ServiceError ForPanel(ServiceError error) => error.Status switch
    {
        401 or 403 => error with { Status = StatusCodes.Status502BadGateway },
        429 => error with { Status = StatusCodes.Status503ServiceUnavailable },
        < 400 or > 599 => error with { Status = StatusCodes.Status502BadGateway },
        _ => error
    };

    public static ServiceResult<T> ForPanel<T>(ServiceResult<T> result) => result.Succeeded ? result : result with { Error = ForPanel(result.Error!) };
}
