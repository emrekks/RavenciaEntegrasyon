using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Alerts;
using MarketplaceHub.Infrastructure.Backups;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OffsiteBackupRulesTests
{
    // The example recipient of the age documentation.
    internal const string Recipient = "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p";

    [Fact]
    public void AgeRecipient_OnlyAValidPublicKeyIsKept_APrivateKeyIsRefusedAndForgotten()
    {
        Assert.Null(OffsiteBackupRules.AgeRecipientError(Recipient, out var normalized));
        Assert.Equal(Recipient, normalized);
        Assert.Null(OffsiteBackupRules.AgeRecipientError("  " + Recipient.ToUpperInvariant() + " ", out normalized));
        Assert.Equal(Recipient, normalized);
        // One character changed breaks the checksum.
        Assert.NotNull(OffsiteBackupRules.AgeRecipientError(Recipient[..^1] + (Recipient[^1] == 'p' ? 'q' : 'p'), out _));
        Assert.NotNull(OffsiteBackupRules.AgeRecipientError("age1abc", out _));
        Assert.NotNull(OffsiteBackupRules.AgeRecipientError("", out _));
        var secret = OffsiteBackupRules.AgeRecipientError("AGE-SECRET-KEY-1GFPYYSJZGFPYYSJZGFPYYSJZGFPYYSJZGFPYYSJZGFPYYSJZGFPQ4EGAEX", out normalized);
        Assert.Contains("ÖZEL", secret, StringComparison.Ordinal);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void Folder_NameDefaultsToTheAppFolder_IdIsTakenFromADriveLink()
    {
        Assert.Null(OffsiteBackupRules.FolderError(null, "", out var mode, out var value));
        Assert.Equal((OffsiteBackupRules.FolderModeName, OffsiteBackupRules.DefaultFolderName), (mode, value));
        Assert.Null(OffsiteBackupRules.FolderError("id", "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp?usp=sharing", out mode, out value));
        Assert.Equal((OffsiteBackupRules.FolderModeId, "1AbCdEfGhIjKlMnOp"), (mode, value));
        Assert.NotNull(OffsiteBackupRules.FolderError("NAME", "a/b", out _, out _));
        Assert.NotNull(OffsiteBackupRules.FolderError("ID", "x y", out _, out _));
    }

    [Fact]
    public void Retention_KeepsTheLastDaysTheFirstCopyOfEachMonthAndTheNewest_AndNeverForeignFiles()
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);
        string[] names =
        [
            "20250801T085900Z.tar.age", // first of a month 15 months ago: outside the 12 months
            "20260901T085900Z.tar.age", // first of September: kept
            "20260915T085900Z.tar.age", // later in September: removed
            "20261001T205900Z.tar.age", // first of October: kept
            "20261002T085900Z.tar.age", // removed
            "20261031T085900Z.tar.age", // within 14 days: kept
            "20261113T085900Z.tar.age", // newest
            "notes.txt", "20261002T085900Z.tar", "ravencia.tar.age"
        ];
        var deleted = OffsiteBackupRules.SelectForDeletion(names, now, keepDays: 14, keepMonths: 12);
        Assert.Equal(["20250801T085900Z.tar.age", "20260915T085900Z.tar.age", "20261002T085900Z.tar.age"], deleted.Order(StringComparer.Ordinal));
        // Months 0: only the days and the newest stay; the newest stays even when older than the window.
        Assert.Equal(["20250801T085900Z.tar.age"], OffsiteBackupRules.SelectForDeletion(["20250801T085900Z.tar.age", "20250901T085900Z.tar.age"], now, 14, 0));
    }

    [Fact]
    public void Schedule_RunsAfterTheBackupSlots_RetriesWithGrowingWaits_AndStopsWhenGoogleRefuses()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var done = new OffsiteBackupScheduleState(true, true, false, new DateTimeOffset(2026, 11, 13, 9, 31, 0, TimeSpan.Zero), 0, null);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 21, 30, 0, TimeSpan.Zero), OffsiteBackupRules.NextRunAt(done, now));
        Assert.Equal(now, OffsiteBackupRules.NextRunAt(done with { LastAttemptAt = null }, now));
        Assert.Equal(now, OffsiteBackupRules.NextRunAt(done with { RequestedAt = now.AddMinutes(-1) }, now));
        Assert.Equal(done.LastAttemptAt!.Value.AddMinutes(30), OffsiteBackupRules.NextRunAt(done with { ConsecutiveFailures = 2 }, now));
        // A long wait never skips the next slot.
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 21, 30, 0, TimeSpan.Zero), OffsiteBackupRules.NextRunAt(done with { LastAttemptAt = new DateTimeOffset(2026, 11, 13, 19, 0, 0, TimeSpan.Zero), ConsecutiveFailures = 6 }, now));
        Assert.Null(OffsiteBackupRules.NextRunAt(done with { AuthorizationInvalid = true, RequestedAt = now }, now));
        Assert.Null(OffsiteBackupRules.NextRunAt(done with { Enabled = false }, now));
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero), OffsiteBackupRules.LastSlotAtOrBefore(now));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 21, 30, 0, TimeSpan.Zero), OffsiteBackupRules.LastSlotAtOrBefore(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task BackupSets_TheNewestVerifiedSetIsStreamedAsTar_AMismatchedOrIncompleteSetIsSkipped()
    {
        using var root = new TempBackupRoot();
        root.WriteSet("20261112T085900Z");
        root.WriteSet("20261113T085900Z");
        root.WriteSet("20261113T205900Z", corrupt: true);
        Directory.CreateDirectory(Path.Combine(root.Path, "20261114T085900Z")); // being written: no SHA256SUMS yet
        var (set, notes) = await new BackupSetDirectory(root.Path).OpenNewestVerifiedAsync(CancellationToken.None);
        Assert.NotNull(set);
        await using (set)
        {
            Assert.Equal("20261113T085900Z", set.Name);
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, note => note.StartsWith("20261113T205900Z", StringComparison.Ordinal) && note.Contains("sağlama", StringComparison.Ordinal));
            using var tar = new MemoryStream();
            await set.WriteTarAsync(tar, CancellationToken.None);
            tar.Position = 0;
            var entries = new List<(string Name, string Content)>();
            await using var reader = new TarReader(tar);
            while (await reader.GetNextEntryAsync() is { } entry)
                entries.Add((entry.Name, entry.DataStream is null ? string.Empty : await new StreamReader(entry.DataStream).ReadToEndAsync()));
            Assert.Equal(["20261113T085900Z/", "20261113T085900Z/manifest.json", "20261113T085900Z/database.dump", "20261113T085900Z/private-volumes.tar.gz", "20261113T085900Z/SHA256SUMS"], entries.Select(x => x.Name));
            Assert.Equal("dump 20261113T085900Z", entries[2].Content);
        }
    }

    [Fact]
    public async Task ResumableUpload_SendsChunksInOrder_AndClosesAnExactChunkWithAnEmptyFinalRequest()
    {
        foreach (var size in new[] { GoogleDriveApi.ChunkSize + 10, GoogleDriveApi.ChunkSize })
        {
            var handler = new RecordingDriveHandler();
            var api = new GoogleDriveApi(new SingleClientFactory(new HttpClient(handler)));
            var payload = RandomNumberGenerator.GetBytes(size);
            var file = await api.UploadAsync("access", "folder-1", "20261113T085900Z.tar.age", (stream, token) => stream.WriteAsync(payload, token).AsTask(), CancellationToken.None);
            Assert.Equal(size, file.Size);
            Assert.Equal(payload, handler.Received.ToArray());
            var expected = size > GoogleDriveApi.ChunkSize
                ? new[] { $"bytes 0-{GoogleDriveApi.ChunkSize - 1}/*", $"bytes {GoogleDriveApi.ChunkSize}-{size - 1}/{size}" }
                : [$"bytes 0-{size - 1}/*", $"bytes */{size}"];
            Assert.Equal(expected, handler.Ranges);
            Assert.All(handler.Authorizations, value => Assert.Equal("Bearer access", value));
        }
    }

    [Fact]
    public void IdToken_GivesTheAccountEmail()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal("owner@gmail.com", GoogleDriveApi.EmailFromIdToken($"{Part("{\"alg\":\"RS256\"}")}.{Part("{\"email\":\"owner@gmail.com\",\"sub\":\"1\"}")}.sig"));
        Assert.Null(GoogleDriveApi.EmailFromIdToken("not-a-token"));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    // Google's resumable protocol: the start answers with a session Location, each chunk with 308 and the received
    // range, the last one with the file.
    private sealed class RecordingDriveHandler : HttpMessageHandler
    {
        public MemoryStream Received { get; } = new();
        public List<string> Ranges { get; } = [];
        public List<string> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (request.Method == HttpMethod.Post)
            {
                var start = new HttpResponseMessage(HttpStatusCode.OK);
                start.Headers.Location = new Uri("https://upload.example.invalid/session/1");
                return start;
            }
            var range = request.Content!.Headers.GetValues("Content-Range").Single();
            Ranges.Add(range);
            await request.Content.CopyToAsync(Received, cancellationToken);
            if (range.EndsWith("/*", StringComparison.Ordinal))
            {
                var partial = new HttpResponseMessage((HttpStatusCode)308);
                partial.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received.Length - 1}");
                return partial;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "file-1", name = "20261113T085900Z.tar.age", size = Received.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }) };
        }
    }
}

// A backup volume as backup.sh leaves it: sets named by time, SHA256SUMS with the container's absolute paths.
internal sealed class TempBackupRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ravencia-offsite-tests", Guid.NewGuid().ToString("N"));

    public TempBackupRoot() => Directory.CreateDirectory(Path);

    public void WriteSet(string name, bool corrupt = false)
    {
        var directory = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "database.dump"), $"dump {name}");
        File.WriteAllText(System.IO.Path.Combine(directory, "private-volumes.tar.gz"), $"files {name}");
        File.WriteAllText(System.IO.Path.Combine(directory, "manifest.json"), $"{{\"createdAt\":\"{name}\"}}");
        string Sum(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(directory, file))));
        File.WriteAllText(System.IO.Path.Combine(directory, "SHA256SUMS"), $"{Sum("database.dump")}  /backup/{name}/database.dump\n{Sum("private-volumes.tar.gz")}  /backup/{name}/private-volumes.tar.gz\n");
        if (corrupt) File.AppendAllText(System.IO.Path.Combine(directory, "database.dump"), "changed");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class OffsiteClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeGoogleDrive : IGoogleDriveApi
{
    public GoogleTokenGrant Grant { get; set; } = new("refresh-token-1", "access-1", $"openid email {OffsiteBackupRules.DriveFileScope}", "owner@gmail.com");
    public List<(string Code, string Verifier, string RedirectUri)> Exchanges { get; } = [];
    public List<string> Revoked { get; } = [];
    public List<string> Deleted { get; } = [];
    public Dictionary<string, DriveObject> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);
    public string? Folder { get; set; }
    public bool AuthorizationInvalid { get; set; }

    public Task<GoogleTokenGrant> ExchangeCodeAsync(GoogleOAuthClient client, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        Exchanges.Add((code, codeVerifier, redirectUri));
        return Task.FromResult(Grant);
    }

    public Task<string> RefreshAccessTokenAsync(GoogleOAuthClient client, string refreshToken, CancellationToken cancellationToken) =>
        AuthorizationInvalid ? throw new GoogleDriveException("GOOGLE_AUTH_INVALID", "Google yetkisi geçersiz.", authorization: true) : Task.FromResult("access-" + refreshToken);

    public Task RevokeAsync(string token, CancellationToken cancellationToken) { Revoked.Add(token); return Task.CompletedTask; }
    public Task<string?> FindFolderAsync(string accessToken, string name, CancellationToken cancellationToken) => Task.FromResult(Folder);
    public Task<bool> FolderExistsAsync(string accessToken, string folderId, CancellationToken cancellationToken) => Task.FromResult(folderId == Folder);
    public Task<string> CreateFolderAsync(string accessToken, string name, CancellationToken cancellationToken) => Task.FromResult(Folder = "folder-" + name.Replace(' ', '-'));
    public Task<IReadOnlyList<DriveObject>> ListAsync(string accessToken, string folderId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DriveObject>>(Files.Values.ToList());

    public async Task<DriveObject> UploadAsync(string accessToken, string folderId, string name, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await writeContent(buffer, cancellationToken);
        var file = new DriveObject("id-" + name, name, buffer.Length);
        Files[name] = file;
        Uploaded[name] = buffer.ToArray();
        return file;
    }

    public Task DeleteAsync(string accessToken, string fileId, CancellationToken cancellationToken)
    {
        Deleted.Add(fileId);
        foreach (var key in Files.Where(pair => pair.Value.Id == fileId).Select(pair => pair.Key).ToList()) Files.Remove(key);
        return Task.CompletedTask;
    }
}

// Stands in for the age binary: marks the stream so the test sees it went through the encryptor.
internal sealed class MarkingEncryptor : IBackupEncryptor
{
    public List<string> Recipients { get; } = [];

    public async Task EncryptAsync(string recipient, Func<Stream, CancellationToken, Task> writePlain, Stream output, CancellationToken cancellationToken)
    {
        Recipients.Add(recipient);
        await output.WriteAsync("age-encryption.org/v1\n"u8.ToArray(), cancellationToken);
        await writePlain(output, cancellationToken);
    }
}

internal sealed class RecordingAlertSender : IAlertEmailSender
{
    public List<string> Messages { get; } = [];
    public Task SendAsync(AlertEmailCredentials credentials, string message, CancellationToken cancellationToken) { Messages.Add(message); return Task.CompletedTask; }
}

[Collection("postgres-integration")]
public sealed class OffsiteBackupPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string ClientId = "123456789-abcdefghijklmnop.apps.googleusercontent.com";
    private const string ClientSecret = "GOCSPX-test-secret-0001";
    private const string RedirectUri = "https://panel.example.invalid/settings/google-drive-callback";
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();

    [PostgreSqlFact]
    public async Task GoogleConnection_StateIsOneTimeShortLivedAndBoundToTheUser_SecretsAreStoredEncryptedAndRevokedOnDisconnect()
    {
        await using var db = fixture.CreateContext();
        var tenantId = await NewTenantAsync(db);
        var owner = Guid.CreateVersion7();
        var clock = new OffsiteClock(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var google = new FakeGoogleDrive();
        var service = new OffsiteBackupSettingsService(db, protection, google, clock);

        var refused = await service.SaveAsync(tenantId, owner, "t", new(true, OffsiteBackupRulesTests.Recipient, null, null, null, null, null, null), null, CancellationToken.None);
        Assert.Equal("OFFSITE_BACKUP_CLIENT_REQUIRED", refused.Error?.Code);
        var saved = await service.SaveAsync(tenantId, owner, "t", new(true, OffsiteBackupRulesTests.Recipient, "NAME", "", 14, 12, ClientId, ClientSecret), null, CancellationToken.None);
        Assert.True(saved.View!.ClientSecretSaved);
        Assert.False(saved.View.Connected);
        Assert.Equal(OffsiteBackupRules.DefaultFolderName, saved.View.FolderValue);
        Assert.DoesNotContain(ClientSecret, await RowsAsync(db, tenantId), StringComparison.Ordinal);

        // Another user cannot finish what the owner started, and the state is spent by the attempt.
        var (url, _) = await service.BeginAuthorizationAsync(tenantId, owner, RedirectUri, "t", CancellationToken.None);
        var state = QueryValue(url!, "state");
        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);
        Assert.Contains("access_type=offline", url, StringComparison.Ordinal);
        Assert.Equal("OFFSITE_BACKUP_STATE_INVALID", (await service.CompleteAuthorizationAsync(tenantId, Guid.CreateVersion7(), "code", state, null, "t", CancellationToken.None)).Error?.Code);
        Assert.Equal("OFFSITE_BACKUP_STATE_INVALID", (await service.CompleteAuthorizationAsync(tenantId, owner, "code", state, null, "t", CancellationToken.None)).Error?.Code);

        // An unknown state does not touch the pending one; an old one has expired.
        (url, _) = await service.BeginAuthorizationAsync(tenantId, owner, RedirectUri, "t", CancellationToken.None);
        Assert.Equal("OFFSITE_BACKUP_STATE_INVALID", (await service.CompleteAuthorizationAsync(tenantId, owner, "code", "forged-state", null, "t", CancellationToken.None)).Error?.Code);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal("OFFSITE_BACKUP_STATE_EXPIRED", (await service.CompleteAuthorizationAsync(tenantId, owner, "code", QueryValue(url!, "state"), null, "t", CancellationToken.None)).Error?.Code);
        Assert.Empty(google.Exchanges);

        // Drive left unticked at the consent: refused and the grant revoked.
        google.Grant = google.Grant with { Scope = "openid email", RefreshToken = "refresh-without-drive" };
        (url, _) = await service.BeginAuthorizationAsync(tenantId, owner, RedirectUri, "t", CancellationToken.None);
        Assert.Equal("OFFSITE_BACKUP_SCOPE_MISSING", (await service.CompleteAuthorizationAsync(tenantId, owner, "code-0", QueryValue(url!, "state"), null, "t", CancellationToken.None)).Error?.Code);
        Assert.Contains("refresh-without-drive", google.Revoked);

        google.Grant = new("refresh-token-1", "access-1", $"openid email {OffsiteBackupRules.DriveFileScope}", "owner@gmail.com");
        (url, _) = await service.BeginAuthorizationAsync(tenantId, owner, RedirectUri, "t", CancellationToken.None);
        var connected = await service.CompleteAuthorizationAsync(tenantId, owner, "code-1", QueryValue(url!, "state"), null, "t", CancellationToken.None);
        Assert.Null(connected.Error);
        Assert.True(connected.View!.Connected);
        Assert.Equal("owner@gmail.com", connected.View.Account);
        var exchange = google.Exchanges.Last();
        Assert.Equal(("code-1", RedirectUri), (exchange.Code, exchange.RedirectUri));
        // PKCE: the verifier sent to Google hashes to the challenge in the consent address.
        Assert.Equal(QueryValue(url!, "code_challenge"), Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(exchange.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        var rows = await RowsAsync(db, tenantId);
        Assert.DoesNotContain("refresh-token-1", rows, StringComparison.Ordinal);
        Assert.DoesNotContain(exchange.Verifier, rows, StringComparison.Ordinal);
        var (settings, stored) = await service.LoadAsync(tenantId, CancellationToken.None);
        var (context, problem) = service.RunContext(tenantId, settings, stored);
        Assert.Null(problem);
        Assert.Equal(("refresh-token-1", ClientSecret), (context!.RefreshToken, context.Client.ClientSecret));
        // A value copied to another tenant does not decrypt there.
        Assert.Null(service.Unprotect(Guid.CreateVersion7(), stored.RefreshTokenProtected!));

        var disconnected = await service.DisconnectAsync(tenantId, owner, "t", CancellationToken.None);
        Assert.False(disconnected.View!.Connected);
        Assert.Contains("refresh-token-1", google.Revoked);
        Assert.Null((await service.LoadAsync(tenantId, CancellationToken.None)).State.RefreshTokenProtected);
        Assert.True(await db.AuditLogs.AnyAsync(x => x.TenantId == tenantId && x.Action == "OFFSITE_BACKUP_GOOGLE_DISCONNECTED"));
    }

    [PostgreSqlFact]
    public async Task Copier_UploadsTheNewestVerifiedSetOnce_KeepsRetention_StopsOnAnInvalidAuthorizationWithOneAlertAndReportsTheRecovery()
    {
        await using var db = fixture.CreateContext();
        var tenantId = await NewTenantAsync(db);
        var owner = Guid.CreateVersion7();
        var clock = new OffsiteClock(new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero));
        var google = new FakeGoogleDrive();
        var service = new OffsiteBackupSettingsService(db, protection, google, clock);
        await service.SaveAsync(tenantId, owner, "t", new(true, OffsiteBackupRulesTests.Recipient, "NAME", "Ravencia Yedek", 14, 12, ClientId, ClientSecret), null, CancellationToken.None);
        await ConnectAsync(service, tenantId, owner);
        var alerts = new AlertEmailSettingsService(db, protection, clock);
        await alerts.SaveAsync(tenantId, owner, "t", "ravencia.alarm@gmail.com", ["owner@example.com"], "abcdefghijklmnop", null, CancellationToken.None);

        using var root = new TempBackupRoot();
        root.WriteSet("20261112T085900Z");
        root.WriteSet("20261113T085900Z");
        root.WriteSet("20261113T205900Z", corrupt: true);
        google.Files["20261001T085900Z.tar.age"] = new("old-1", "20261001T085900Z.tar.age", 10);
        google.Files["20261002T085900Z.tar.age"] = new("old-2", "20261002T085900Z.tar.age", 10);
        google.Files["notes.txt"] = new("note", "notes.txt", 10);
        var encryptor = new MarkingEncryptor();
        var sender = new RecordingAlertSender();
        var copier = new OffsiteBackupCopier(service, google, encryptor, new BackupSetDirectory(root.Path), new PanelNotificationService(db, clock), alerts, sender, clock, NullLogger<OffsiteBackupCopier>.Instance);

        Assert.Equal(OffsiteBackupRunResult.Uploaded, await copier.RunAsync(await ContextAsync(service, tenantId), CancellationToken.None));
        Assert.Equal([OffsiteBackupRulesTests.Recipient], encryptor.Recipients);
        Assert.StartsWith("age-encryption.org/v1", Encoding.ASCII.GetString(google.Uploaded["20261113T085900Z.tar.age"]), StringComparison.Ordinal);
        Assert.Equal(["old-2"], google.Deleted);
        Assert.True(google.Files.ContainsKey("notes.txt"));
        var view = await service.GetAsync(tenantId, CancellationToken.None);
        Assert.Equal(("20261113T085900Z", 2, 0), (view.LastSet, view.RemoteCopies, view.ConsecutiveFailures));
        Assert.Equal("folder-Ravencia-Yedek", view.FolderId);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 21, 30, 0, TimeSpan.Zero), view.NextRunAt);

        clock.Now = clock.Now.AddHours(12);
        Assert.Equal(OffsiteBackupRunResult.AlreadyPresent, await copier.RunAsync(await ContextAsync(service, tenantId), CancellationToken.None));

        // Google refuses the authorization: the copy stops, the owner gets one e-mail and a notice until reconnecting.
        google.AuthorizationInvalid = true;
        Assert.Equal(OffsiteBackupRunResult.AuthorizationInvalid, await copier.RunAsync(await ContextAsync(service, tenantId), CancellationToken.None));
        Assert.Equal(OffsiteBackupRunResult.AuthorizationInvalid, await copier.RunAsync(await ContextAsync(service, tenantId), CancellationToken.None));
        Assert.Single(sender.Messages);
        view = await service.GetAsync(tenantId, CancellationToken.None);
        Assert.True(view.AuthorizationInvalid);
        Assert.Null(view.NextRunAt);
        var notices = await new AttentionNoticeService(db, clock).ListAsync(tenantId, CancellationToken.None);
        Assert.Contains(notices, notice => notice.Code == AttentionNoticePolicy.OffsiteBackupFailingCode);
        Assert.Equal("GOOGLE_AUTH_INVALID", (await service.RequestRunAsync(tenantId, owner, "t", CancellationToken.None))?.Code);

        // Reconnected: the next copy works and the owner hears that it does.
        google.AuthorizationInvalid = false;
        await ConnectAsync(service, tenantId, owner);
        google.Files.Remove("20261113T085900Z.tar.age");
        Assert.Equal(OffsiteBackupRunResult.Uploaded, await copier.RunAsync(await ContextAsync(service, tenantId), CancellationToken.None));
        Assert.Equal(2, sender.Messages.Count);
        Assert.Contains("yeniden çalışıyor", sender.Messages[1], StringComparison.Ordinal);
        Assert.DoesNotContain(await new AttentionNoticeService(db, clock).ListAsync(tenantId, CancellationToken.None), notice => notice.Code == AttentionNoticePolicy.OffsiteBackupFailingCode);
        Assert.True(await db.PanelNotifications.AnyAsync(x => x.TenantId == tenantId && x.UserId == null && x.ClientKey.StartsWith("offsite-backup:recovered:")));
    }

    private async Task ConnectAsync(OffsiteBackupSettingsService service, Guid tenantId, Guid owner)
    {
        var (url, _) = await service.BeginAuthorizationAsync(tenantId, owner, RedirectUri, "t", CancellationToken.None);
        var result = await service.CompleteAuthorizationAsync(tenantId, owner, "code", QueryValue(url!, "state"), null, "t", CancellationToken.None);
        Assert.Null(result.Error);
    }

    private static async Task<OffsiteBackupRunContext> ContextAsync(OffsiteBackupSettingsService service, Guid tenantId)
    {
        var (settings, state) = await service.LoadAsync(tenantId, CancellationToken.None);
        return service.RunContext(tenantId, settings, state).Context!;
    }

    private static async Task<Guid> NewTenantAsync(AppDbContext db)
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = id, Code = $"offsite-{id:N}"[..30], DisplayName = "Offsite", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<string> RowsAsync(AppDbContext db, Guid tenantId) =>
        string.Join("\n", await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.ValueJson).ToListAsync());

    private static string QueryValue(string url, string key) =>
        Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Single(part => part[0] == key)[1]);
}

[Collection("postgres-integration")]
public sealed class OffsiteBackupEndToEndTests(ApiEndToEndFixture fixture) : IClassFixture<ApiEndToEndFixture>
{
    [PostgreSqlFact]
    public async Task Settings_AreForOwnersAndAdministrators_TakeThePassword_AndNeverReturnTheSecret()
    {
        var (admin, csrf, adminSignIn) = await fixture.SignInAsync(MembershipRole.Administrator);
        var (operations, _, operationsSignIn) = await fixture.SignInAsync(MembershipRole.Operations);
        using (admin)
        using (adminSignIn)
        using (operations)
        using (operationsSignIn)
        {
            using var forbidden = await operations.GetAsync("/api/v1/settings/offsite-backup");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            var body = new { enabled = false, ageRecipient = OffsiteBackupRulesTests.Recipient, folderMode = "NAME", folderValue = "", keepDays = 14, keepMonths = 12, clientId = "123456789-abcdefghijklmnop.apps.googleusercontent.com", clientSecret = "GOCSPX-e2e-secret-000001" };
            using var withoutPassword = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/offsite-backup", csrf, body));
            Assert.False(withoutPassword.IsSuccessStatusCode);

            using var saved = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Put, "/api/v1/settings/offsite-backup", csrf, new { body.enabled, body.ageRecipient, body.folderMode, body.folderValue, body.keepDays, body.keepMonths, body.clientId, body.clientSecret, password = ApiEndToEndFixture.Password }));
            var savedText = await saved.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.DoesNotContain("GOCSPX-e2e-secret-000001", savedText, StringComparison.Ordinal);
            var view = JsonDocument.Parse(savedText).RootElement;
            Assert.True(view.GetProperty("clientSecretSaved").GetBoolean());
            Assert.EndsWith("/settings/google-drive-callback", view.GetProperty("redirectUri").GetString(), StringComparison.Ordinal);

            using var authorize = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/settings/offsite-backup/google/authorize", csrf, new { password = ApiEndToEndFixture.Password }));
            Assert.Equal(HttpStatusCode.OK, authorize.StatusCode);
            var url = JsonDocument.Parse(await authorize.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
            Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url, StringComparison.Ordinal);
            Assert.DoesNotContain("GOCSPX", url, StringComparison.Ordinal);

            using var forged = await admin.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/settings/offsite-backup/google/complete", csrf, new { code = "x", state = "forged" }));
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            Assert.Equal("OFFSITE_BACKUP_STATE_INVALID", (await ApiTestHost.ProblemAsync(forged)).GetProperty("code").GetString());

            using var read = await admin.GetAsync("/api/v1/settings/offsite-backup");
            Assert.DoesNotContain("GOCSPX-e2e-secret-000001", await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await using var scope = fixture.Host!.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.DoesNotContain(await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == fixture.TenantId).Select(x => x.ValueJson).ToListAsync(), json => json.Contains("GOCSPX-e2e-secret-000001", StringComparison.Ordinal));
        }
    }
}
