using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PazaramaQuestionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Row = """
        {"questionId":"56643287-ad2c-40be-9088-08dc076bf0d7","productName":"Tükenmez Kalem","productImageUrl":"https://img.pzrmcdn.com/asset/MSTK55550/images/a.jpeg",
         "barcode":"MSTK55550","brand":"4 Element","maskedUserName":"M**** S****","question":"garanti var mı ","questionDate":"2023-12-28T09:12:16.333",
         "answer":"Merhaba, evet.","answerDate":"2023-12-28T10:17:13.023","questionStatus":2,"topicId":"cd6a0496"}
        """;

    private static string Page(string rows, int index = 1, int totalPages = 1, int totalCount = 1) =>
        $$$"""{"data":{"sellerId":"s","approvalAnswersByMerchantSearchs":[{{{rows}}}],"pageResponse":{"pageIndex":{{{index}}},"pageSize":10,"totalCount":{{{totalCount}}},"totalPages":{{{totalPages}}}}},"success":true}""";

    // ---- states ----

    [Theory]
    [InlineData("WAITING_FOR_ANSWER", new[] { 0 })]
    [InlineData("ANSWERED", new[] { 1, 2 })]
    [InlineData("REJECTED", new[] { 3 })]
    [InlineData(null, new[] { 0, 1, 2, 3 })]
    [InlineData("EXPIRED", new[] { 0, 1, 2, 3 })]
    public void Panel_states_ask_for_the_matching_pazarama_states(string? panel, int[] expected) =>
        Assert.Equal(expected, PazaramaQuestionMapper.PazaramaStatuses(panel));

    [Theory]
    [InlineData(0, "WAITING_FOR_ANSWER")]
    [InlineData(1, "ANSWERED")]
    [InlineData(2, "ANSWERED")]
    [InlineData(3, "REJECTED")]
    [InlineData(null, "WAITING_FOR_ANSWER")]
    public void Pazarama_states_map_to_panel_states(int? status, string expected) => Assert.Equal(expected, PazaramaQuestionMapper.PanelStatus(status));

    // ---- list ----

    [Fact]
    public void A_listed_question_carries_product_customer_and_answer()
    {
        var page = PazaramaQuestionMapper.Page(Parse(Page(Row)), 1);
        var question = Assert.Single(page.Items);

        Assert.Equal("56643287-ad2c-40be-9088-08dc076bf0d7", question.Id);
        Assert.Equal("PRODUCT", question.Kind);
        Assert.Equal("ANSWERED", question.Status);
        Assert.Equal("garanti var mı", question.Text);
        Assert.Equal("Tükenmez Kalem", question.ProductName);
        Assert.Equal("MSTK55550", question.ProductBarcode);
        Assert.Equal("M**** S****", question.CustomerName);
        var answer = Assert.Single(question.Conversations);
        Assert.Equal("Merchant", answer.Author);
        Assert.Equal("Merhaba, evet.", answer.Text);
        Assert.Null(answer.RejectionReason);
    }

    [Fact]
    public void Dates_are_turkey_time_stored_as_utc()
    {
        var question = PazaramaQuestionMapper.Question(Parse(Row))!;

        // 09:12 in Turkey (UTC+3) is 06:12 UTC; PostgreSQL only accepts zero offsets.
        Assert.Equal(TimeSpan.Zero, question.CreatedAt.Offset);
        Assert.Equal(new DateTimeOffset(2023, 12, 28, 6, 12, 16, 333, TimeSpan.Zero), question.CreatedAt);
        Assert.Equal(new DateTimeOffset(2023, 12, 28, 7, 17, 13, 23, TimeSpan.Zero), question.LastModifiedAt);
    }

    [Fact]
    public void A_rejected_answer_says_so()
    {
        var row = Row.Replace("\"questionStatus\":2", "\"questionStatus\":3");

        var question = PazaramaQuestionMapper.Question(Parse(row))!;

        Assert.Equal("REJECTED", question.Status);
        Assert.Contains("reddedildi", question.Conversations.Single().RejectionReason);
    }

    [Fact]
    public void An_unanswered_question_has_no_conversation()
    {
        var row = """{"questionId":"q1","question":"kargo ne zaman","questionDate":"2025-08-25T18:45:42.773","questionStatus":0,"answer":null}""";

        var question = PazaramaQuestionMapper.Question(Parse(row))!;

        Assert.Equal("WAITING_FOR_ANSWER", question.Status);
        Assert.Empty(question.Conversations);
    }

    [Fact]
    public void Rows_without_an_id_or_text_are_skipped()
    {
        var page = PazaramaQuestionMapper.Page(Parse(Page($$"""{{Row}},{"question":"x"},{"questionId":"q2"}""", totalCount: 3)), 1);

        Assert.Single(page.Items);
        Assert.Equal(3, page.TotalElements);
    }

    [Fact]
    public void Paging_numbers_come_from_the_response()
    {
        var page = PazaramaQuestionMapper.Page(Parse(Page(Row, index: 2, totalPages: 5, totalCount: 42)), 2);

        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.TotalPages);
        Assert.Equal(42, page.TotalElements);
    }

    [Fact]
    public void An_empty_list_is_an_empty_page()
    {
        var page = PazaramaQuestionMapper.Page(Parse("""{"data":{"pageResponse":{"totalCount":0,"totalPages":0}}}"""), 1);

        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"approvalAnswersByMerchantSearchs":null,"unAnsweredCount":0}}""")]
    public void No_questions_can_be_answered_in_several_ways(string json)
    {
        var page = PazaramaQuestionMapper.Page(Parse(json), 1);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalElements);
    }

    [Fact]
    public void A_bare_list_of_questions_is_read()
    {
        var page = PazaramaQuestionMapper.Page(Parse($$"""{"data":[{{Row}}]}"""), 1);

        Assert.Single(page.Items);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"data":"x"}""")]
    [InlineData("""{"data":5}""")]
    public void Something_else_is_a_contract_error(string json) => Assert.Throws<JsonException>(() => PazaramaQuestionMapper.Page(Parse(json), 1));

    [Fact]
    public void The_shape_of_an_odd_answer_names_fields_only()
    {
        var shape = PazaramaHttpClient.ShapeOf(Parse("""{"data":{"secret":"value"},"success":true,"list":[1]}"""));

        Assert.Contains("data.secret", shape);
        Assert.Contains("list:Array", shape);
        Assert.DoesNotContain("value", shape);
    }

    // ---- detail ----

    [Fact]
    public void Detail_adds_the_order_number_and_topic()
    {
        var question = PazaramaQuestionMapper.Detail(Parse("""
            {"productName":"Pantolon","barcode":"8683525133020","questionId":"a82a1cf6-d113-4e73-0c48-08dde351ae8d","maskedUserName":"T**** G****",
             "question":"yarın kargoya verir misiniz","questionDate":"2025-08-25T18:45:42.773","questionStatus":0,"topicName":"Teslimat ve Kargo","orderNumber":567800436}
            """))!;

        Assert.Equal("Teslimat ve Kargo: yarın kargoya verir misiniz", question.Text);
        Assert.Equal("567800436", question.OrderNumber);
        Assert.Equal("WAITING_FOR_ANSWER", question.Status);
    }

    [Theory]
    [InlineData("\"orderNumber\":567800436", "ORDER")]
    [InlineData("\"orderNumber\":0", "PRODUCT")]
    [InlineData("\"orderNumber\":null", "PRODUCT")]
    [InlineData("\"other\":1", "PRODUCT")]
    public void A_question_that_names_an_order_is_an_order_question(string orderField, string kind)
    {
        var json = "{\"questionId\":\"a82a1cf6-d113-4e73-0c48-08dde351ae8d\",\"question\":\"nerede\",\"questionDate\":\"2025-08-25T18:45:42.773\",\"questionStatus\":0," + orderField + "}";

        Assert.Equal(kind, PazaramaQuestionMapper.Question(Parse(json))!.Kind);
        Assert.Equal(kind, PazaramaQuestionMapper.Detail(Parse(json))!.Kind);
    }

    [Fact]
    public void Detail_of_something_else_is_nothing() => Assert.Null(PazaramaQuestionMapper.Detail(Parse("[]")));

    // ---- request ----

    [Fact]
    public void The_search_body_uses_one_based_pages_and_turkey_dates()
    {
        var request = new QuestionPollRequest("PRODUCT", "ANSWERED", new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero), 0, 500);

        var body = JsonSerializer.Serialize(PazaramaQuestionMapper.SearchBody(request, 2));

        Assert.Contains("\"questionStatus\":2", body);
        Assert.Contains("\"pageIndex\":1", body);
        Assert.Contains("\"pageSize\":100", body);
        Assert.Contains("\"questionStartDate\":\"2026-10-02T00:30:00\"", body);
        Assert.Contains("\"questionEndDate\":\"2026-10-15T12:00:00\"", body);
        Assert.Contains("\"barcode\":null", body);
    }

    [Fact]
    public void No_dates_means_no_date_filter()
    {
        var body = JsonSerializer.Serialize(PazaramaQuestionMapper.SearchBody(new QuestionPollRequest("PRODUCT", null, null, null, 3, 20), 0));

        Assert.Contains("\"questionStartDate\":null", body);
        Assert.Contains("\"questionEndDate\":null", body);
        Assert.Contains("\"pageIndex\":3", body);
    }

    [Fact]
    public void The_services_are_the_documented_ones()
    {
        Assert.Equal("QuestionAnswer/getApprovalAnswersByMerchantSearch", PazaramaHttpClient.QuestionSearch);
        Assert.Equal("QuestionAnswer/getApprovalAnswerById", PazaramaHttpClient.QuestionDetail);
        Assert.Equal("QuestionAnswer/sellerAnswer", PazaramaHttpClient.QuestionAnswer);
    }
}
