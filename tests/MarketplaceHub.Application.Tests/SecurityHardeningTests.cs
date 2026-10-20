using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Realtime;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ClientNetworkAndRateLimitTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    public void Ipv6SendersAreCountedPerSlash64(string address, string expected)
    {
        Assert.Equal(expected, ClientNetwork.PartitionKey(IPAddress.Parse(address)));
        Assert.Equal("unknown", ClientNetwork.PartitionKey((IPAddress?)null));
    }

    [Fact]
    public void PerNetworkLimit_CountsAWhole64AsOneSender_AndLeavesWebhooksAndStaticFilesAlone()
    {
        using var limiter = RateLimitPolicies.PerNetwork(3);
        for (var index = 0; index < 3; index++)
            Assert.True(limiter.AttemptAcquire(Request("/api/v1/orders", $"2001:db8:aa:bb::{index + 1}")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Request("/api/v1/orders", "2001:db8:aa:bb::99")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("/hubs/operations", "2001:db8:aa:bb::5")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/v1/orders", "2001:db8:aa:cc::1")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/v1/hooks/11111111-1111-1111-1111-111111111111/token", "2001:db8:aa:bb::5")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/assets/app.js", "2001:db8:aa:bb::5")).IsAcquired);
    }

    [Fact]
    public void StepBucket_IsPerSessionCookie_AndPerNetworkWithoutOne()
    {
        var first = Request("/api/v1/auth/reauthenticate", "203.0.113.7", cookie: "token-a");
        var second = Request("/api/v1/auth/reauthenticate", "203.0.113.7", cookie: "token-b");
        var none = Request("/api/v1/auth/reauthenticate", "203.0.113.7");

        Assert.StartsWith("step:session:", RateLimitPolicies.StepPartition(first), StringComparison.Ordinal);
        Assert.NotEqual(RateLimitPolicies.StepPartition(first), RateLimitPolicies.StepPartition(second));
        Assert.DoesNotContain("token-a", RateLimitPolicies.StepPartition(first), StringComparison.Ordinal);
        Assert.Equal("step:network:203.0.113.7", RateLimitPolicies.StepPartition(none));
    }

    [Fact]
    public void WebhookDeliveries_AreReadAtMostThirtyTwoAtOnce()
    {
        using var limiter = WebhookRateLimits.Concurrency();
        var leases = Enumerable.Range(0, WebhookRateLimits.ConcurrentDeliveries).Select(_ => limiter.AttemptAcquire(Request("/api/v1/hooks/11111111-1111-1111-1111-111111111111/t", "198.51.100.1"))).ToList();

        Assert.All(leases, lease => Assert.True(lease.IsAcquired));
        Assert.False(limiter.AttemptAcquire(Request("/api/v1/hooks/22222222-2222-2222-2222-222222222222/t", "198.51.100.2")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("/api/v1/orders", "198.51.100.2")).IsAcquired);
        leases[0].Dispose();
        using var next = limiter.AttemptAcquire(Request("/api/v1/hooks/22222222-2222-2222-2222-222222222222/t", "198.51.100.2"));
        Assert.True(next.IsAcquired);
        leases.ForEach(lease => lease.Dispose());
    }

    [Fact]
    public void WebhookAddressLimit_Is600PerMinute()
    {
        Assert.Equal(600, WebhookRateLimits.PerAddressPerMinute);
    }

    private static DefaultHttpContext Request(string path, string address, string? cookie = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider() };
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (cookie is not null) context.Request.Headers.Cookie = $"{SessionAuthMiddleware.ProductionCookieName}={cookie}";
        return context;
    }
}

public sealed class SignInThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiveFailuresBlockOnlyTheSendingNetwork()
    {
        var clock = new Clock(Start);
        var throttle = new SignInThrottle(clock);
        var user = Guid.CreateVersion7();

        for (var attempt = 1; attempt < SignInThrottle.FailuresPerBlock; attempt++) Assert.False(throttle.RegisterFailure(user, "203.0.113.7"));
        Assert.True(throttle.RegisterFailure(user, "203.0.113.7"));

        Assert.True(throttle.IsBlocked(user, "203.0.113.7"));
        Assert.False(throttle.IsBlocked(user, "198.51.100.9"));
        Assert.False(throttle.IsBlocked(Guid.CreateVersion7(), "203.0.113.7"));
        clock.Now = Start + SignInThrottle.FirstBlock + TimeSpan.FromSeconds(1);
        Assert.False(throttle.IsBlocked(user, "203.0.113.7"));
    }

    [Fact]
    public void EachFurtherBlockLastsTwiceAsLong_UpToADay()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SignInThrottle.BlockLength(1));
        Assert.Equal(TimeSpan.FromMinutes(30), SignInThrottle.BlockLength(2));
        Assert.Equal(TimeSpan.FromMinutes(60), SignInThrottle.BlockLength(3));
        Assert.Equal(TimeSpan.FromHours(24), SignInThrottle.BlockLength(12));

        var clock = new Clock(Start);
        var throttle = new SignInThrottle(clock);
        var user = Guid.CreateVersion7();
        for (var attempt = 0; attempt < SignInThrottle.FailuresPerBlock; attempt++) throttle.RegisterFailure(user, "net");
        clock.Now += SignInThrottle.FirstBlock + TimeSpan.FromSeconds(1);
        for (var attempt = 0; attempt < SignInThrottle.FailuresPerBlock; attempt++) throttle.RegisterFailure(user, "net");
        clock.Now += SignInThrottle.FirstBlock + TimeSpan.FromSeconds(1);
        Assert.True(throttle.IsBlocked(user, "net"));
    }

    [Fact]
    public void ASuccessClearsTheNetworksCount()
    {
        var throttle = new SignInThrottle(new Clock(Start));
        var user = Guid.CreateVersion7();
        for (var attempt = 1; attempt < SignInThrottle.FailuresPerBlock; attempt++) throttle.RegisterFailure(user, "net");

        throttle.RegisterSuccess(user, "net");

        Assert.False(throttle.RegisterFailure(user, "net"));
        Assert.False(throttle.IsBlocked(user, "net"));
    }

    [Fact]
    public void AnAccountTakesALimitedNumberOfSignInAttemptsPerMinute()
    {
        var clock = new Clock(Start);
        var throttle = new SignInThrottle(clock);
        var user = Guid.CreateVersion7();
        for (var attempt = 0; attempt < SignInThrottle.AccountAttemptsPerMinute; attempt++) Assert.True(throttle.TryTakeAccountAttempt(user));

        Assert.False(throttle.TryTakeAccountAttempt(user));
        Assert.True(throttle.TryTakeAccountAttempt(Guid.CreateVersion7()));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(throttle.TryTakeAccountAttempt(user));
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Password123456!")]
    [InlineData("MarketplaceHub1!")]
    [InlineData("P@ssw0rd123456789!")]
    [InlineData("Galatasaray1905!!")]
    [InlineData("Fenerbahçe19071907")]
    [InlineData("Ravencia2026Ravencia")]
    [InlineData("TrendyolHepsiburada1!")]
    [InlineData("aaaaaaaaaaaaaaaaaa")]
    [InlineData("123456789012345")]
    [InlineData("Qwertyuiopasdfgh1!")]
    [InlineData("abcdefghijklmnop")]
    [InlineData("Short1!")]
    public void CommonOrDressedUpPasswordsAreRefused(string password)
    {
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Theory]
    [InlineData("Kx9!mQ2#vT7$pL4@")]
    [InlineData("Mavi-Bulut 47 Kalem Defter!")]
    [InlineData("tren-raf-limon-9341-Kase")]
    public void LongUnusualPasswordsAreAccepted(string password)
    {
        Assert.True(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void ThePersonsOwnNameOrEmailIsRefused()
    {
        Assert.False(PasswordPolicy.IsAcceptable("Kx9!Zeynepoglu#vT7", "zeynepoglu@example.com", "Ayşe Zeynepoğlu"));
        Assert.False(PasswordPolicy.IsAcceptable("Kx9!mQ2#Ayse#vT7$", "owner@example.com", "Ayşe Zeynepoğlu"));
        Assert.True(PasswordPolicy.IsAcceptable("Kx9!mQ2#vT7$pL4@", "owner@example.com", "Ayşe Zeynepoğlu"));
    }
}

public sealed class RecoveryCodeTests
{
    [Fact]
    public void NewCodesAreTwelveCrockfordSymbolsInThreeGroups()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => RecoveryCodes.NewCode()).ToList();

        Assert.All(codes, code => Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), code));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void TypedLookAlikesReadAsDigits_AndEarlierCodesStillMatch()
    {
        var hasher = new TokenHasher(Enumerable.Repeat((byte)3, 32).ToArray());

        Assert.Equal("0123ABCD1111", RecoveryCodes.Canonical("o123-abcd-ilLI"));
        Assert.Contains(hasher.Hash("0123ABCD1111"), RecoveryCodes.Digests(hasher, "o123 abcd ilLI"));
        // A code of the earlier format (two base64url groups) is stored without dashes, upper case.
        Assert.Contains(hasher.Hash("AB_OLQ9X_Z"), RecoveryCodes.Digests(hasher, "ab_ol-q9x_z"));
    }
}

public sealed class BoundSecretTests
{
    private readonly IDataProtector purpose = new EphemeralDataProtectionProvider().CreateProtector("MarketplaceHub.PlatformCredential.v1");

    [Fact]
    public void ABoundSecretOnlyOpensForItsOwnTenantAndConnection()
    {
        var tenant = Guid.CreateVersion7(); var connection = Guid.CreateVersion7();
        var stored = BoundSecret.ProtectForConnection(purpose, tenant, connection, "{\"apiKey\":\"k\"}");

        Assert.StartsWith(BoundSecret.Prefix, stored, StringComparison.Ordinal);
        Assert.Equal("{\"apiKey\":\"k\"}", BoundSecret.UnprotectForConnection(purpose, tenant, connection, stored));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => BoundSecret.UnprotectForConnection(purpose, tenant, Guid.CreateVersion7(), stored));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => BoundSecret.UnprotectForConnection(purpose, Guid.CreateVersion7(), connection, stored));
    }

    [Fact]
    public void SecretsStoredBeforeBindingStillOpen()
    {
        var legacy = purpose.Protect("old");

        Assert.False(BoundSecret.IsBound(legacy));
        Assert.Equal("old", BoundSecret.UnprotectForConnection(purpose, Guid.CreateVersion7(), Guid.CreateVersion7(), legacy));
    }
}

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("4f8560a62b1748fd", true)]
    [InlineData("operation-4f8560a6-2b17-48fd-a3b2-66ae6fac6988", true)]
    [InlineData("a b", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("<script>", false)]
    [InlineData("", false)]
    public void OnlyShortPlainTokensAreKept(string value, bool kept)
    {
        Assert.Equal(kept, CorrelationId.IsAcceptable(value));
        Assert.Equal(kept, CorrelationId.FromRequest(value) == value);
    }

    [Fact]
    public void ALongValueIsReplaced()
    {
        Assert.NotEqual(new string('a', 65), CorrelationId.FromRequest(new string('a', 65)));
    }
}

public sealed class StepUpMiddlewareTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("POST", "/api/v1/settings/data-reset", true)]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/deep-delete/", true)]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111//data-reset", true)]
    [InlineData("PUT", "/api/v1/connections/11111111-1111-1111-1111-111111111111/credential", true)]
    [InlineData("GET", "/api/v1/connections/11111111-1111-1111-1111-111111111111/credential", false)]
    [InlineData("PUT", "/api/v1/settings/shipping-label", false)]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/test-jobs", false)]
    public void RoutesThatNeedAFreshProof(string method, string path, bool expected)
    {
        Assert.Equal(expected, StepUpMiddleware.RequiresStepUp(method, path));
    }

    [Fact]
    public async Task AnOldSignInIsAskedToReauthenticate_ARecentOneGoesThrough()
    {
        var (stale, staleCalled) = await Invoke(Now.AddMinutes(-6));
        var (fresh, freshCalled) = await Invoke(Now.AddMinutes(-4));

        Assert.False(staleCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, stale.Response.StatusCode);
        Assert.True(freshCalled);
    }

    private static async Task<(HttpContext Context, bool Called)> Invoke(DateTimeOffset reauthenticatedAt)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/settings/data-reset";
        context.Response.Body = new MemoryStream();
        context.Items["UserSession"] = new UserSession { Id = Guid.CreateVersion7(), UserId = Guid.CreateVersion7(), State = SessionState.Active, TokenHash = "hash", ReauthenticatedAt = reauthenticatedAt };
        var called = false;
        await new StepUpMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new SignInThrottleTests.Clock(Now));
        if (!called)
        {
            context.Response.Body.Position = 0;
            var problem = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
            Assert.Equal(StepUpMiddleware.Code, problem.GetProperty("code").GetString());
        }
        return (context, called);
    }
}

public sealed class IdempotencyKeyTests
{
    [Fact]
    public void KeysAreStoredPerUser()
    {
        var first = Guid.CreateVersion7(); var second = Guid.CreateVersion7();

        Assert.NotEqual(IdempotencyRequestDigest.StoredKey(first, "k"), IdempotencyRequestDigest.StoredKey(second, "k"));
        Assert.True(IdempotencyRequestDigest.StoredKey(first, new string('k', 256)).Length <= 256);
    }

    [Fact]
    public void TheRequestDigestNeedsTheServerKey()
    {
        var request = "POST\n/api/v1/invoices/1/cancellation-jobs\n\n{\"password\":\"guess\"}"u8.ToArray();
        var one = new TokenHasher(Enumerable.Repeat((byte)1, 32).ToArray()).HashBytes(IdempotencyRequestDigest.Prefix(request));
        var two = new TokenHasher(Enumerable.Repeat((byte)2, 32).ToArray()).HashBytes(IdempotencyRequestDigest.Prefix(request));

        Assert.NotEqual(one, two);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(request)), one);
    }

    [Theory]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks", true)]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/instant-notifications", true)]
    [InlineData("GET", "/api/v1/connections/11111111-1111-1111-1111-111111111111/webhooks", false)]
    [InlineData("POST", "/api/v1/connections/11111111-1111-1111-1111-111111111111/test-jobs", false)]
    public void SecretAnswersAreNotKeptForReplay(string method, string path, bool secret)
    {
        Assert.Equal(secret, SecretResponseRoutes.Matches(method, path));
    }
}

public sealed class HubSessionTests
{
    [Fact]
    public void ConnectionsOfEndedSessionsAreAborted()
    {
        var monitor = new HubSessionMonitor(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<HubSessionMonitor>.Instance);
        var live = new FakeCaller("live"); var ended = new FakeCaller("ended");
        var liveSession = Guid.CreateVersion7();
        monitor.Track(live, liveSession);
        monitor.Track(ended, Guid.CreateVersion7());

        Assert.Equal(1, monitor.AbortAllExcept(new HashSet<Guid> { liveSession }));

        Assert.False(live.Aborted);
        Assert.True(ended.Aborted);
        Assert.Equal(1, monitor.Count);
        monitor.Forget("live");
        Assert.Equal(0, monitor.Count);
    }

    [Fact]
    public async Task AConnectionFromAnotherSiteIsAborted_APanelOneIsTracked()
    {
        var monitor = new HubSessionMonitor(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<HubSessionMonitor>.Instance);
        var filter = new HubSessionFilter(monitor);
        var foreign = new FakeCaller("foreign", "https://evil.example", Guid.CreateVersion7());
        var own = new FakeCaller("own", "https://panel.example", Guid.CreateVersion7());
        var nextCalls = 0;

        await filter.OnConnectedAsync(new HubLifetimeContext(foreign, new ServiceCollection().BuildServiceProvider(), new OperationsHub()), _ => { nextCalls++; return Task.CompletedTask; });
        await filter.OnConnectedAsync(new HubLifetimeContext(own, new ServiceCollection().BuildServiceProvider(), new OperationsHub()), _ => { nextCalls++; return Task.CompletedTask; });

        Assert.True(foreign.Aborted);
        Assert.False(own.Aborted);
        Assert.Equal(1, nextCalls);
        Assert.Equal(1, monitor.Count);
    }

    private sealed class FakeCaller(string id, string? origin = null, Guid? sessionId = null) : HubCallerContext
    {
        private readonly DefaultHttpContext http = CreateHttp(origin);
        public bool Aborted { get; private set; }
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => sessionId is { } session ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("session_id", session.ToString())], "test")) : null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => http.Features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;

        private static DefaultHttpContext CreateHttp(string? origin)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider() };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("panel.example");
            if (origin is not null) context.Request.Headers.Origin = origin;
            context.Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeature { HttpContext = context });
            return context;
        }
    }

    private sealed class HttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}

public sealed class SettingsSaveConflictTests
{
    [Fact]
    public void AConcurrentFirstSaveIsRecognisedAsAConflict()
    {
        var unique = new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new Npgsql.PostgresException("duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation));
        var other = new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new Npgsql.PostgresException("not null", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.NotNullViolation));

        Assert.True(SettingsEndpoints.IsUniqueViolation(unique));
        Assert.False(SettingsEndpoints.IsUniqueViolation(other));
    }
}

public sealed class ShippingLabelSettingsSchemaTests
{
    [Fact]
    public void UnknownFieldsAreDropped_AndValuesKeptInTheirRanges()
    {
        using var document = JsonDocument.Parse("""
            {"senderName":"Ravencia","stickerWidthMm":9000,"sectionGapMm":-3,"a4LabelsPerPage":3,"defaultFormat":"poster","showA4Button":false,
             "injected":"<script>","layout":{"a4":[{"kind":"address","fields":["address","address","password"],"align":"middle","position":{"x":500,"y":-1,"width":1,"height":50},"extra":1},"sender",7],"other":[]}}
            """);

        var normalized = ShippingLabelSettingsSchema.Normalize(document.RootElement);

        Assert.Null(normalized["injected"]);
        Assert.Equal(300, normalized["stickerWidthMm"]!.GetValue<double>());
        Assert.Equal(0, normalized["sectionGapMm"]!.GetValue<double>());
        Assert.Equal(1, normalized["a4LabelsPerPage"]!.GetValue<int>());
        Assert.Equal("a4", normalized["defaultFormat"]!.GetValue<string>());
        Assert.False(normalized["showA4Button"]!.GetValue<bool>());
        var blocks = normalized["layout"]!["a4"]!.AsArray();
        Assert.Equal(2, blocks.Count);
        Assert.Equal("left", blocks[0]!["align"]!.GetValue<string>());
        Assert.Equal(["address"], blocks[0]!["fields"]!.AsArray().Select(field => field!.GetValue<string>()));
        Assert.Equal(92, blocks[0]!["position"]!["x"]!.GetValue<double>());
        Assert.Null(blocks[0]!["extra"]);
        Assert.Equal("sender", blocks[1]!.GetValue<string>());
        Assert.Null(normalized["layout"]!["other"]);
    }
}

public sealed class FileAndNetworkBoundaryTests
{
    [Fact]
    public async Task AStoredPathThatClimbsIntoAnotherTenantIsRefused_EvenWithTheRightPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-file-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new PrivateFileStorage(root);
            var mine = Guid.CreateVersion7(); var other = Guid.CreateVersion7();
            var stored = await storage.SaveAsync(other, "a.pdf", "application/pdf", new MemoryStream([1, 2, 3]), 1024, CancellationToken.None);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.OpenReadAsync(mine, $"{mine:N}/../{stored}", CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.DeleteAsync(mine, $"{mine:N}/../{stored}", CancellationToken.None));
            await using (var opened = await storage.OpenReadAsync(other, stored, CancellationToken.None)) Assert.Equal(3, opened.Length);
            Assert.True(await storage.DeleteAsync(other, stored, CancellationToken.None));
            Assert.False(await storage.DeleteAsync(other, stored, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("64:ff9b::a00:1")]          // NAT64 of 10.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]      // NAT64 of 169.254.169.254
    [InlineData("64:ff9b:1::7f00:1")]       // local-use NAT64 of 127.0.0.1
    [InlineData("2002:c0a8:0101::1")]       // 6to4 of 192.168.1.1
    [InlineData("::a00:1")]                 // IPv4-compatible 10.0.0.1
    [InlineData("::ffff:0:a00:1")]          // IPv4-translated 10.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("100::1")]                  // discard-only
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    public void EmbeddedAndNonGlobalIpv6AddressesAreRefused(string address)
    {
        Assert.False(PublicNetwork.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("64:ff9b::808:808")]        // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]         // 6to4 of 8.8.8.8
    [InlineData("2a00:1450:4001:80b::200e")]
    public void EmbeddedPublicAndGlobalAddressesAreAllowed(string address)
    {
        Assert.True(PublicNetwork.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void TheMediaClientIgnoresEnvironmentProxies()
    {
        using var handler = PublicNetwork.Handler();
        Assert.False(handler.UseProxy);
    }
}

public sealed class BreakGlassPasswordTests
{
    [Fact]
    public void TemporaryPasswordsMeetThePasswordRules()
    {
        var passwords = Enumerable.Range(0, 30).Select(_ => BreakGlassService.TemporaryPassword()).ToList();

        Assert.All(passwords, password =>
        {
            Assert.Equal(24, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, character => !char.IsLetterOrDigit(character));
        });
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Fact]
    public void TheManualSetupKeyIsGroupedBase32()
    {
        var key = AuthEndpoints.GroupedKey(AuthEndpoints.Base32(Enumerable.Range(1, 20).Select(value => (byte)value).ToArray()));

        Assert.Matches(new Regex("^([A-Z2-7]{4} )+[A-Z2-7]{1,4}$"), key);
        Assert.Equal(32, key.Replace(" ", "", StringComparison.Ordinal).Length);
    }
}
