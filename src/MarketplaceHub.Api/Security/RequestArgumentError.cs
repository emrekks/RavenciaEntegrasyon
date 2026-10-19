namespace MarketplaceHub.Api.Security;

// An ArgumentException that reaches an endpoint becomes a 400. Only the panel's own request checks
// (page size, cursor) are shown as they are; any other message comes from deeper code, is not meant
// for the user and may describe internals, so a plain message is shown instead.
public static class RequestArgumentError
{
    public static (string Code, string Message) From(ArgumentException exception)
    {
        var cursor = exception.ParamName is "cursor" or "after"
            || exception.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase);
        var ownCheck = exception.GetType() == typeof(ArgumentException)
            && (exception.ParamName is null or "cursor" or "after" or "limit");
        var message = ownCheck ? exception.Message.Split(" (Parameter", 2)[0] : "İstek geçersiz; değerleri kontrol edip yeniden deneyin.";
        return (cursor ? "INVALID_CURSOR" : "INVALID_REQUEST", message);
    }
}
