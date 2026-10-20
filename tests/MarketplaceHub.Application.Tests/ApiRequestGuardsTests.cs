using System.Net;
using System.Security.Claims;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ApiRequestGuardsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("same-origin", false)]
    [InlineData("none", false)]
    [InlineData("same-site", true)]
    [InlineData("cross-site", true)]
    public void QuotaSpendingReads_AreRefusedWhenAnotherSiteStartedThem(string? fetchSite, bool refused)
    {
        var context = new DefaultHttpContext();
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        Assert.Equal(refused, FetchMetadata.IsStartedByAnotherSite(context.Request));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.30.10.5", true)]
    [InlineData("10.0.0.8", true)]
    [InlineData("203.0.113.40", false)]
    [InlineData("8.8.8.8", false)]
    public void QueueHealth_IsServedOnlyToTheServersOwnNetwork(string address, bool served)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        Assert.Equal(served, FetchMetadata.IsFromServerNetwork(context));
    }

    [Fact]
    public void SettingsSave_WithoutIfMatch_StillWrites()
    {
        Assert.Null(SettingsEndpoints.Precondition(new DefaultHttpContext(), Setting(3)));
    }

    [Fact]
    public void SettingsSave_WithTheCurrentVersion_Writes()
    {
        Assert.Null(SettingsEndpoints.Precondition(WithIfMatch("\"v3\""), Setting(3)));
    }

    [Theory]
    [InlineData("\"v2\"", 3L, 412)]
    [InlineData("\"v1\"", null, 412)]
    [InlineData("v3", 3L, 400)]
    [InlineData("\"v0\"", 3L, 400)]
    public void SettingsSave_WithAStaleOrMalformedVersion_IsRefused(string ifMatch, long? currentVersion, int status)
    {
        var result = SettingsEndpoints.Precondition(WithIfMatch(ifMatch), currentVersion is { } version ? Setting(version) : null);

        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(true, "OWNER", "disabled", "POST", "/api/v1/connections", true)]
    [InlineData(true, "ADMINISTRATOR", "disabled", "PUT", "/api/v1/settings/appearance", true)]
    [InlineData(true, "OWNER", "enabled", "POST", "/api/v1/connections", false)]
    [InlineData(true, "OWNER", "disabled", "GET", "/api/v1/orders", false)]
    [InlineData(true, "OWNER", "disabled", "POST", "/api/v1/auth/mfa/setup", false)]
    [InlineData(true, "OPERATIONS", "disabled", "POST", "/api/v1/orders/x/instant-process", false)]
    [InlineData(false, "OWNER", "disabled", "POST", "/api/v1/connections", false)]
    public void ElevatedRolesWithoutMfa_MustEnrollBeforeChangingAnything(bool enforced, string role, string mfa, string method, string path, bool refused)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim(SessionAuthMiddleware.MfaClaim, mfa)], "test"))
        };
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(refused, ElevatedRoleMfaMiddleware.MustEnrollFirst(context, enforced));
    }

    private static DefaultHttpContext WithIfMatch(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = value;
        return context;
    }

    private static TenantSetting Setting(long version) => new() { TenantId = Guid.CreateVersion7(), Key = "appearance", ValueJson = "{}", Version = version };
}
