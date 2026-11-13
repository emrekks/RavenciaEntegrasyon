using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Infrastructure.Backups;

public sealed record GoogleOAuthClient(string ClientId, string ClientSecret);

// What the authorization-code exchange gave: the long-lived refresh token, the scopes Google granted and the account.
public sealed record GoogleTokenGrant(string? RefreshToken, string AccessToken, string? Scope, string? Email);

public sealed record DriveObject(string Id, string Name, long Size);

// Authorization: the refresh token is no longer accepted (revoked by the owner, expired, or the client changed);
// nothing works again until the owner reconnects. Anything else is passing and is retried.
public sealed class GoogleDriveException(string code, string message, bool authorization = false, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Authorization { get; } = authorization;
}

// The Google calls the off-site copy makes: OAuth (code exchange with PKCE, refresh, revoke) and Drive v3 (folders,
// listing, resumable upload, delete), all with the drive.file scope. A fake replaces it in the tests.
public interface IGoogleDriveApi
{
    Task<GoogleTokenGrant> ExchangeCodeAsync(GoogleOAuthClient client, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken);
    Task<string> RefreshAccessTokenAsync(GoogleOAuthClient client, string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string token, CancellationToken cancellationToken);
    Task<string?> FindFolderAsync(string accessToken, string name, CancellationToken cancellationToken);
    Task<bool> FolderExistsAsync(string accessToken, string folderId, CancellationToken cancellationToken);
    Task<string> CreateFolderAsync(string accessToken, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<DriveObject>> ListAsync(string accessToken, string folderId, CancellationToken cancellationToken);
    // Streams what writeContent writes as one new file in the folder; returns the stored file.
    Task<DriveObject> UploadAsync(string accessToken, string folderId, string name, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken);
    Task DeleteAsync(string accessToken, string fileId, CancellationToken cancellationToken);
}

public sealed class GoogleDriveApi(IHttpClientFactory httpClientFactory) : IGoogleDriveApi
{
    public const string HttpClientName = "GoogleDrive";
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    // Resumable chunks must be multiples of 256 KiB.
    internal const int ChunkSize = 32 * 256 * 1024;

    private HttpClient Http() => httpClientFactory.CreateClient(HttpClientName);

    public async Task<GoogleTokenGrant> ExchangeCodeAsync(GoogleOAuthClient client, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var response = await Http().PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier
        }), cancellationToken);
        var token = await TokenAsync(response, cancellationToken);
        return new GoogleTokenGrant(token.RefreshToken, token.AccessToken ?? string.Empty, token.Scope, EmailFromIdToken(token.IdToken));
    }

    public async Task<string> RefreshAccessTokenAsync(GoogleOAuthClient client, string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await Http().PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        }), cancellationToken);
        var token = await TokenAsync(response, cancellationToken);
        return token.AccessToken is { Length: > 0 } accessToken ? accessToken : throw new GoogleDriveException("GOOGLE_TOKEN_EMPTY", "Google erişim anahtarı vermedi.");
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await Http().PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), cancellationToken);
        // 400 invalid_token: already revoked or expired, which is what was asked for.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            throw new GoogleDriveException("GOOGLE_REVOKE_FAILED", $"Google bağlantıyı iptal edemedi (HTTP {(int)response.StatusCode}).");
    }

    public async Task<string?> FindFolderAsync(string accessToken, string name, CancellationToken cancellationToken)
    {
        var query = $"name = '{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}' and mimeType = '{FolderMimeType}' and 'root' in parents and trashed = false";
        using var request = Authorized(HttpMethod.Get, $"{FilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=files(id,name)&pageSize=10&orderBy=createdTime", accessToken);
        using var response = await Http().SendAsync(request, cancellationToken);
        var list = await JsonAsync<FileList>(response, "Drive klasörü aranamadı", cancellationToken);
        return list.Files?.FirstOrDefault()?.Id;
    }

    public async Task<bool> FolderExistsAsync(string accessToken, string folderId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, $"{FilesEndpoint}/{Uri.EscapeDataString(folderId)}?fields=id,mimeType,trashed", accessToken);
        using var response = await Http().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        var file = await JsonAsync<FileResource>(response, "Drive klasörü okunamadı", cancellationToken);
        return file.MimeType == FolderMimeType && file.Trashed != true;
    }

    public async Task<string> CreateFolderAsync(string accessToken, string name, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, $"{FilesEndpoint}?fields=id", accessToken);
        request.Content = JsonContent.Create(new { name, mimeType = FolderMimeType });
        using var response = await Http().SendAsync(request, cancellationToken);
        var file = await JsonAsync<FileResource>(response, "Drive klasörü oluşturulamadı", cancellationToken);
        return file.Id ?? throw new GoogleDriveException("DRIVE_FOLDER_CREATE_FAILED", "Drive klasörü oluşturulamadı.");
    }

    public async Task<IReadOnlyList<DriveObject>> ListAsync(string accessToken, string folderId, CancellationToken cancellationToken)
    {
        var objects = new List<DriveObject>();
        string? pageToken = null;
        var query = $"'{folderId}' in parents and trashed = false";
        do
        {
            var url = $"{FilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=nextPageToken,files(id,name,size)&pageSize=1000{(pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken))}";
            using var request = Authorized(HttpMethod.Get, url, accessToken);
            using var response = await Http().SendAsync(request, cancellationToken);
            var list = await JsonAsync<FileList>(response, "Drive klasörü listelenemedi", cancellationToken);
            objects.AddRange((list.Files ?? []).Where(file => file.Id is not null && file.Name is not null).Select(file => new DriveObject(file.Id!, file.Name!, ParseSize(file.Size))));
            pageToken = list.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && objects.Count < 20_000);
        return objects;
    }

    public async Task<DriveObject> UploadAsync(string accessToken, string folderId, string name, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken)
    {
        using var start = Authorized(HttpMethod.Post, $"{UploadEndpoint}?uploadType=resumable&fields=id,name,size", accessToken);
        start.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        start.Content = JsonContent.Create(new { name, parents = new[] { folderId }, mimeType = "application/octet-stream" });
        using var started = await Http().SendAsync(start, cancellationToken);
        if (!started.IsSuccessStatusCode) await ThrowAsync(started, "Drive yüklemesi başlatılamadı", cancellationToken);
        var session = started.Headers.Location ?? throw new GoogleDriveException("DRIVE_UPLOAD_NO_SESSION", "Drive yükleme oturumu açmadı.");

        // The content is produced while it is sent: the writer fills a pipe, chunks are cut from it and PUT in order.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: ChunkSize * 2L, resumeWriterThreshold: ChunkSize));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await using var stream = pipe.Writer.AsStream(leaveOpen: true);
                await writeContent(stream, linked.Token);
                await stream.FlushAsync(linked.Token);
            }
            catch (Exception exception) { failure = exception; }
            await pipe.Writer.CompleteAsync(failure);
        }, CancellationToken.None);

        try
        {
            await using var source = pipe.Reader.AsStream();
            var buffer = new byte[ChunkSize];
            long offset = 0;
            while (true)
            {
                var filled = await FillAsync(source, buffer, linked.Token);
                var last = filled < buffer.Length;
                if (filled == 0 && offset == 0)
                    throw new GoogleDriveException("DRIVE_UPLOAD_EMPTY", "Yüklenecek içerik boş.");
                // A full chunk may still be the last one; then the size is announced with an empty final request.
                var total = last ? (offset + filled).ToString(System.Globalization.CultureInfo.InvariantCulture) : "*";
                using var put = Authorized(HttpMethod.Put, session.ToString(), accessToken);
                put.Content = new ByteArrayContent(buffer, 0, filled);
                put.Content.Headers.ContentLength = filled;
                put.Content.Headers.TryAddWithoutValidation("Content-Range", filled == 0 ? $"bytes */{total}" : $"bytes {offset}-{offset + filled - 1}/{total}");
                using var response = await Http().SendAsync(put, linked.Token);
                offset += filled;
                if ((int)response.StatusCode == 308)
                {
                    if (last) throw new GoogleDriveException("DRIVE_UPLOAD_INCOMPLETE", "Drive yüklemenin bittiğini kabul etmedi.");
                    // Google says how much it has; anything short of what was sent would leave a hole.
                    var range = response.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null;
                    if (range is null || !range.EndsWith($"-{offset - 1}", StringComparison.Ordinal))
                        throw new GoogleDriveException("DRIVE_UPLOAD_PARTIAL", "Drive yüklenen parçanın tamamını almadı; kopya bir sonraki denemede yeniden gönderilecek.");
                    continue;
                }
                if (!response.IsSuccessStatusCode) await ThrowAsync(response, "Drive yüklemesi tamamlanamadı", linked.Token);
                var file = await response.Content.ReadFromJsonAsync<FileResource>(linked.Token)
                    ?? throw new GoogleDriveException("DRIVE_UPLOAD_NO_FILE", "Drive yüklenen dosyayı bildirmedi.");
                await producer;
                return new DriveObject(file.Id ?? string.Empty, file.Name ?? name, ParseSize(file.Size));
            }
        }
        catch
        {
            await linked.CancelAsync();
            await pipe.Reader.CompleteAsync();
            try { await producer; } catch { /* the first failure is the one reported */ }
            throw;
        }
    }

    public async Task DeleteAsync(string accessToken, string fileId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Delete, $"{FilesEndpoint}/{Uri.EscapeDataString(fileId)}", accessToken);
        using var response = await Http().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, "Drive'daki eski kopya silinemedi", cancellationToken);
    }

    // The account's e-mail from the ID token Google returned in the same TLS response (no signature check is needed for
    // a value only shown on the panel).
    internal static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException) { return null; }
    }

    private static async Task<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0) break;
            filled += read;
        }
        return filled;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static long ParseSize(string? value) => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0;

    private static async Task<TokenResponse> TokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        TokenResponse? token = null;
        try { token = JsonSerializer.Deserialize<TokenResponse>(text); }
        catch (JsonException) { }
        if (response.IsSuccessStatusCode && token is not null) return token;
        var error = token?.Error ?? $"HTTP {(int)response.StatusCode}";
        // invalid_grant: the refresh token or code is no longer valid; invalid_client / unauthorized_client: the OAuth client
        // was deleted or its secret changed. Both need the owner.
        if (error is "invalid_grant")
            throw new GoogleDriveException("GOOGLE_AUTH_INVALID", "Google yetkisi geçersiz (iptal edilmiş ya da süresi dolmuş). Google ile yeniden bağlanın.", authorization: true);
        if (error is "invalid_client" or "unauthorized_client")
            throw new GoogleDriveException("GOOGLE_CLIENT_INVALID", "Google OAuth istemcisi kabul edilmedi: istemci kimliğini ve gizli anahtarı kontrol edin, sonra yeniden bağlanın.", authorization: true);
        throw new GoogleDriveException("GOOGLE_TOKEN_FAILED", $"Google oturum anahtarı alınamadı ({Clip(error)}{(token?.ErrorDescription is { Length: > 0 } description ? ": " + Clip(description) : string.Empty)}).");
    }

    private static async Task<T> JsonAsync<T>(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, action, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new GoogleDriveException("DRIVE_EMPTY_RESPONSE", $"{action}: Drive boş yanıt verdi.");
    }

    private static async Task ThrowAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string? reason = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = error.TryGetProperty("message", out var value) ? value.GetString() : null;
                if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 && errors[0].TryGetProperty("reason", out var first))
                    reason = first.GetString();
            }
        }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        if (status == 401)
            throw new GoogleDriveException("GOOGLE_AUTH_INVALID", "Google yetkisi geçersiz. Google ile yeniden bağlanın.", authorization: true);
        if (status == 403 && reason is "storageQuotaExceeded")
            throw new GoogleDriveException("DRIVE_QUOTA_EXCEEDED", "Google Drive alanı dolu: Drive'da yer açın ya da saklama süresini kısaltın.");
        if (status == 403 && reason is "insufficientPermissions" or "appNotAuthorizedToFile" or "forbidden")
            throw new GoogleDriveException("DRIVE_PERMISSION", $"{action}: uygulamanın bu klasöre erişimi yok (drive.file yalnız uygulamanın kendi oluşturduğu klasörleri görür).");
        if (status == 404)
            throw new GoogleDriveException("DRIVE_NOT_FOUND", $"{action}: klasör ya da dosya bulunamadı (silinmiş ya da uygulamanın oluşturmadığı bir klasör).");
        throw new GoogleDriveException("DRIVE_HTTP_" + status, $"{action} (HTTP {status}{(message is { Length: > 0 } ? ": " + Clip(message) : string.Empty)}).");
    }

    private static string Clip(string value) => value.Length <= 200 ? value : value[..200];

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class FileList
    {
        [JsonPropertyName("files")] public List<FileResource>? Files { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    private sealed class FileResource
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public string? Size { get; set; }
        [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
        [JsonPropertyName("trashed")] public bool? Trashed { get; set; }
    }
}
