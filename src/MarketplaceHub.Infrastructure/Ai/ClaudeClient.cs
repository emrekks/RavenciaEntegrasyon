using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Skills;
using Anthropic.Models.Skills.Versions;
using Beta = Anthropic.Models.Beta.Messages;

namespace MarketplaceHub.Infrastructure.Ai;

// What a Claude call used, as the API reported it (null when it reported nothing).
public sealed record ClaudeUsage(long? InputTokens, long? OutputTokens, long? CacheReadInputTokens, long? CacheCreationInputTokens);

// A file the model wrote in the code-execution container (an output of a skill).
public sealed record ClaudeOutputFile(string FileId, string FileName);

public sealed record ClaudeSkillRequest(
    string ApiKey,
    string Model,
    string SkillId,
    string SkillVersion,
    string System,
    string Prompt,
    string Effort,
    int MaxTokens,
    TimeSpan Timeout,
    IReadOnlyList<ClaudeImage>? Images = null);

// A picture sent with the prompt: either bytes (JPEG or PNG) or an https URL Claude fetches itself.
public sealed record ClaudeImage(byte[]? Data, string? MediaType, string? Url);

// The outcome of a skill run: the model's text, the files it produced, how it stopped and what it used.
public sealed record ClaudeSkillResult(string StopReason, string Text, IReadOnlyList<ClaudeOutputFile> Files, ClaudeUsage Usage, string? RefusalExplanation);

public sealed record ClaudeSkillUpload(string SkillId, string Version);

public sealed record ClaudeSkillFile(string Path, byte[] Content);

// A failure of a Claude call with a message for the person (Turkish) and a status for the panel.
public sealed class ClaudeException(string code, string message, int status, ClaudeUsage? usage = null, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public ClaudeUsage? Usage { get; } = usage;
}

// The panel's door to the Claude API (official Anthropic SDK). Shared by every AI feature; the key comes from
// Ayarlar › Yapay Zekâ and is passed per call, never read from the environment. The HttpClient comes from the
// factory ("anthropic"), so tests give it a fake handler and nothing reaches the real API.
public interface IClaudeClient
{
    Task<ClaudeUsage> TestAsync(string apiKey, string model, CancellationToken cancellationToken);
    // existingSkillId: a new version of that skill (its SKILL.md name must stay the same); null creates a new skill.
    Task<ClaudeSkillUpload> UploadSkillAsync(string apiKey, string displayTitle, IReadOnlyList<ClaudeSkillFile> files, string? existingSkillId, CancellationToken cancellationToken);
    Task<ClaudeSkillResult> RunSkillAsync(ClaudeSkillRequest request, CancellationToken cancellationToken);
    Task<byte[]> DownloadFileAsync(string apiKey, string fileId, long maxBytes, CancellationToken cancellationToken);
}

public sealed class ClaudeClient(IHttpClientFactory httpClients) : IClaudeClient
{
    public const string HttpClientName = "anthropic";
    public const string CodeExecutionBeta = "code-execution-2025-08-25";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const string CancelledCode = "AI_CANCELLED";

    private AnthropicClient Client(string apiKey, TimeSpan timeout) => new()
    {
        ApiKey = apiKey,
        HttpClient = httpClients.CreateClient(HttpClientName),
        Timeout = timeout,
        MaxRetries = 1
    };

    public async Task<ClaudeUsage> TestAsync(string apiKey, string model, CancellationToken cancellationToken)
    {
        var message = await Call(() => Client(apiKey, TimeSpan.FromSeconds(30)).Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new UserMessageParam { Content = "Reply with the single word: ok" }]
        }, cancellationToken), cancellationToken);
        return Usage(message.Usage);
    }

    public async Task<ClaudeSkillUpload> UploadSkillAsync(string apiKey, string displayTitle, IReadOnlyList<ClaudeSkillFile> files, string? existingSkillId, CancellationToken cancellationToken)
    {
        if (existingSkillId is not null)
        {
            var version = await Call(() => Client(apiKey, TimeSpan.FromMinutes(2)).Skills.Versions.Create(existingSkillId, new VersionCreateParams
            {
                Files = files.Select(file => new BinaryContent { Stream = new MemoryStream(file.Content), FileName = file.Path }).ToList()
            }, cancellationToken), cancellationToken);
            if (string.IsNullOrWhiteSpace(version.ID))
                throw new ClaudeException("AI_SKILL_UPLOAD_EMPTY", "Claude beceri sürümünü kaydetti ama sürüm kimliğini bildirmedi.", 502);
            return new(existingSkillId, version.ID);
        }
        var skill = await Call(() => Client(apiKey, TimeSpan.FromMinutes(2)).Skills.Create(new SkillCreateParams
        {
            DisplayName = displayTitle,
            Files = files.Select(file => new BinaryContent { Stream = new MemoryStream(file.Content), FileName = file.Path }).ToList()
        }, cancellationToken), cancellationToken);
        if (string.IsNullOrWhiteSpace(skill.ID) || string.IsNullOrWhiteSpace(skill.LatestVersionID))
            throw new ClaudeException("AI_SKILL_UPLOAD_EMPTY", "Claude beceriyi kaydetti ama kimliğini ya da sürümünü bildirmedi.", 502);
        return new(skill.ID, skill.LatestVersionID);
    }

    public async Task<ClaudeSkillResult> RunSkillAsync(ClaudeSkillRequest request, CancellationToken cancellationToken)
    {
        var client = Client(request.ApiKey, request.Timeout);
        // Agent Skills run in the code-execution container: the beta messages path with the code-execution beta.
        var message = await Call(() => client.Beta.Messages.Create(new Beta.MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Betas = [CodeExecutionBeta, FallbackBeta],
            // A policy decline is retried server-side on the model's default fallback (Claude API only).
            Fallbacks = new Beta.Default(),
            Thinking = new Beta.BetaThinkingConfigAdaptive(),
            OutputConfig = new Beta.BetaOutputConfig { Effort = request.Effort == "medium" ? Beta.Effort.Medium : Beta.Effort.Low },
            System = request.System,
            Container = new Beta.BetaContainerParams { Skills = [new Beta.BetaSkillParams { Type = Beta.BetaSkillParamsType.Custom, SkillID = request.SkillId, Version = request.SkillVersion }] },
            Tools = [new Beta.BetaCodeExecutionTool20260521()],
            Messages = [request.Images is { Count: > 0 } ? new Beta.BetaUserMessageParam { Content = Blocks(request.Images, request.Prompt) } : new Beta.BetaUserMessageParam { Content = request.Prompt }]
        }, cancellationToken), cancellationToken);

        var text = new List<string>();
        var fileIds = new List<string>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock)) text.Add(textBlock.Text);
            else if (block.TryPickBashCodeExecutionToolResult(out var bash) && bash.Content.TryPickBetaBashCodeExecutionResultBlock(out var result))
                fileIds.AddRange(result.Content.Select(output => output.FileID));
            // A file written by a Python run comes back on the code-execution result, not on a bash one.
            else if (block.TryPickCodeExecutionToolResult(out var code))
            {
                if (code.Content.TryPickResultBlock(out var codeResult)) fileIds.AddRange(codeResult.Content.Select(output => output.FileID));
                else if (code.Content.TryPickEncryptedCodeExecutionResultBlock(out var encrypted)) fileIds.AddRange(encrypted.Content.Select(output => output.FileID));
            }
        }
        // D4: the message is billed already; a failed file lookup after it keeps its usage, so the cost is logged.
        var usage = message.Usage is { } reported ? new ClaudeUsage(reported.InputTokens, reported.OutputTokens, reported.CacheReadInputTokens, reported.CacheCreationInputTokens) : new ClaudeUsage(null, null, null, null);
        var files = new List<ClaudeOutputFile>();
        try
        {
            foreach (var fileId in fileIds.Distinct(StringComparer.Ordinal))
            {
                var metadata = await Call(() => client.Files.RetrieveMetadata(fileId, cancellationToken: cancellationToken), cancellationToken);
                files.Add(new ClaudeOutputFile(fileId, metadata.Filename));
            }
        }
        catch (ClaudeException exception) when (exception.Usage is null)
        {
            throw new ClaudeException(exception.Code, exception.Message, exception.Status, usage, exception.InnerException);
        }
        return new(message.StopReason?.Raw() ?? "", string.Join("\n", text), files, usage, message.StopDetails?.Explanation);
    }

    // The pictures first, then the prompt.
    private static List<Beta.BetaContentBlockParam> Blocks(IReadOnlyList<ClaudeImage> images, string prompt)
    {
        List<Beta.BetaContentBlockParam> blocks = [];
        foreach (var image in images)
            blocks.Add(new Beta.BetaImageBlockParam
            {
                Source = image.Data is { } data
                    ? new Beta.BetaBase64ImageSource { Data = Convert.ToBase64String(data), MediaType = image.MediaType == "image/png" ? Beta.MediaType.ImagePng : Beta.MediaType.ImageJpeg }
                    : new Beta.BetaUrlImageSource { Url = image.Url! }
            });
        blocks.Add(new Beta.BetaTextBlockParam { Text = prompt });
        return blocks;
    }

    public async Task<byte[]> DownloadFileAsync(string apiKey, string fileId, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await Call(() => Client(apiKey, TimeSpan.FromMinutes(1)).Files.Download(fileId, cancellationToken: cancellationToken), cancellationToken);
        await using var stream = await response.ReadAsStream(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new ClaudeException("AI_FILE_TOO_LARGE", "Claude'un ürettiği dosya beklenenden büyük; eklenmedi.", 502);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static ClaudeUsage Usage(Usage? usage) => usage is null
        ? new(null, null, null, null)
        : new(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens);

    // The SDK's typed errors as one Turkish message per kind; the key never appears in it. A call the user abandoned
    // (the request's token) is AI_CANCELLED, not a timeout (D4).
    private static async Task<T> Call<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try { return await call(); }
        catch (AnthropicUnauthorizedException exception) { throw new ClaudeException("AI_KEY_INVALID", "Claude API anahtarı geçersiz ya da iptal edilmiş.", 400, inner: exception); }
        catch (AnthropicForbiddenException exception) { throw new ClaudeException("AI_KEY_FORBIDDEN", "Bu API anahtarının bu işleme izni yok.", 400, inner: exception); }
        catch (AnthropicNotFoundException exception) { throw new ClaudeException("AI_NOT_FOUND", "Claude istenen model, beceri ya da dosyayı bulamadı. Model adını ve beceriyi kontrol edin.", 502, inner: exception); }
        catch (AnthropicRateLimitException exception) { throw new ClaudeException("AI_RATE_LIMITED", "Claude API şu an çok fazla istek alıyor (hız sınırı). Biraz sonra yeniden deneyin.", 429, inner: exception); }
        catch (AnthropicBadRequestException exception) { throw new ClaudeException("AI_BAD_REQUEST", $"Claude isteği kabul etmedi: {Short(exception.Message)}", 502, inner: exception); }
        catch (Anthropic5xxException exception) { throw new ClaudeException("AI_UNAVAILABLE", "Claude API geçici olarak yanıt vermiyor. Biraz sonra yeniden deneyin.", 502, inner: exception); }
        catch (AnthropicApiException exception) { throw new ClaudeException("AI_API_ERROR", $"Claude API hatası: {Short(exception.Message)}", 502, inner: exception); }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested) { throw new ClaudeException(CancelledCode, "İstek iptal edildi; Claude çağrısı yarıda bırakıldı.", 400, inner: exception); }
        catch (TaskCanceledException exception) { throw new ClaudeException("AI_TIMEOUT", "Claude zamanında yanıt vermedi. Yeniden deneyin.", 504, inner: exception); }
        catch (HttpRequestException exception) { throw new ClaudeException("AI_CONNECTION_FAILED", "Claude API'ye bağlanılamadı.", 502, inner: exception); }
    }

    private static string Short(string value) => value.Length <= 240 ? value : value[..240] + "…";
}
