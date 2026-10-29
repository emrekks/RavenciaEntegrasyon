using MarketplaceHub.Api.Security;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A-1 (2026-10-29): the request scope's RequestPath put the invoice-link and webhook route tokens in the API log.
public sealed class SecretPathEnricherTests
{
    private const string Secret = "s3cr3t-Route_Token";
    private const string Connection = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

    [Theory]
    [InlineData("/api/v1/hooks/" + Connection + "/" + Secret, "/api/v1/hooks/" + Connection + "/***")]
    [InlineData("/api/v1/hooks/" + Connection + "/" + Secret + "/LineItems/B17553DE/Cancel", "/api/v1/hooks/" + Connection + "/***/LineItems/B17553DE/Cancel")]
    [InlineData("/API/V1/HOOKS/" + Connection + "/" + Secret + "/orders", "/API/V1/HOOKS/" + Connection + "/***/orders")]
    [InlineData("/api/v1/public/invoice-documents/" + Secret + "/content", "/api/v1/public/invoice-documents/***/content")]
    [InlineData("/api/v1/public/invoice-documents/" + Secret + "/invoice.pdf", "/api/v1/public/invoice-documents/***/invoice.pdf")]
    [InlineData("/api/v1/public/manual-invoice-documents/" + Secret + "/content", "/api/v1/public/manual-invoice-documents/***/content")]
    [InlineData("https://panel.example/api/v1/public/invoice-documents/" + Secret + "/content?x=1", "https://panel.example/api/v1/public/invoice-documents/***/content?x=1")]
    public void RequestPath_HasTheSecretSegmentMasked_InTheEventAndItsJsonLine(string path, string expected)
    {
        var (logEvent, json) = LogThroughTheHostingScope(path);

        Assert.Equal(expected, ((ScalarValue)logEvent.Properties["RequestPath"]).Value);
        Assert.Equal(expected, ((ScalarValue)logEvent.Properties["Path"]).Value);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, logEvent.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains(expected.Replace("?x=1", string.Empty, StringComparison.Ordinal), json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/public/product-media/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b/content")]
    [InlineData("/api/v1/orders/4736002251")]
    [InlineData("/api/v1/connections/" + Connection + "/instant-notifications")]
    [InlineData("/api/v1/hooks/" + Connection)]
    [InlineData("/health/ready")]
    public void OtherPaths_AreKeptAsTheyAre(string path)
    {
        var (logEvent, _) = LogThroughTheHostingScope(path);

        Assert.Equal(path, ((ScalarValue)logEvent.Properties["RequestPath"]).Value);
        Assert.Equal(path, SecretPathEnricher.MaskPath(path));
    }

    [Fact]
    public void AScopeListIsMaskedToo()
    {
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, MessageTemplate.Empty,
        [
            new LogEventProperty("Scope", new SequenceValue([new ScalarValue($"POST /api/v1/hooks/{Connection}/{Secret}"), new ScalarValue("kept")])),
            new LogEventProperty("Count", new ScalarValue(3))
        ]);

        new SecretPathEnricher().Enrich(logEvent, null!);

        var scope = (SequenceValue)logEvent.Properties["Scope"];
        Assert.Equal($"POST /api/v1/hooks/{Connection}/***", ((ScalarValue)scope.Elements[0]).Value);
        Assert.Equal("kept", ((ScalarValue)scope.Elements[1]).Value);
        Assert.Equal(3, ((ScalarValue)logEvent.Properties["Count"]).Value);
    }

    // As the API logs: ASP.NET Core's request scope (RequestId, RequestPath) through Serilog's logger provider.
    private static (LogEvent Event, string Json) LogThroughTheHostingScope(string path)
    {
        var sink = new CapturingSink();
        using (var serilog = new LoggerConfiguration().Enrich.FromLogContext().Enrich.With<SecretPathEnricher>().WriteTo.Sink(sink).CreateLogger())
        using (var factory = new SerilogLoggerFactory(serilog))
        {
            var logger = factory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware");
            using (logger.BeginScope(new List<KeyValuePair<string, object?>> { new("RequestId", "0HN7:00000001"), new("RequestPath", path) }))
                logger.LogInformation("Executing endpoint for {Path}", path);
        }
        var logEvent = Assert.Single(sink.Events);
        using var writer = new StringWriter();
        new RenderedCompactJsonFormatter().Format(logEvent, writer);
        return (logEvent, writer.ToString());
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
