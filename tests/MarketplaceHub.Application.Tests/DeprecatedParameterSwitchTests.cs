using System.Text.Json;
using MarketplaceHub.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8: one switch for the old query parameters (Api:AcceptDeprecatedParameters, default true). Accepted, a use
// is logged once per route and parameter an hour, with no query value; refused, the old name gets 400 naming the new one.
public sealed class DeprecatedParameterSwitchTests
{
    [Fact]
    public void TheSwitchCoversExactlyTheParametersTheDocumentMarksDeprecated()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.TestProject(), "ApiContract", "openapi.v1.json")));
        var deprecated = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                if (operation.Value.TryGetProperty("parameters", out var parameters))
                    foreach (var parameter in parameters.EnumerateArray())
                        if (parameter.TryGetProperty("deprecated", out var flag) && flag.GetBoolean())
                            deprecated.Add($"{operation.Name.ToUpperInvariant()} {path.Name} {parameter.GetProperty("name").GetString()}");

        Assert.Equal(deprecated.Order(StringComparer.Ordinal), DeprecatedParameters.All.Select(entry => $"GET {entry.Route} {entry.Parameter}").Order(StringComparer.Ordinal));
        Assert.All(DeprecatedParameters.All, entry => Assert.False(string.IsNullOrEmpty(entry.Replacement)));
    }

    [Fact]
    public async Task ByDefaultAnOldNameIsAcceptedAndLoggedOnceAnHourWithoutItsValue()
    {
        var clock = new Clock();
        var logger = new RecordingLogger();
        var passed = 0;
        var middleware = Middleware(null, clock, logger, () => passed++);

        var first = await RunAsync(middleware, "/api/v1/jobs", "?since=2026-11-16T00:00:00Z&platform=SECRET-VALUE");
        await RunAsync(middleware, "/api/v1/jobs", "?since=2026-11-17T00:00:00Z");
        clock.Now += TimeSpan.FromMinutes(61);
        await RunAsync(middleware, "/api/v1/jobs", "?since=2026-11-18T00:00:00Z");

        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal(3, passed);
        // since twice (the first hour and the next), platform once; never a value.
        Assert.Equal(3, logger.Lines.Count);
        Assert.Equal(2, logger.Lines.Count(line => line.Contains("since", StringComparison.Ordinal) && line.Contains("dateFrom", StringComparison.Ordinal)));
        Assert.Single(logger.Lines, line => line.Contains("platform", StringComparison.Ordinal) && line.Contains("platforms", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("SECRET-VALUE", StringComparison.Ordinal) || line.Contains("2026-11", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/v1/reports/sales", "?from=2026-11-01", "from", "dateFrom")]
    [InlineData("/api/v1/reports/payouts/lines", "?page=2", "page", "after")]
    [InlineData("/api/v1/invoice-workspace/page", "?pageSize=50", "pageSize", "limit")]
    [InlineData("/API/v1/Orders/", "?Platform=N11", "platform", "platforms")]
    public async Task SwitchedOffAnOldNameIsRefusedNamingItsReplacement(string path, string query, string parameter, string replacement)
    {
        var passed = 0;
        var middleware = Middleware("false", new Clock(), new RecordingLogger(), () => passed++);

        var context = await RunAsync(middleware, path, query);

        Assert.Equal(0, passed);
        Assert.Equal(400, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(DeprecatedParameters.RefusedCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(parameter, body.RootElement.GetProperty("parameter").GetString());
        Assert.Equal(replacement, body.RootElement.GetProperty("replacement").GetString());
        Assert.Contains(replacement, body.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/jobs", "?dateFrom=2026-11-16T00:00:00Z&platforms=N11")]
    [InlineData("/api/v1/jobs", "?since=")]
    [InlineData("/api/v1/connections", "?page=2")]
    public async Task SwitchedOffTheUnifiedNamesAndOtherRoutesAnswerAsBefore(string path, string query)
    {
        var passed = 0;
        var middleware = Middleware("false", new Clock(), new RecordingLogger(), () => passed++);

        await RunAsync(middleware, path, query);

        Assert.Equal(1, passed);
    }

    [Fact]
    public async Task AWriteIsNeverRefusedForAQueryParameter()
    {
        var passed = 0;
        var middleware = Middleware("false", new Clock(), new RecordingLogger(), () => passed++);
        await RunAsync(middleware, "/api/v1/jobs", "?since=2026-11-16", HttpMethods.Post);
        Assert.Equal(1, passed);
    }

    private static DeprecatedParameterMiddleware Middleware(string? setting, TimeProvider clock, ILogger<DeprecatedParameterMiddleware> logger, Action passed)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(setting is null ? [] : [new KeyValuePair<string, string?>(DeprecatedParameters.SettingKey, setting)])
            .Build();
        return new DeprecatedParameterMiddleware(_ => { passed(); return Task.CompletedTask; }, configuration, clock, logger);
    }

    private static async Task<DefaultHttpContext> RunAsync(DeprecatedParameterMiddleware middleware, string path, string query, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<DeprecatedParameterMiddleware>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
