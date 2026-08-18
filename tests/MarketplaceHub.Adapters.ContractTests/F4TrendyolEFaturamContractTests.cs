using System.Net;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamContractTests
{
    [Fact]
    public void Direct_account_stage_uses_the_provider_portal_bff_while_production_stays_on_the_gateway()
    {
        var options = new TrendyolEFaturamOptions();

        Assert.Equal("https://stage-apigateway.trendyolefaturam.com/", options.StageBaseAddress.AbsoluteUri);
        Assert.Equal("https://apigateway.trendyolecozum.com/", options.ProductionBaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("earchive-status-approved-anonymous.json", "ACCEPTED", true, "205")]
    [InlineData("earchive-status-cancelled-anonymous.json", "CANCELLED", true, "305")]
    [InlineData("earchive-status-rejected-anonymous.json", "REJECTED", true, "105")]
    public void Official_earchive_status_codes_are_mapped_without_text_guessing(string fixture, string canonical, bool terminal, string raw)
    {
        var result = TrendyolEFaturamJsonMapper.InvoiceStatus(Fixture(fixture), "local-reference");
        Assert.Equal(canonical, result.CanonicalStatus);
        Assert.Equal(terminal, result.IsTerminal);
        Assert.Equal(raw, result.RawStatus);
        Assert.Equal("RVN2026000000001", result.InvoiceNumber);
    }

    [Theory]
    [InlineData(10, "PENDING", false)]
    [InlineData(20, "PENDING", false)]
    [InlineData(29, "REJECTED", true)]
    [InlineData(30, "PENDING", false)]
    [InlineData(40, "PENDING", false)]
    [InlineData(50, "PENDING", false)]
    [InlineData(100, "PENDING", false)]
    [InlineData(105, "REJECTED", true)]
    [InlineData(200, "PENDING", false)]
    [InlineData(205, "ACCEPTED", true)]
    [InlineData(305, "CANCELLED", true)]
    [InlineData(405, "REJECTED", true)]
    [InlineData(999, "MANUAL_REVIEW", true)]
    public void Official_status_catalog_is_fail_closed(int status, string canonical, bool terminal)
    {
        var result = TrendyolEFaturamStatusCatalog.Classify(status);
        Assert.Equal(canonical, result.CanonicalStatus);
        Assert.Equal(terminal, result.IsTerminal);
    }

    [Fact]
    public void Fiscal_scope_is_read_from_direct_account_token_instead_of_user_settings()
    {
        Assert.True(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"companyId":10,"userId":20}"""), out var access));
        Assert.Equal(10, access.CompanyId);
        Assert.Equal(20, access.UserId);
    }

    [Fact]
    public void Direct_account_access_reads_safe_token_lifetime_metadata()
    {
        Assert.True(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"companyId":10,"userId":20,"iat":1724000000,"nbf":1724000001,"exp":1724003600,"iss":"stage-issuer","aud":["invoice-api"]}"""), out var access));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1724000000), access.IssuedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1724000001), access.NotBefore);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1724003600), access.ExpiresAt);
        Assert.Equal("stage-issuer", access.Issuer);
        Assert.Equal("invoice-api", access.Audience);
    }

    [Fact]
    public void Fiscal_scope_is_read_from_direct_account_privileges_and_subject()
    {
        Assert.True(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"sub":"20","privs":{"10":["INVOICE_CREATE"]}}"""), out var access));
        Assert.Equal(10, access.CompanyId);
        Assert.Equal(20, access.UserId);
    }

    [Fact]
    public void Direct_account_privilege_list_does_not_override_authorized_endpoint_response()
    {
        Assert.True(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"sub":"20","privs":{"10":["INVOICE_READ"]}}"""), out var access));
        Assert.False(access.HasInvoiceCreatePrivilege);
        Assert.True(access.HasInvoiceReadPrivilege);
        var error = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.Unauthorized, null, null);
        Assert.Equal("EFATURAM_ACCESS_TOKEN_REJECTED", error.Code);
    }

    [Fact]
    public void Multiple_direct_account_privilege_companies_remain_fail_closed() =>
        Assert.False(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"sub":"20","privs":{"10":[],"11":[]}}"""), out _));

    [Fact]
    public void Connection_test_validates_direct_sign_in_and_read_only_protected_invoice_api_access()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "TrendyolEFaturam", "TrendyolEFaturamHttpClient.cs"));
        var connectionTest = source[..source.IndexOf("    public async Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync", StringComparison.Ordinal)];

        Assert.DoesNotContain("TrendyolEFaturamEndpoints.PermanentDocumentUrl", connectionTest, StringComparison.Ordinal);
        Assert.Contains("AcquireAccess(configured", connectionTest, StringComparison.Ordinal);
        Assert.Contains("HttpMethod.Get", connectionTest, StringComparison.Ordinal);
        Assert.Contains("TrendyolEFaturamEndpoints.EArchiveStatus(ConnectionProbeInvoiceUuid)", connectionTest, StringComparison.Ordinal);
        Assert.Contains("HttpStatus is not (404 or 409)", connectionTest, StringComparison.Ordinal);
        Assert.Contains("new AuthenticationHeaderValue(\"Bearer\", token)", source, StringComparison.Ordinal);
        Assert.Contains("TryAddWithoutValidation(\"x-access-token\", token)", source, StringComparison.Ordinal);
        Assert.Contains("access.Value.UserId, null, \"PORTAL\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("access.Value.UserId, null, \"PARTNER\"", source, StringComparison.Ordinal);
        Assert.Contains("new ByteArrayContent(Encoding.UTF8.GetBytes(officialPayload))", source, StringComparison.Ordinal);
        Assert.Contains("payload.Headers.ContentType = new MediaTypeHeaderValue(\"application/json\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonContent.Create(payload.RootElement)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"companyId\":10}")]
    [InlineData("{\"companyId\":10,\"userId\":\"invalid\"}")]
    public void Direct_account_token_without_fiscal_scope_does_not_create_a_fiscal_scope(string payload) =>
        Assert.False(TrendyolEFaturamDirectAccountAccess.TryRead(Token(payload), out _));

    [Fact]
    public void Invalid_direct_account_token_does_not_create_a_fiscal_scope() =>
        Assert.False(TrendyolEFaturamDirectAccountAccess.TryRead("not-a-jwt", out _));

    [Fact]
    public void Fresh_authorized_endpoint_unauthorized_is_not_reported_as_sign_in_failure()
    {
        var error = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.Unauthorized, null, "safe-request-id");
        Assert.Equal("EFATURAM_ACCESS_TOKEN_REJECTED", error.Code);
        Assert.Equal(AdapterErrorClass.Authentication, error.Class);
        Assert.Equal(401, error.HttpStatus);
        Assert.Contains("girişi başarılı", error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("taze JWT", error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/problem/connection-error#token-info-read-timed-out", "problem:/problem/connection-error#token-info-read-timed-out")]
    [InlineData("https://stage.example.test/problem/token-invalid?token=secret#ignored", "problem:/problem/token-invalid")]
    [InlineData("/problem/token invalid", null)]
    [InlineData("/other/not-a-provider-problem", null)]
    public void Provider_problem_reference_is_allowlisted_without_preserving_query_values(string value, string? expected) =>
        Assert.Equal(expected, TrendyolEFaturamProblemDetails.Normalize(value));

    [Fact]
    public async Task Provider_validation_field_and_code_are_preserved_without_response_body()
    {
        using var content = new StringContent("""{"errors":[{"field":"invoiceLines[0].unitPriceAmount","code":"AmountMismatch","defaultMessage":"sensitive value"}]}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);

        Assert.Equal("validation:invoiceLines.0.unitPriceAmount:AmountMismatch", reference);
        Assert.DoesNotContain("sensitive", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_problem_type_is_preserved_when_instance_is_absent()
    {
        using var content = new StringContent("""{"type":"/problem/ubl-validation-failed","title":"Invoice UBL Build Failed","detail":"customer content"}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);

        Assert.Equal("problem:/problem/ubl-validation-failed", reference);
    }

    [Fact]
    public async Task Official_application_mismatch_type_is_preserved_without_sender_tax_id_detail()
    {
        using var content = new StringContent("""{"type":"https://api.trendyol.com/etransformation/gateway/application-mismatch","title":"Conflict","detail":"Application detail status not suitable for invoice operation for tax id 1234567890"}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);
        var error = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.Conflict, null, reference);

        Assert.Equal("problem:/etransformation/gateway/application-mismatch", reference);
        Assert.Equal("EFATURAM_APPLICATION_NOT_ACTIVE", error.Code);
        Assert.Contains("aktif görmüyor", error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_application_mismatch_detail_is_mapped_without_persisting_sender_tax_id()
    {
        using var content = new StringContent("""{"title":"Conflict","status":409,"detail":"Application detail status not suitable for invoice operation for tax id 12345678901"}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);
        var error = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.Conflict, null, reference);

        Assert.Equal("problem:/etransformation/gateway/application-mismatch", reference);
        Assert.Equal("EFATURAM_APPLICATION_NOT_ACTIVE", error.Code);
        Assert.DoesNotContain("12345678901", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_problem_dictionary_preserves_only_the_field_path()
    {
        using var content = new StringContent("""{"title":"Bad Request","errors":{"InvoiceLines[0].UnitPriceAmount":["customer-sensitive message"]}}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);

        Assert.Equal("validation:InvoiceLines.0.UnitPriceAmount:rejected", reference);
        Assert.DoesNotContain("sensitive", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_problem_detail_is_preserved_when_it_is_safe_and_actionable()
    {
        using var content = new StringContent("""{"title":"Bad Request","detail":"invoiceLines[0].unitPriceAmount must be greater than 0"}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);

        Assert.Equal("provider-detail:invoiceLines[0].unitPriceAmount must be greater than 0", reference);
    }

    [Fact]
    public async Task Provider_problem_detail_with_email_is_not_persisted()
    {
        using var content = new StringContent("""{"title":"Bad Request","detail":"customer@example.test is invalid"}""");

        var reference = await TrendyolEFaturamProblemDetails.TryReadReferenceAsync(content, CancellationToken.None);

        Assert.Equal("provider-title:bad-request", reference);
    }

    [Theory]
    [InlineData("TEXMP", "8590921777")]
    [InlineData("Trendyol Express", "8590921777")]
    [InlineData("Yurtiçi Kargo", "3130557669")]
    [InlineData("PTT Kargo Marketplace", "7320068060")]
    public void Official_Trendyol_carrier_aliases_resolve_without_user_mapping(string provider, string expectedTaxId)
    {
        Assert.True(TrendyolCarrierCatalog.TryResolve(provider, out var carrier));
        Assert.Equal(expectedTaxId, carrier.TaxId);
    }

    [Fact]
    public void Unknown_carrier_is_not_invented() => Assert.False(TrendyolCarrierCatalog.TryResolve("UNMAPPED-CARRIER", out _));

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("latest", false)]
    [InlineData("1.0", false)]
    public void Api_version_is_exactly_pinned(string value, bool expected) => Assert.Equal(expected, TrendyolEFaturamContractGuard.IsPinnedApiVersion(value));

    [Fact]
    public void Tax_id_format_validation_remains_strict_for_invoice_recipients()
    {
        Assert.True(TrendyolEFaturamContractGuard.IsTaxIdFormat("1234567890"));
        Assert.False(TrendyolEFaturamContractGuard.IsTaxIdFormat("123456789"));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "TrendyolEFaturam", "Fixtures", name));
    private static string Token(string payload) => $"header.{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
