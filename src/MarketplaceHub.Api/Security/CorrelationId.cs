namespace MarketplaceHub.Api.Security;

// A client may send X-Correlation-ID to find its request in the logs and audit rows. It is kept only when
// it is a short token of letters, digits and . _ : - (a GUID, an operation id); anything else (spaces,
// line breaks, markup, very long values) would end up verbatim in log lines, audit rows and answers, so a
// fresh id is used instead.
public static class CorrelationId
{
    public const int MaximumLength = 64;

    public static string FromRequest(string? value) => IsAcceptable(value) ? value! : Guid.NewGuid().ToString("N");

    public static bool IsAcceptable(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}
