using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace MarketplaceHub.Api.Security;

// Two kinds of address carry a bearer secret in their path: the public invoice links
// (/api/v1/public/invoice-documents/{token}/..., /api/v1/public/manual-invoice-documents/{token}/...) and the
// webhook routes (/api/v1/hooks/{connectionPublicId}/{routeToken}/...; on a Shopify ROUTE_TOKEN subscription
// the route token is the only key). ASP.NET Core opens a scope with the request path for every request, and
// Serilog writes it as RequestPath on every event of that request (routing's "Executing endpoint", the
// invoice-link and webhook warnings), so the secret reached the API's log in plain text (security review
// 2026-10-29 A-1). This enricher replaces the secret segment with *** in every text property of an event (the
// rendered message is drawn from the same properties); the connection id, the event path and every other
// address are kept as they are. Nothing reads the raw path back from the log (no alert or script matches it).
public sealed partial class SecretPathEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<LogEventProperty>? masked = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (Masked(value) is { } replacement) (masked ??= []).Add(new LogEventProperty(name, replacement));
        }
        if (masked is null) return;
        foreach (var property in masked) logEvent.AddOrUpdateProperty(property);
    }

    // The text with the secret segment of the two address kinds masked; any other text unchanged.
    public static string MaskPath(string value) =>
        value.Contains("/api/v1/", StringComparison.OrdinalIgnoreCase) ? SecretSegment().Replace(value, "$1" + Mask) : value;

    // Text values and the scope list (a sequence of them); null when nothing changed.
    private static LogEventPropertyValue? Masked(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                var result = MaskPath(text);
                return result == text ? null : new ScalarValue(result);
            case SequenceValue sequence:
                var elements = sequence.Elements.Select(Masked).ToList();
                return elements.All(element => element is null) ? null : new SequenceValue(sequence.Elements.Select((element, index) => elements[index] ?? element));
            default:
                return null;
        }
    }

    [GeneratedRegex(@"(/api/v1/(?:hooks/[^/?#\s""]+|public/(?:manual-)?invoice-documents)/)[^/?#\s""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegment();
}
