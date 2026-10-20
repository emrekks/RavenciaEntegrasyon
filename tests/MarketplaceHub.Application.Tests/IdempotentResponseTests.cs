using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class IdempotentResponseTests
{
    [Theory]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/vnd.marketplacehub+json", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    public void OnlyJsonBodiesAreKeptForReplay(string? contentType, bool storable)
    {
        Assert.Equal(storable, IdempotentResponse.IsStorableBody(contentType, 10));
    }

    [Fact]
    public void BodiesOverOneMebibyteAreNotKept()
    {
        Assert.False(IdempotentResponse.IsStorableBody("application/json", IdempotentResponse.MaximumStoredBodyBytes + 1));
    }

    [Fact]
    public async Task Replay_RestoresStatusBodyContentTypeETagAndLocation()
    {
        var first = new DefaultHttpContext();
        first.Response.StatusCode = StatusCodes.Status201Created;
        first.Response.ContentType = "application/json; charset=utf-8";
        first.Response.Headers.ETag = "\"v7\"";
        first.Response.Headers.Location = "/api/v1/connections/11111111-1111-1111-1111-111111111111";
        var record = NewRecord();

        IdempotentResponse.Capture(record, first.Response, "{\"id\":\"11111111-1111-1111-1111-111111111111\"}");
        var replay = new DefaultHttpContext();
        replay.Response.Body = new MemoryStream();
        await IdempotentResponse.ReplayAsync(record, replay.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, replay.Response.StatusCode);
        Assert.Equal("\"v7\"", replay.Response.Headers.ETag.ToString());
        Assert.Equal("/api/v1/connections/11111111-1111-1111-1111-111111111111", replay.Response.Headers.Location.ToString());
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal("application/json; charset=utf-8", replay.Response.ContentType);
        replay.Response.Body.Position = 0;
        Assert.Equal("{\"id\":\"11111111-1111-1111-1111-111111111111\"}", await new StreamReader(replay.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Replay_OfAProblemKeepsItsProblemContentType()
    {
        // A 409 problem used to be stored without its body and replayed empty.
        var first = new DefaultHttpContext();
        first.Response.StatusCode = StatusCodes.Status409Conflict;
        first.Response.ContentType = ApiProblems.ContentType;
        var record = NewRecord();

        IdempotentResponse.Capture(record, first.Response, "{\"code\":\"ORDER_LOCKED\"}");
        var replay = new DefaultHttpContext();
        replay.Response.Body = new MemoryStream();
        await IdempotentResponse.ReplayAsync(record, replay.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, replay.Response.StatusCode);
        Assert.Equal(ApiProblems.ContentType, replay.Response.ContentType);
        Assert.True(replay.Response.Body.Length > 0);
    }

    [Fact]
    public void Capture_LeavesOutValuesTooLongForTheirColumns()
    {
        var response = new DefaultHttpContext().Response;
        response.Headers.ETag = "\"" + new string('v', 200) + "\"";
        response.Headers.Location = "/" + new string('x', 3_000);
        var record = NewRecord();

        IdempotentResponse.Capture(record, response, null);

        Assert.Null(record.ResponseETag);
        Assert.Null(record.ResponseLocation);
        Assert.Null(record.ResponseContentType);
    }

    [Fact]
    public void SessionLookup_TranslatesToOneStatementOverSessionUserMembershipAndSecurity()
    {
        // Translation needs no database; a query EF could not translate would throw here instead of at sign-in.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

        var sql = SessionAuthMiddleware.Lookup(db, "hash").ToQueryString();

        Assert.Contains("user_sessions", sql, StringComparison.Ordinal);
        Assert.Contains("JOIN", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_memberships", sql, StringComparison.Ordinal);
        Assert.Contains("tenants", sql, StringComparison.Ordinal);
        Assert.Contains("user_security", sql, StringComparison.Ordinal);
    }

    private static ApiIdempotencyRecord NewRecord() => new() { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), RouteTemplate = "/api/v1/x", IdempotencyKey = "k", RequestHash = "h", State = "IN_PROGRESS" };
}
