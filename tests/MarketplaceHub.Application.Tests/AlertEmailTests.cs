using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Alerts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class AlertEmailRulesTests
{
    private const string Password = "abcdefghijklmnop";

    [Theory]
    [InlineData("ravencia.alarm@gmail.com", true)]
    [InlineData("Ravencia.Alarm@GMAIL.com", true)]
    [InlineData("destek@ravencia.com", false)]
    [InlineData("not-an-address", false)]
    public void SenderMustBeAGmailAddress(string sender, bool valid)
    {
        var normalized = AlertEmailRules.NormalizeAddress(sender);
        Assert.Equal(valid, AlertEmailRules.Validate(normalized, ["owner@example.com"]) is null);
    }

    [Fact]
    public void Recipients_AreSplitTrimmedLowercasedAndDeduplicated()
    {
        var recipients = AlertEmailRules.NormalizeRecipients(["A@Example.com, b@example.com", " a@example.com ", null, "c@example.com;d@example.com"]);
        Assert.Equal(["a@example.com", "b@example.com", "c@example.com", "d@example.com"], recipients);
        Assert.NotNull(AlertEmailRules.Validate("x@gmail.com", []));
        Assert.NotNull(AlertEmailRules.Validate("x@gmail.com", ["1@a.com", "2@a.com", "3@a.com", "4@a.com", "5@a.com", "6@a.com"]));
        Assert.NotNull(AlertEmailRules.Validate("x@gmail.com", ["broken@"]));
    }

    [Theory]
    [InlineData("abcd efgh ijkl mnop", true)]
    [InlineData("ABCDEFGHIJKLMNOP", true)]
    [InlineData("abcdefghijklmno", false)]
    [InlineData("abcdefghijklmno1", false)]
    public void AppPassword_IsSixteenLettersWithSpacesIgnored(string value, bool valid) =>
        Assert.Equal(valid, AlertEmailRules.IsAppPassword(AlertEmailRules.NormalizeAppPassword(value)));

    [Fact]
    public void HostFile_RoundTripsAndRejectsBrokenFiles()
    {
        var credentials = new AlertEmailCredentials("ravencia.alarm@gmail.com", ["owner@example.com", "second@example.com"], Password);
        var text = AlertEmailRules.ToHostFile(credentials);
        Assert.Equal($"ravencia.alarm@gmail.com\nowner@example.com,second@example.com\n{Password}\n", text);
        var parsed = AlertEmailRules.FromHostFile(text.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.NotNull(parsed);
        Assert.Equal(credentials.SenderAddress, parsed.SenderAddress);
        Assert.Equal(credentials.Recipients, parsed.Recipients);
        Assert.Equal(Password, parsed.AppPassword);
        Assert.Null(AlertEmailRules.FromHostFile("only-one-line"));
        Assert.Null(AlertEmailRules.FromHostFile($"destek@ravencia.com\nowner@example.com\n{Password}\n"));
    }

    [Fact]
    public void Message_HasTheHeadersOfTheHostAlertsAndAnEncodedSubject()
    {
        var message = AlertEmailRules.BuildMessage("ravencia.alarm@gmail.com", ["owner@example.com"], AlertEmailRules.TestMessage, new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero), "panel");
        Assert.Contains("From: Ravencia <ravencia.alarm@gmail.com>\r\n", message, StringComparison.Ordinal);
        Assert.Contains("To: <owner@example.com>\r\n", message, StringComparison.Ordinal);
        var subject = Convert.ToBase64String(Encoding.UTF8.GetBytes("[Ravencia] " + AlertEmailRules.TestMessage));
        Assert.Contains($"Subject: =?UTF-8?B?{subject}?=\r\n", message, StringComparison.Ordinal);
        Assert.Contains("Date: Tue, 03 Nov 2026 20:00:00 +0000\r\n", message, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain; charset=UTF-8\r\n", message, StringComparison.Ordinal);
        var body = message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].Replace("\r\n", string.Empty, StringComparison.Ordinal);
        Assert.Contains(AlertEmailRules.TestMessage, Encoding.UTF8.GetString(Convert.FromBase64String(body)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmtpDialogue_AuthenticatesSendsToEveryRecipientAndStuffsDots()
    {
        var stream = new ScriptedSmtpStream("220 ready", "250-smtp.gmail.com\r\n250 SMTPUTF8", "235 ok", "250 ok", "250 ok", "251 ok", "354 go", "250 queued", "221 bye");
        var credentials = new AlertEmailCredentials("ravencia.alarm@gmail.com", ["a@example.com", "b@example.com"], Password);

        await SmtpDialogue.RunAsync(stream, credentials, "Subject: x\r\n\r\n.leading dot\r\n", CancellationToken.None);

        var sent = stream.Sent;
        Assert.Contains("EHLO ravencia\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0ravencia.alarm@gmail.com\0{Password}")) + "\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("RCPT TO:<a@example.com>\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("RCPT TO:<b@example.com>\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("\r\n..leading dot\r\n.\r\n", sent, StringComparison.Ordinal);
        Assert.EndsWith("QUIT\r\n", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmtpDialogue_RefusedLogin_IsReportedWithoutThePassword()
    {
        var stream = new ScriptedSmtpStream("220 ready", "250 hi", "535-5.7.8 Username and Password not accepted\r\n535 5.7.8 BadCredentials");
        var credentials = new AlertEmailCredentials("ravencia.alarm@gmail.com", ["a@example.com"], Password);

        var exception = await Assert.ThrowsAsync<AlertEmailException>(() => SmtpDialogue.RunAsync(stream, credentials, "x", CancellationToken.None));

        Assert.Equal("ALERT_EMAIL_AUTH_FAILED", exception.Code);
        Assert.DoesNotContain(Password, exception.Message, StringComparison.Ordinal);
        Assert.Contains("535", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef", false)]
    [InlineData("short", "short", false)]
    [InlineData("0123456789abcdef0123456789abcdef", "wrong-token-wrong-token-wrong-tok", false)]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef", true)]
    public void HostSync_NeedsTheConfiguredTokenFromTheServerNetwork(string? configured, string presented, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Alerts:HostSyncToken"] = configured }).Build();
        var local = new DefaultHttpContext();
        local.Connection.RemoteIpAddress = IPAddress.Parse("172.19.0.1");
        local.Request.Headers[AlertEmailEndpoints.HostTokenHeader] = presented;
        Assert.Equal(allowed, AlertEmailEndpoints.HostAllowed(local, configuration));

        var outside = new DefaultHttpContext();
        outside.Connection.RemoteIpAddress = IPAddress.Parse("159.146.51.211");
        outside.Request.Headers[AlertEmailEndpoints.HostTokenHeader] = presented;
        Assert.False(AlertEmailEndpoints.HostAllowed(outside, configuration));
    }

    // Answers each command the client writes with the next scripted reply (the first one is the greeting).
    private sealed class ScriptedSmtpStream : Stream
    {
        private readonly Queue<string> replies;
        private readonly StringBuilder sent = new();
        private byte[] pending = [];
        private int position;

        public ScriptedSmtpStream(params string[] replies)
        {
            this.replies = new Queue<string>(replies);
            Next();
        }

        public string Sent => sent.ToString();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private void Next()
        {
            pending = replies.Count > 0 ? Encoding.UTF8.GetBytes(replies.Dequeue() + "\r\n") : [];
            position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, pending.Length - position);
            Array.Copy(pending, position, buffer, offset, available);
            position += available;
            return available;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var text = Encoding.UTF8.GetString(buffer, offset, count);
            sent.Append(text);
            // A command (or the DATA body ending in "\r\n.\r\n") gets the next reply.
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) Next();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

[Collection("postgres-integration")]
public sealed class AlertEmailEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    private const string Password = "qrstuvwxyzabcdef";

    [PostgreSqlFact]
    public async Task Settings_AreForOwnersAndAdministrators_ThePasswordIsWriteOnlyEncryptedAndAudited()
    {
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        var (operations, _, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (admin)
        using (adminSignIn)
        using (operations)
        using (operationsSignIn)
        {
            using var forbidden = await operations.GetAsync("/api/v1/settings/alert-email");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var missingPassword = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/alert-email", csrf, new { senderAddress = "ravencia.alarm@gmail.com", recipients = new[] { "owner@example.com" } }));
            Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);

            using var saved = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/alert-email", csrf, new { senderAddress = "Ravencia.Alarm@gmail.com", recipients = new[] { "owner@example.com" }, appPassword = "qrst uvwx yzab cdef" }));
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.DoesNotContain(Password, savedText, StringComparison.Ordinal);
            var body = JsonDocument.Parse(savedText).RootElement;
            Assert.True(body.GetProperty("appPasswordSaved").GetBoolean());
            Assert.Equal("ravencia.alarm@gmail.com", body.GetProperty("senderAddress").GetString());

            // A blank password keeps the saved one.
            using var kept = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/alert-email", csrf, new { senderAddress = "ravencia.alarm@gmail.com", recipients = new[] { "owner@example.com", "second@example.com" }, appPassword = "" }));
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

            using var read = await admin.GetAsync("/api/v1/settings/alert-email");
            var readText = await read.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Password, readText, StringComparison.Ordinal);
            Assert.Equal(2, JsonDocument.Parse(readText).RootElement.GetProperty("recipients").GetArrayLength());

            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.TenantSettings.AsNoTracking().SingleAsync(x => x.TenantId == fixture.TenantId && x.Key == AlertEmailSettingsService.SettingKey);
            Assert.DoesNotContain(Password, row.ValueJson, StringComparison.Ordinal);
            var credentials = await scope.ServiceProvider.GetRequiredService<AlertEmailSettingsService>().LoadCredentialsAsync(fixture.TenantId, CancellationToken.None);
            Assert.Equal(Password, credentials?.AppPassword);
            Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.TenantId == fixture.TenantId && x.Action == "ALERT_EMAIL_SETTINGS_CHANGED"));
            Assert.False(await db.AuditLogs.AnyAsync(x => x.Action.StartsWith("ALERT_EMAIL") && x.Reason.Contains(Password)));

            // The host sync does not exist without a configured host token.
            using var host = await admin.GetAsync("/internal/alert-settings");
            Assert.Equal(HttpStatusCode.NotFound, host.StatusCode);
        }
    }
}
