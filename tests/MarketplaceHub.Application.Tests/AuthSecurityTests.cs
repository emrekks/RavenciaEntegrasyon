using System.Net;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class AuthSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MfaFailures_EndTheSessionAtTheFifth()
    {
        var session = NewSession(SessionState.MfaChallenge);

        for (var attempt = 1; attempt < MfaAttemptPolicy.MaximumFailures; attempt++)
        {
            Assert.False(MfaAttemptPolicy.RegisterFailure(session, Now));
            Assert.Equal(SessionState.MfaChallenge, session.State);
        }

        Assert.True(MfaAttemptPolicy.RegisterFailure(session, Now));
        Assert.Equal(SessionState.Revoked, session.State);
        Assert.Equal(Now, session.RevokedAt);
    }

    [Fact]
    public void MfaSuccess_ResetsTheSessionsCount()
    {
        var session = NewSession(SessionState.MfaChallenge);
        MfaAttemptPolicy.RegisterFailure(session, Now);
        MfaAttemptPolicy.RegisterFailure(session, Now);

        MfaAttemptPolicy.RegisterSuccess(session);

        Assert.Equal(0, session.MfaFailedAttempts);
        Assert.False(MfaAttemptPolicy.RegisterFailure(session, Now));
    }

    [Fact]
    public void AuditRecord_CarriesUserSessionSourceAddressAndCorrelation()
    {
        using var db = NewDb();
        var logs = new List<(LogLevel Level, string Message)>();
        var http = NewContext(logs);
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        AuthAudit.Record(db, http, AuthAudit.MfaFailed, userId, tenantId, "totp; attempt=2", Now, sessionId);

        var entry = Assert.Single(db.ChangeTracker.Entries<AuditLog>()).Entity;
        Assert.Equal(AuthAudit.MfaFailed, entry.Action);
        Assert.Equal(userId, entry.ActorUserId);
        Assert.Equal(tenantId, entry.TenantId);
        Assert.Equal("UserSession", entry.TargetType);
        Assert.Equal(sessionId.ToString("D"), entry.TargetId);
        Assert.Equal("totp; attempt=2; ip=203.0.113.9", entry.Reason);
        Assert.Equal("corr-77", entry.CorrelationId);
        var (level, message) = Assert.Single(logs);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("203.0.113.9", message, StringComparison.Ordinal);
        Assert.Contains("corr-77", message, StringComparison.Ordinal);
        Assert.Contains(userId.ToString(), message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditRecord_OfAFailedSignInForAnUnknownAccountNamesNoUser()
    {
        using var db = NewDb();
        var logs = new List<(LogLevel Level, string Message)>();

        AuthAudit.Record(db, NewContext(logs), AuthAudit.LoginFailed, null, null, "unknown-account", Now);

        var entry = Assert.Single(db.ChangeTracker.Entries<AuditLog>()).Entity;
        Assert.Null(entry.ActorUserId);
        Assert.Null(entry.TargetId);
        Assert.Equal("User", entry.TargetType);
        Assert.StartsWith("unknown-account;", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditRecord_KeepsTheReasonWithinItsColumn()
    {
        using var db = NewDb();

        AuthAudit.Record(db, NewContext([]), AuthAudit.PasswordChangeFailed, Guid.CreateVersion7(), null, new string('x', 2_000), Now);

        Assert.Equal(512, Assert.Single(db.ChangeTracker.Entries<AuditLog>()).Entity.Reason.Length);
    }

    [Fact]
    public void SuccessfulSignIn_IsLoggedAsInformation()
    {
        using var db = NewDb();
        var logs = new List<(LogLevel Level, string Message)>();

        AuthAudit.Record(db, NewContext(logs), AuthAudit.LoginSucceeded, Guid.CreateVersion7(), Guid.CreateVersion7(), "state=ACTIVE", Now, Guid.CreateVersion7());

        Assert.Equal(LogLevel.Information, Assert.Single(logs).Level);
    }

    private static UserSession NewSession(SessionState state) => new() { Id = Guid.CreateVersion7(), UserId = Guid.CreateVersion7(), State = state, TokenHash = "hash" };

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static DefaultHttpContext NewContext(List<(LogLevel Level, string Message)> logs)
    {
        var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(new CapturingProvider(logs))).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "corr-77" };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        return context;
    }

    private sealed class CapturingProvider(List<(LogLevel Level, string Message)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(logs, categoryName);
        public void Dispose() { }
    }

    private sealed class Capturing(List<(LogLevel Level, string Message)> logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category == "MarketplaceHub.Auth") logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
