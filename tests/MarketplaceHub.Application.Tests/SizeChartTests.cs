using System.Net;
using System.Security.Claims;
using System.Text;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.SizeCharts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SizeChartRulesTests
{
    private static SizeChartInput Input(string?[] columns, params (string Size, decimal?[] Values)[] rows) =>
        new(columns, rows.Select(row => new SizeChartRowInput(row.Size, row.Values)).ToList());

    [Fact]
    public void Valid_measurements_are_kept_exactly_with_a_turkish_decimal_comma()
    {
        var (table, error) = SizeChartRules.Validate(Input(["Boy", "En"], ("S", [64m, 50m]), ("M", [66.5m, 53.0m])));
        Assert.Null(error);
        Assert.Equal(["Boy", "En"], table!.Columns);
        Assert.Equal(["64", "50"], table.Rows[0].Values);
        Assert.Equal(["66,5", "53"], table.Rows[1].Values);
    }

    [Theory]
    [InlineData(null, "boş")]
    [InlineData(0d, "0'dan büyük")]
    [InlineData(-3d, "0'dan büyük")]
    [InlineData(501d, "en fazla 500")]
    [InlineData(64.25, "bir ondalık")]
    public void A_bad_cell_is_refused_never_rounded(double? value, string expected)
    {
        var (table, error) = SizeChartRules.Validate(Input(["Boy"], ("S", [value is null ? null : (decimal)value.Value])));
        Assert.Null(table);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Column_names_must_be_present_unique_and_short_and_at_most_eight()
    {
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", " "], ("S", [1m, 2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", "BOY"], ("S", [1m, 2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input([new string('x', 25)], ("S", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(Enumerable.Range(1, 9).Select(i => (string?)$"K{i}").ToArray(), ("S", Enumerable.Repeat((decimal?)1m, 9).ToArray()))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input([], ("S", []))).Error);
    }

    [Fact]
    public void Every_row_needs_a_unique_size_and_one_value_per_column()
    {
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy", "En"], ("S", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy"], ("S", [1m]), ("s", [2m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(Input(["Boy"], ("", [1m]))).Error);
        Assert.NotNull(SizeChartRules.Validate(new SizeChartInput(["Boy"], [])).Error);
    }

    [Theory]
    [InlineData("Beden", true)]
    [InlineData("BEDEN", true)]
    [InlineData(" beden ", true)]
    [InlineData("Renk", false)]
    [InlineData(null, false)]
    public void The_size_option_is_the_one_named_beden(string? label, bool expected) => Assert.Equal(expected, SizeChartRules.IsSizeOption(label));

    [Fact]
    public void The_drawn_values_must_echo_the_request_exactly()
    {
        var (table, _) = SizeChartRules.Validate(Input(["Boy", "En"], ("S", [64m, 50m]), ("M", [66.5m, 53m])));
        var same = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"S","values":["64","50"]},{"size":"M","values":["66,5","53"]}]}""";
        var changed = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"S","values":["64","50"]},{"size":"M","values":["66","53"]}]}""";
        var reordered = """{"unit":"cm","columns":["Boy","En"],"rows":[{"size":"M","values":["66,5","53"]},{"size":"S","values":["64","50"]}]}""";
        Assert.True(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(same), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(changed), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes(reordered), table!));
        Assert.False(SizeChartService.EchoMatches(Encoding.UTF8.GetBytes("not json"), table!));
    }

    [Fact]
    public void Only_a_complete_jpeg_reports_its_size()
    {
        var jpeg = Jpeg(1200, 1800);
        Assert.Equal((1200, 1800), SizeChartService.JpegSize(jpeg));
        Assert.Null(SizeChartService.JpegSize(jpeg[..^2]));
        Assert.Null(SizeChartService.JpegSize([0x89, 0x50, 0x4E, 0x47]));
        Assert.Equal((800, 600), SizeChartService.JpegSize(Jpeg(800, 600)));
    }

    internal static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x12, 0x34,
        0xFF, 0xD9
    ];
}

// The size chart is always the last picture: in the strip and variant lists the panel reads, and in what goes out.
public sealed class SizeChartPlacementTests
{
    private const string A = "/api/v1/files/product-media/00000000-0000-0000-0000-00000000000a/content";
    private const string B = "/api/v1/files/product-media/00000000-0000-0000-0000-00000000000b/content";
    private const string Chart = "/api/v1/files/product-media/00000000-0000-0000-0000-0000000000cc/content";
    private const string NewChart = "/api/v1/files/product-media/00000000-0000-0000-0000-0000000000dd/content";

    [Fact]
    public void An_upload_saved_after_the_chart_still_comes_before_it() =>
        Assert.Equal([A, B, Chart], SizeChartPlacement.Last([A, Chart, B], Chart));

    [Fact]
    public void A_reorder_that_put_the_chart_first_leaves_it_last() =>
        Assert.Equal([A, B, Chart], SizeChartPlacement.Last([Chart, A, B], Chart));

    [Fact]
    public void A_new_chart_replaces_the_old_one_which_is_then_an_ordinary_picture_or_gone()
    {
        // The product points at the new chart; the old one, if still on the card, is an ordinary picture.
        Assert.Equal([Chart, A, NewChart], SizeChartPlacement.Last([NewChart, Chart, A], NewChart));
        Assert.Equal([A, B], SizeChartPlacement.Last([A, B], Chart));
        Assert.Equal([A, B], SizeChartPlacement.Last([A, B], null));
    }

    private sealed record Item(Guid? VariantId, Guid Id);
    private static readonly Guid V1 = Guid.NewGuid(), V2 = Guid.NewGuid();
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Every_variant_publishes_the_chart_last_even_when_its_own_pictures_lack_it_or_put_it_first()
    {
        // Card strip saved as C (chart), P1, P2; V1 has its own pictures including the chart; V2 has its own without it.
        Item[] media = [new(null, C), new(null, P1), new(null, P2), new(V1, C), new(V1, P2), new(V2, P1)];
        Assert.Equal([P2, C], PublicationVariantMedia.For(media, V1, x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        Assert.Equal([P1, C], PublicationVariantMedia.For(media, V2, x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        Assert.Equal([P1, P2, C], PublicationVariantMedia.For(media, Guid.NewGuid(), x => x.VariantId, x => x.Id, C).Select(x => x.Id));
        // Without a marked chart the saved order stands.
        Assert.Equal([C, P2], PublicationVariantMedia.For(media, V1, x => x.VariantId, x => x.Id).Select(x => x.Id));
    }

    [Fact]
    public void When_a_marketplace_takes_fewer_pictures_the_chart_is_still_sent_last()
    {
        string[] urls = ["1", "2", "3", "4", "5", "6", "chart"];
        Assert.Equal(["1", "2", "3", "4", "chart"], SizeChartPlacement.TakeKeepingLast(urls, 5, lastIsSizeChart: true));
        Assert.Equal(["1", "2", "3", "4", "5"], SizeChartPlacement.TakeKeepingLast(urls, 5, lastIsSizeChart: false));
        Assert.Equal(["1", "chart"], SizeChartPlacement.TakeKeepingLast(["1", "chart"], 5, lastIsSizeChart: true));
    }

    // Ş2: the chart is found by its url in the list that is trimmed, after blanks and duplicates are gone.
    [Fact]
    public void The_chart_is_told_by_its_url_after_blanks_and_duplicates_are_dropped()
    {
        const string chart = "https://cdn.example.com/chart.png";
        // The chart's own row could not be built (null): the last url is a product picture and must not take its place.
        string?[] withoutChartUrl = ["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg", "https://a/6.jpg", null];
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg"],
            ProductPublicationComposer.SelectPublicationImageUrls(withoutChartUrl, null, limit: 5));
        // The chart repeated earlier in the list (a duplicate the Distinct keeps first) still goes last and stays.
        string?[] chartTwice = ["https://a/1.jpg", chart, "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", "https://a/5.jpg", "", chart];
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg", "https://a/3.jpg", "https://a/4.jpg", chart],
            ProductPublicationComposer.SelectPublicationImageUrls(chartTwice, chart, limit: 5));
        // A chart url missing from the list changes nothing.
        Assert.Equal(["https://a/1.jpg", "https://a/2.jpg"], ProductPublicationComposer.SelectPublicationImageUrls(["https://a/1.jpg", "https://a/2.jpg"], chart, limit: 5));
    }

    [Fact]
    public void A_family_keeps_every_cards_chart_last_once_when_trimmed()
    {
        string[] urls = ["a1", "chartA", "a2", "b1", "chartB", "b2"];
        Assert.Equal(["a1", "a2", "chartA", "chartB"], SizeChartPlacement.TakeKeepingCharts(urls, 4, ["chartA", "chartB"]));
        Assert.Equal(["a1", "a2", "b1", "b2", "chartA", "chartB"], SizeChartPlacement.TakeKeepingCharts(urls, 250, ["chartA", "chartB"]));
        Assert.Equal(["a1", "chartA", "a2"], SizeChartPlacement.TakeKeepingCharts(urls, 3, []));
    }
}

// Ayarlar › Yapay Zekâ: only the owner and administrators reach it; the size chart is product work.
public sealed class AiSettingsRoleTests
{
    [Theory]
    [InlineData("GET", "/api/v1/settings/ai", "OPERATIONS", false)]
    [InlineData("GET", "/api/v1/settings/ai", "READONLY", false)]
    [InlineData("GET", "/api/v1/settings/ai/usage", "ACCOUNTING", false)]
    [InlineData("PUT", "/api/v1/settings/ai", "OPERATIONS", false)]
    [InlineData("POST", "/api/v1/settings/ai/test", "OPERATIONS", false)]
    [InlineData("POST", "/api/v1/settings/ai/skills/beden-tablosu/upload", "OPERATIONS", false)]
    [InlineData("GET", "/api/v1/settings/ai", "OWNER", true)]
    [InlineData("PUT", "/api/v1/settings/ai", "ADMINISTRATOR", true)]
    [InlineData("POST", "/api/v1/settings/ai/skills/beden-tablosu/upload", "OWNER", true)]
    [InlineData("POST", "/api/v1/products/11111111-1111-1111-1111-111111111111/size-chart/generate", "OPERATIONS", true)]
    [InlineData("POST", "/api/v1/products/11111111-1111-1111-1111-111111111111/size-chart/generate", "READONLY", false)]
    public async Task Roles(string method, string path, string role, bool allowed)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test")) };
        context.Request.Method = method;
        context.Request.Path = path;
        var called = false;
        await new RoleAuthorizationMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.Equal(allowed, called);
    }

    [Fact]
    public void The_size_chart_skill_is_embedded_with_its_files()
    {
        var skill = AiSkillCatalog.Find(AiSkillCatalog.SizeChartKey);
        Assert.NotNull(skill);
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/SKILL.md");
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/scripts/draw_size_chart.py");
        Assert.Contains(skill.Files, file => file.Path == "beden-tablosu/assets/ravencia-symbol.png");
        Assert.Equal(12, skill.BundleHash.Length);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop", true)]
    [InlineData("sk-ant-api03 abcdefghijklmnop", false)]
    [InlineData("sk-proj-abcdefghijklmnopqrstu", false)]
    [InlineData("sk-ant-x", false)]
    public void Only_a_claude_key_shape_is_accepted(string key, bool expected) => Assert.Equal(expected, AiSettingsService.IsApiKeyShaped(key));

    [Fact]
    public void Cost_uses_the_model_list_price_and_is_unknown_for_other_models()
    {
        Assert.Equal(0.005m, ClaudePricing.CostUsd("claude-sonnet-5-5", new ClaudeUsage(1000, 300, null, null)));
        Assert.Null(ClaudePricing.CostUsd("some-other-model", new ClaudeUsage(1000, 300, null, null)));
        Assert.Null(ClaudePricing.CostUsd("claude-sonnet-5-5", new ClaudeUsage(null, null, null, null)));
    }
}

// The SDK client against a fake Anthropic API: nothing reaches the real one.
public sealed class ClaudeClientTests
{
    private const string Key = "sk-ant-api03-test-key-never-shown";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? ApiKey, string? Beta, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : null, body));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string SkillResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[
          {"type":"server_tool_use","id":"srvtoolu_1","name":"bash_code_execution","input":{"command":"python scripts/draw_size_chart.py --input chart.json"}},
          {"type":"bash_code_execution_tool_result","tool_use_id":"srvtoolu_1","content":{"type":"bash_code_execution_result","stdout":"OK","stderr":"","return_code":0,
            "content":[{"type":"bash_code_execution_output","file_id":"file_img"},{"type":"bash_code_execution_output","file_id":"file_echo"}]}},
          {"type":"text","text":"OK"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":1500,"output_tokens":400,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}
        """;

    private static string Metadata(string id, string name) =>
        $$"""{"id":"{{id}}","type":"file","filename":"{{name}}","mime_type":"application/octet-stream","size_bytes":10,"created_at":"2026-11-06T10:00:00Z","downloadable":true}""";

    [Fact]
    public async Task A_skill_run_returns_the_files_the_container_wrote_with_their_names_and_the_usage()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/messages" => Json(SkillResponse),
            "/v1/files/file_img" => Json(Metadata("file_img", "beden-tablosu.jpg")),
            "/v1/files/file_echo" => Json(Metadata("file_echo", "beden-tablosu.json")),
            _ => Json("""{"type":"error","error":{"type":"not_found_error","message":"no"}}""", HttpStatusCode.NotFound)
        });
        var client = new ClaudeClient(new Factory(handler));

        var result = await client.RunSkillAsync(new ClaudeSkillRequest(Key, "claude-sonnet-5-5", "skill_1", "v1", "system", "prompt", "low", 8000, TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(["beden-tablosu.jpg", "beden-tablosu.json"], result.Files.Select(file => file.FileName));
        Assert.Equal(1500, result.Usage.InputTokens);
        Assert.Equal(400, result.Usage.OutputTokens);
        var call = handler.Requests.First(request => request.Path == "/v1/messages");
        Assert.Equal(Key, call.ApiKey);
        Assert.Contains(ClaudeClient.CodeExecutionBeta, call.Beta, StringComparison.Ordinal);
        Assert.Contains("\"skill_id\":\"skill_1\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"custom\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("claude-sonnet-5-5", call.Body, StringComparison.Ordinal);
        Assert.Contains("code_execution", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_becomes_a_turkish_message_without_the_key()
    {
        var handler = new FakeHandler(_ => Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));
        var client = new ClaudeClient(new Factory(handler));

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.TestAsync(Key, "claude-sonnet-5-5", CancellationToken.None));

        Assert.Equal("AI_KEY_INVALID", failure.Code);
        Assert.DoesNotContain(Key, failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    // D4: the message is billed before the file lookups; a failed lookup keeps its usage for the cost log.
    [Fact]
    public async Task A_failed_file_lookup_after_the_billed_message_keeps_the_usage()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/v1/messages"
            ? Json(SkillResponse)
            : Json("""{"type":"error","error":{"type":"api_error","message":"boom"}}""", HttpStatusCode.InternalServerError));
        var client = new ClaudeClient(new Factory(handler));

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.RunSkillAsync(new ClaudeSkillRequest(Key, "claude-sonnet-5-5", "skill_1", "v1", "system", "prompt", "low", 8000, TimeSpan.FromSeconds(30)), CancellationToken.None));

        Assert.Equal("AI_UNAVAILABLE", failure.Code);
        Assert.Equal((1500L, 400L), (failure.Usage?.InputTokens, failure.Usage?.OutputTokens));
    }

    [Fact]
    public async Task A_call_the_user_abandoned_is_cancelled_not_a_timeout()
    {
        var handler = new FakeHandler(_ => Json(SkillResponse));
        var client = new ClaudeClient(new Factory(handler));
        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.TestAsync(Key, "claude-sonnet-5-5", abandoned.Token));

        Assert.Equal(ClaudeClient.CancelledCode, failure.Code);
    }

    [Fact]
    public async Task A_download_larger_than_allowed_is_refused()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var client = new ClaudeClient(new Factory(handler));

        Assert.Equal(2048, (await client.DownloadFileAsync(Key, "file_img", 4096, CancellationToken.None)).Length);
        var failure = await Assert.ThrowsAsync<ClaudeException>(() => client.DownloadFileAsync(Key, "file_img", 1024, CancellationToken.None));
        Assert.Equal("AI_FILE_TOO_LARGE", failure.Code);
    }
}
