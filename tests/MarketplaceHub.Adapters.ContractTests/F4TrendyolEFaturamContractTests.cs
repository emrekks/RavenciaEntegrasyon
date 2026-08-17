using System.Net;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamContractTests
{
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
        var error = TrendyolEFaturamErrorMapper.FromAuthorizedStatus(HttpStatusCode.Unauthorized, null, null);
        Assert.Equal("EFATURAM_ACCESS_TOKEN_REJECTED", error.Code);
    }

    [Fact]
    public void Multiple_direct_account_privilege_companies_remain_fail_closed() =>
        Assert.False(TrendyolEFaturamDirectAccountAccess.TryRead(Token("""{"sub":"20","privs":{"10":[],"11":[]}}"""), out _));

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
    }

    [Theory]
    [InlineData("/problem/connection-error#token-info-read-timed-out", "problem:/problem/connection-error#token-info-read-timed-out")]
    [InlineData("https://stage.example.test/problem/token-invalid?token=secret#ignored", "problem:/problem/token-invalid")]
    [InlineData("/problem/token invalid", null)]
    [InlineData("/other/not-a-provider-problem", null)]
    public void Provider_problem_reference_is_allowlisted_without_preserving_query_values(string value, string? expected) =>
        Assert.Equal(expected, TrendyolEFaturamProblemDetails.Normalize(value));

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
