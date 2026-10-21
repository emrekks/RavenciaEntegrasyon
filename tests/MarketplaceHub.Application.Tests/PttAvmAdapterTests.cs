using System.Net;
using System.Text;
using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PttAvmAdapterTests
{
    private const string Username = "ravencia-api-user";
    private const string Password = "S3cr3t&<pass>";
    private const string StoreId = "123456";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 22, 30, 0, TimeSpan.Zero);

    // Answers as published on developers.pttavm.com (Supplier Information, Order Control V2 SOAP samples),
    // shortened: the order sample keeps one order with one line.
    private const string SupplierAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:u="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
           <s:Header>
              <ActivityId CorrelationId="218e076b-b502-4487-9815-ef775a4d4eca" xmlns="http://schemas.microsoft.com/2004/09/ServiceModel/Diagnostics">00000000-0000-0000-0000-000000000000</ActivityId>
              <o:Security s:mustUnderstand="1" xmlns:o="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
                 <u:Timestamp u:Id="_0"><u:Created>2025-04-29T11:58:47.277Z</u:Created><u:Expires>2025-04-29T12:03:47.277Z</u:Expires></u:Timestamp>
              </o:Security>
           </s:Header>
           <s:Body>
              <KullaniciTedarikciBilgisiGetirResponse xmlns="http://tempuri.org/">
                 <KullaniciTedarikciBilgisiGetirResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                    <a:TedarikciAdi>abcdef</a:TedarikciAdi>
                    <a:TedarikciId>123456</a:TedarikciId>
                 </KullaniciTedarikciBilgisiGetirResult>
              </KullaniciTedarikciBilgisiGetirResponse>
           </s:Body>
        </s:Envelope>
        """;

    private const string OrdersAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
           <s:Body>
              <SiparisKontrolListesiV2Response xmlns="http://tempuri.org/">
                 <SiparisKontrolListesiV2Result xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                    <a:TedarikciSiparisKontrolV2>
                       <a:Barcodes xmlns:b="http://schemas.datacontract.org/2004/07/ePttAVMService.Model"/>
                       <a:FirmaUnvani/>
                       <a:IslemTarihi>2024-10-21T14:48:46</a:IslemTarihi>
                       <a:KargoBarkod/>
                       <a:KargoTutari>222.3</a:KargoTutari>
                       <a:SiparisNo>PTT-L6LI1VK2D-211024</a:SiparisNo>
                       <a:SiparisUrunler>
                          <a:SiparisUrun>
                             <a:KdvDahilToplamTutar>49.66</a:KdvDahilToplamTutar>
                             <a:LineItemId>232318670</a:LineItemId>
                             <a:SiparisDurumu>iptal</a:SiparisDurumu>
                             <a:UrunBarkod>10102024-85224</a:UrunBarkod>
                          </a:SiparisUrun>
                       </a:SiparisUrunler>
                       <a:VergiNo/>
                    </a:TedarikciSiparisKontrolV2>
                 </SiparisKontrolListesiV2Result>
              </SiparisKontrolListesiV2Response>
           </s:Body>
        </s:Envelope>
        """;

    // Minimal answers built from the WSDL types (categoryMainResponse, ArrayOfStokKontrolDetay); not copied from PTT AVM.
    private const string CategoriesAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><GetMainCategoriesResponse xmlns="http://tempuri.org/"><GetMainCategoriesResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><a:error i:nil="true"/><a:main_category><a:category><a:id>1</a:id><a:name>Elektronik</a:name><a:updated_at>2023-06-13T15:24:53</a:updated_at></a:category></a:main_category><a:success>true</a:success></GetMainCategoriesResult></GetMainCategoriesResponse></s:Body></s:Envelope>
        """;

    private const string StockAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><StokKontrolListesiResponse xmlns="http://tempuri.org/"><StokKontrolListesiResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"/></StokKontrolListesiResponse></s:Body></s:Envelope>
        """;

    // The fault WCF gives for a wrong user name or password (and for a malformed request).
    private const string SecurityFault = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode xmlns:a="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">a:InvalidSecurity</faultcode><faultstring xml:lang="en-US">An error occurred when verifying security for the message.</faultstring></s:Fault></s:Body></s:Envelope>
        """;

    [Fact]
    public void PttAvmIsAMarketplaceWithItsOwnConnectionTest()
    {
        Assert.True(ActiveIntegrationScope.Contains("PTTAVM"));
        Assert.True(ActiveIntegrationScope.IsMarketplace("PTTAVM"));
        Assert.Equal(MarketplaceJobTypes.PttAvmConnectionTest, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.ConnectionTest));
        Assert.Equal("PTTAVM_CONNECTION_TEST", MarketplaceJobTypes.PttAvmConnectionTest);
        Assert.Contains(MarketplaceJobTypes.PttAvmConnectionTest, MarketplaceJobTypes.ProcessorJobTypes);
        Assert.Equal("developers.pttavm.com", CapabilityEvidencePolicy.OfficialDocumentationHost("PTTAVM"));
        Assert.Equal("PTT AVM", JobOperationsService.Marketplace("PTTAVM_CONNECTION_TEST"));
        Assert.Equal("PTT AVM", JobOperationsService.Marketplace("PRICE_INVENTORY_SYNC", "PTTAVM"));
        Assert.Contains("ORDERS", ConnectionDataResetPolicy.ScopesFor("PTTAVM"));
        // Invoice creation starts off on a new connection.
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("PTTAVM", """{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}"""));
    }

    [Fact]
    public void PttAvmConnectionsCarryTheirReadAndPriceStockCapabilities() =>
        Assert.Equal(
            [MarketplaceCapabilities.ConnectionTest, MarketplaceCapabilities.OrderRead, MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.ReferenceRead, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite],
            MarketplaceConnectionService.PttAvmCapabilityCodes);

    [Fact]
    public void PttAvmConnectionsRouteToThePttAvmAdapter()
    {
        object trendyol = new(), shopify = new(), pttAvm = new();

        Assert.Same(pttAvm, MarketplacePortRouter.SelectPort("pttavm", trendyol, shopify, pttAvm: pttAvm));
        Assert.Throws<InvalidOperationException>(() => MarketplacePortRouter.SelectPort("PTTAVM", trendyol, shopify));
    }

    [Fact]
    public async Task TheConnectionTestSignsASupplierInfoCallWithAUsernameTokenAndATimestamp()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, SupplierAnswer));

        var result = await Client(handler).TestAsync(Account(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("PTTAVM", result.Value!.PlatformCode);
        Assert.Equal(StoreId, result.Value.ScopeFingerprint);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ws.pttavm.com:93/service.svc", request.Uri);
        Assert.Equal("\"http://tempuri.org/IService/KullaniciTedarikciBilgisiGetir\"", request.Headers["SOAPAction"]);
        Assert.StartsWith("text/xml", request.ContentType);

        var envelope = XDocument.Parse(request.Body!);
        XNamespace soap = PttAvmHttpClient.EnvelopeNamespace, tem = PttAvmHttpClient.ServiceNamespace, wsse = PttAvmHttpClient.SecurityNamespace, wsu = PttAvmHttpClient.UtilityNamespace;
        Assert.Equal("soapenv", envelope.Root!.GetPrefixOfNamespace(soap));
        Assert.Equal("tem", envelope.Root.GetPrefixOfNamespace(tem));
        var security = envelope.Root.Element(soap + "Header")!.Element(wsse + "Security")!;
        Assert.Equal("1", security.Attribute(soap + "mustUnderstand")!.Value);
        var timestamp = security.Element(wsu + "Timestamp")!;
        Assert.Equal("2026-10-21T22:30:00.000Z", timestamp.Element(wsu + "Created")!.Value);
        Assert.Equal("2026-10-21T22:35:00.000Z", timestamp.Element(wsu + "Expires")!.Value);
        var token = security.Element(wsse + "UsernameToken")!;
        Assert.Equal(Username, token.Element(wsse + "Username")!.Value);
        var password = token.Element(wsse + "Password")!;
        // The password is escaped in the XML and arrives as typed.
        Assert.Equal(Password, password.Value);
        Assert.EndsWith("#PasswordText", password.Attribute("Type")!.Value);
        Assert.NotNull(envelope.Root.Element(soap + "Body")!.Element(tem + "KullaniciTedarikciBilgisiGetir"));
    }

    [Fact]
    public async Task AnApiUserOfAnotherStoreIsNotAcceptedForThisStore()
    {
        var client = Client(new CapturingHandler(_ => Respond(HttpStatusCode.OK, SupplierAnswer)));

        var result = await client.TestAsync(Account(storeId: "654321"), CancellationToken.None);

        Assert.Equal("PTTAVM_STORE_MISMATCH", result.Error!.Code);
        Assert.Contains("123456", result.Error.SafeMessage);
        Assert.Contains("654321", result.Error.SafeMessage);
    }

    [Fact]
    public async Task AStoreNameTypedAsTheStoreIdGivesWayToTheApiUsersStore()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, SupplierAnswer));

        var result = await Client(handler).TestAsync(Account(storeId: "Ravencia"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(StoreId, result.Value!.ExternalStoreId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ASecurityFaultIsReportedAsRejectedCredentialsWithoutLeakingThem()
    {
        var logger = new CapturingLogger();
        var client = Client(new CapturingHandler(_ => Respond(HttpStatusCode.InternalServerError, SecurityFault)), logger: logger);

        var result = await client.TestAsync(Account(), CancellationToken.None);

        Assert.Equal("PTTAVM_CREDENTIALS_REJECTED", result.Error!.Code);
        Assert.Equal(AdapterErrorClass.Authentication, result.Error.Class);
        Assert.Contains("Hesap Yönetimi › Entegrasyon Bilgileri", result.Error.SafeMessage);
        Assert.Contains("API Kullanıcı Adı", result.Error.SafeMessage);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(Username, message);
            Assert.DoesNotContain(Password, message);
        });
    }

    [Fact]
    public void AnOrdinaryFaultIsARejectionWithPttAvmsSentence()
    {
        var (result, error) = PttAvmHttpClient.SoapResponse(HttpStatusCode.InternalServerError,
            """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Bitiş tarihi başlangıç tarihinden önce olamaz.</faultstring></s:Fault></s:Body></s:Envelope>""",
            PttAvmHttpClient.Orders);

        Assert.Null(result);
        Assert.Equal("PTTAVM_REQUEST_REJECTED", error!.Code);
        Assert.Contains("Bitiş tarihi", error.SafeMessage);
        Assert.Contains("40 gün", error.SafeMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void AGatewayPageIsATransientBlockNotACredentialsRefusal(HttpStatusCode status)
    {
        var (_, error) = PttAvmHttpClient.SoapResponse(status, "<!DOCTYPE html><html><head><title>Just a moment...</title></head><body>cloudflare</body></html>", PttAvmHttpClient.SupplierInfo);

        Assert.Equal("PTTAVM_GATEWAY_BLOCKED", error!.Code);
        Assert.Equal(AdapterErrorClass.RateLimit, error.Class);
        Assert.Equal(TimeSpan.FromMinutes(15), error.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "PTTAVM_REMOTE_ERROR", AdapterErrorClass.Remote5xx)]
    [InlineData(HttpStatusCode.BadGateway, "PTTAVM_REMOTE_ERROR", AdapterErrorClass.Remote5xx)]
    [InlineData(HttpStatusCode.TooManyRequests, "PTTAVM_RATE_LIMITED", AdapterErrorClass.RateLimit)]
    [InlineData(HttpStatusCode.RequestTimeout, "PTTAVM_REMOTE_TIMEOUT", AdapterErrorClass.TransientNetwork)]
    public void OtherFailuresAreTransient(HttpStatusCode status, string code, AdapterErrorClass @class)
    {
        var (_, error) = PttAvmHttpClient.SoapResponse(status, "", PttAvmHttpClient.SupplierInfo);

        Assert.Equal(code, error!.Code);
        Assert.Equal(@class, error.Class);
    }

    [Fact]
    public void NullParametersAreSentAsNilNeverAsEmptyOrMissingElements()
    {
        var envelope = PttAvmHttpClient.SoapEnvelope("BarkodKontrol", Account(), Now, [new("Barkod", null), new("SearchUrunAdi", "")]);

        Assert.Contains("""<tem:Barkod i:nil="true"/>""", envelope);
        Assert.Contains("<tem:SearchUrunAdi></tem:SearchUrunAdi>", envelope);
        Assert.Contains($"""xmlns:i="{PttAvmHttpClient.InstanceNamespace}" """.TrimEnd(), envelope);
        var nil = XDocument.Parse(envelope).Descendants(XName.Get("Barkod", PttAvmHttpClient.ServiceNamespace)).Single();
        Assert.True(PttAvmHttpClient.IsNil(nil));
    }

    [Fact]
    public void AnOperationWithoutParametersHasAnEmptyElement() =>
        Assert.Contains("<soapenv:Body><tem:GetMainCategories/></soapenv:Body>", PttAvmHttpClient.SoapEnvelope("GetMainCategories", Account(), Now, []));

    [Fact]
    public void TheDocumentedSupplierAndOrderAnswersAreRead()
    {
        var (supplierResult, supplierError) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, SupplierAnswer, PttAvmHttpClient.SupplierInfo);
        var (ordersResult, ordersError) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, OrdersAnswer, PttAvmHttpClient.Orders);

        Assert.Null(supplierError);
        Assert.Equal(("123456", "abcdef"), PttAvmHttpClient.Supplier(supplierResult!));
        Assert.Null(ordersError);
        var order = Assert.Single(PttAvmHttpClient.ContractItems(ordersResult!, "TedarikciSiparisKontrolV2"));
        Assert.Equal("PTT-L6LI1VK2D-211024", PttAvmHttpClient.ContractValue(order, "SiparisNo"));
        Assert.Null(PttAvmHttpClient.ContractValue(order, "VergiNo"));
        var line = order.Elements().Single(x => x.Name.LocalName == "SiparisUrunler").Elements().Single();
        Assert.Equal("232318670", PttAvmHttpClient.ContractValue(line, "LineItemId"));
    }

    [Fact]
    public void ANilOrMissingSupplierIsAContractProblem()
    {
        var nil = XElement.Parse("""<KullaniciTedarikciBilgisiGetirResult xmlns="http://tempuri.org/" xmlns:i="http://www.w3.org/2001/XMLSchema-instance" i:nil="true"/>""");
        var (_, wrongOperation) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, OrdersAnswer, PttAvmHttpClient.SupplierInfo);

        Assert.Null(PttAvmHttpClient.Supplier(nil));
        Assert.Equal("PTTAVM_SOAP_CONTRACT_INVALID", wrongOperation!.Code);
        Assert.DoesNotContain("PTT-L6LI1VK2D", wrongOperation.SafeMessage);
    }

    [Fact]
    public async Task TheProbeProvesReadsOnlyWithTheDocumentedAnswers()
    {
        var handler = new CapturingHandler(request => Operation(request) switch
        {
            "KullaniciTedarikciBilgisiGetir" => Respond(HttpStatusCode.OK, SupplierAnswer),
            "GetMainCategories" => Respond(HttpStatusCode.OK, CategoriesAnswer),
            "SiparisKontrolListesiV2" => Respond(HttpStatusCode.OK, OrdersAnswer),
            _ => Respond(HttpStatusCode.InternalServerError, """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Ürün bulunamadı</faultstring></s:Fault></s:Body></s:Envelope>""")
        });

        var result = await Client(handler).DiscoverCapabilitiesAsync(Account(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var evidence = result.Value!.ToDictionary(x => x.Code, x => x.SupportLevel);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.ConnectionTest]);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.ReferenceRead]);
        Assert.Equal("SUPPORTED", evidence[MarketplaceCapabilities.OrderRead]);
        Assert.Equal("UNKNOWN", evidence[MarketplaceCapabilities.ProductRead]);
        Assert.All(result.Value!, item => Assert.StartsWith("https://developers.pttavm.com/", item.SourceUrl));
        Assert.All(result.Value!, item => Assert.Equal("soap", item.SourceVersion));
        // 22:30 UTC is already the 22nd in Turkey.
        var orders = XDocument.Parse(handler.Requests.Single(x => x.Headers["SOAPAction"].Contains("SiparisKontrolListesiV2", StringComparison.Ordinal)).Body!);
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        Assert.Equal("2026-10-21", orders.Descendants(tem + "BaslangicTarihi").Single().Value);
        Assert.Equal("2026-10-22", orders.Descendants(tem + "BitisTarihi").Single().Value);
        Assert.Equal("0", orders.Descendants(tem + "AktifSiparisler").Single().Value);
        var stock = XDocument.Parse(handler.Requests.Single(x => x.Headers["SOAPAction"].Contains("StokKontrolListesi", StringComparison.Ordinal)).Body!);
        Assert.Equal(StoreId, stock.Descendants(tem + "ShopId").Single().Value);
        Assert.Equal("0", stock.Descendants(tem + "SearchPage").Single().Value);
    }

    [Fact]
    public async Task AnEmptyStockListStillProvesProductRead()
    {
        var handler = new CapturingHandler(request => Operation(request) switch
        {
            "KullaniciTedarikciBilgisiGetir" => Respond(HttpStatusCode.OK, SupplierAnswer),
            "GetMainCategories" => Respond(HttpStatusCode.OK, CategoriesAnswer),
            "SiparisKontrolListesiV2" => Respond(HttpStatusCode.OK, OrdersAnswer),
            _ => Respond(HttpStatusCode.OK, StockAnswer)
        });

        var result = await Client(handler).DiscoverCapabilitiesAsync(Account(), CancellationToken.None);

        Assert.All(result.Value!, item => Assert.Equal("SUPPORTED", item.SupportLevel));
    }

    [Fact]
    public void ACategoryAnswerWithSuccessFalseLeavesReferenceReadUnknown()
    {
        var refused = XElement.Parse("""<GetMainCategoriesResult xmlns="http://tempuri.org/" xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService"><a:error><a:error_code>E1</a:error_code><a:error_message>Kategori bulunamadı</a:error_message></a:error><a:success>false</a:success></GetMainCategoriesResult>""");

        Assert.Contains("Kategori bulunamadı", PttAvmHttpClient.MainCategoryError(refused));
    }

    [Theory]
    [InlineData("SiparisKontrolListesiV2", "orders")]
    [InlineData("SiparisDetay", "orders")]
    [InlineData("KargoBilgiListesi", "orders")]
    [InlineData("StokFiyatGuncelle3", "product-write")]
    [InlineData("StokGuncelleV2Bulk", "product-write")]
    [InlineData("UpdateProductsV3", "product-write")]
    [InlineData("StokKontrolListesi", "reads")]
    [InlineData("KullaniciTedarikciBilgisiGetir", "reads")]
    [InlineData(null, "reads")]
    public void RequestsAreCountedInTheirPttAvmBucket(string? operation, string bucket) =>
        Assert.Equal(bucket, PttAvmRateLimitHandler.BucketFor(operation));

    [Fact]
    public void ProductWritesAreSpacedFifteenSecondsApart()
    {
        var options = new PttAvmOptions();

        Assert.Equal(1d / 15, PttAvmRateLimitHandler.BucketShape(options, "product-write").TokensPerSecond, 6);
        Assert.Equal(1d, PttAvmRateLimitHandler.BucketShape(options, "product-write").Capacity);
        Assert.Equal(new Uri("https://ws.pttavm.com:93/service.svc"), options.SoapBaseAddress);
    }

    [Theory]
    [InlineData("PRODUCTION", "VERIFIED")]
    [InlineData("PRODUCTION", "ACTIVE")]
    public void WritesPassOnlyWhenEverySwitchIsOn(string environment, string status) =>
        Assert.Null(Client(new CapturingHandler(_ => throw new InvalidOperationException()), globalWrites: true).WriteBlocked<bool>(Account(environment, status, true), "test"));

    [Theory]
    [InlineData(false, "PRODUCTION", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "VERIFIED", false)]
    [InlineData(true, "STAGE", "VERIFIED", true)]
    [InlineData(true, "PRODUCTION", "DRAFT", true)]
    public void AnyClosedSwitchBlocksTheWrite(bool global, string environment, string status, bool connectionWrites)
    {
        var blocked = Client(new CapturingHandler(_ => throw new InvalidOperationException()), globalWrites: global).WriteBlocked<bool>(Account(environment, status, connectionWrites), "fiyat-stok");

        Assert.Equal("PTTAVM_EXTERNAL_WRITES_DISABLED", blocked!.Error!.Code);
        Assert.Contains("fiyat-stok gönderilmedi", blocked.Error.SafeMessage);
    }

    [Fact]
    public void LoggedRequestContextDoesNotPrintTheUserNameOrThePassword()
    {
        var context = Account();
        var request = PttAvmHttpClient.BuildSoapRequest(context, new PttAvmOptions().SoapBaseAddress, PttAvmHttpClient.SupplierInfo, Now, []);

        Assert.DoesNotContain(Username, context.ToString());
        Assert.DoesNotContain(Password, context.ToString());
        Assert.True(request.Options.TryGetValue(PttAvmRateLimitHandler.AccountKeyOption, out var bucket));
        Assert.Equal(PttAvmRateLimitHandler.AccountKey(Username), bucket);
        Assert.DoesNotContain(Username, bucket);
        Assert.DoesNotContain(Password, string.Join(",", request.Headers.Select(x => x.Key + "=" + string.Join(",", x.Value))));
    }

    [Fact]
    public void AStoredUsernameAndPasswordAreRead()
    {
        var load = PttAvmAuthenticationHandler.Read(Account().Connection, """{"Username":" ravencia-api-user ","Password":"S3cr3t&<pass>"}""");

        Assert.False(load.LegacyApiKeyPayload);
        Assert.Equal(Username, load.Account!.Username);
        Assert.Equal(Password, load.Account.Password);
    }

    [Fact]
    public void AnOldApiKeyPayloadIsReportedAsRejectedCredentialsAskingForTheNewValues()
    {
        var load = PttAvmAuthenticationHandler.Read(Account().Connection, """{"ApiKey":"old-key","AccessToken":"old-token"}""");

        Assert.Null(load.Account);
        Assert.True(load.LegacyApiKeyPayload);
        Assert.False(PttAvmAuthenticationHandler.Read(Account().Connection, "{}").LegacyApiKeyPayload);
        Assert.Equal("PTTAVM_CREDENTIAL_INVALID", PttAvmHttpClient.CredentialFailure<bool>(PttAvmCredentialLoad.Missing).Error!.Code);

        var result = PttAvmHttpClient.CredentialFailure<ConnectionIdentity>(load);
        Assert.Equal("PTTAVM_CREDENTIALS_REJECTED", result.Error!.Code);
        Assert.Equal(AdapterErrorClass.Authentication, result.Error.Class);
        Assert.Contains("Mağaza ID, API Kullanıcı Adı ve API Şifresi", result.Error.SafeMessage);
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("Ravencia", null)]
    [InlineData("12 34", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    public void TheStoreIdIsTheNumericMağazaId(string value, string? expected) =>
        Assert.Equal(expected, PttAvmHttpClient.StoreId(value));

    [Theory]
    [InlineData(" pass word ", "pass word")]
    [InlineData("​şifre123", "şifre123")]
    [InlineData("pa\u0001ss", null)]
    [InlineData("   ", null)]
    public void ThePasswordIsKeptAsTypedApartFromInvisibleCharacters(string value, string? expected) =>
        Assert.Equal(expected, MarketplaceConnectionService.PttAvmPasswordValue(value));

    // Order Control V2 as published (two orders of the sample, the first cut to two of its lines), plus an open
    // order with one line waiting for cargo and one already sent.
    private const string OrderListAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
           <s:Body>
              <SiparisKontrolListesiV2Response xmlns="http://tempuri.org/">
                 <SiparisKontrolListesiV2Result xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                    <a:TedarikciSiparisKontrolV2>
                       <a:Barcodes xmlns:b="http://schemas.datacontract.org/2004/07/ePttAVMService.Model">
                          <b:Barkod><b:barcode>2710243114843</b:barcode><b:type>KARGO</b:type></b:Barkod>
                       </a:Barcodes>
                       <a:Eposta>Deneme@gmail.com</a:Eposta>
                       <a:FaturaAdresi>Fatura Sokak 1</a:FaturaAdresi>
                       <a:FaturaIlce>Çankaya</a:FaturaIlce>
                       <a:FaturaIli>Ankara</a:FaturaIli>
                       <a:FaturaMusteriAdi>Ayşe</a:FaturaMusteriAdi>
                       <a:FaturaMusteriSoyadi>Yılmaz</a:FaturaMusteriSoyadi>
                       <a:FaturaTip>Bireysel</a:FaturaTip>
                       <a:FirmaUnvani/>
                       <a:IslemTarihi>2024-10-07T17:38:29</a:IslemTarihi>
                       <a:KargoBarkod>2710243114843</a:KargoBarkod>
                       <a:KargoTutari>1330</a:KargoTutari>
                       <a:MusteriAdi>Ayşe</a:MusteriAdi>
                       <a:MusteriId>902933</a:MusteriId>
                       <a:MusteriSoyadi>Yılmaz</a:MusteriSoyadi>
                       <a:SiparisAdresi>Teslimat Sokak 2</a:SiparisAdresi>
                       <a:SiparisIlce>Keçiören</a:SiparisIlce>
                       <a:SiparisIli>Ankara</a:SiparisIli>
                       <a:SiparisNo>PTT-A1712HP2E-071024</a:SiparisNo>
                       <a:SiparisUrunler>
                          <a:SiparisUrun>
                             <a:IndirimPttavm>2.71</a:IndirimPttavm>
                             <a:IndirimTedarikci>0</a:IndirimTedarikci>
                             <a:IndirimToplam>2.71</a:IndirimToplam>
                             <a:KdvDahilToplamTutar>33.92</a:KdvDahilToplamTutar>
                             <a:KdvOrani>10</a:KdvOrani>
                             <a:LineItemId>229049580</a:LineItemId>
                             <a:SiparisDurumu>iade</a:SiparisDurumu>
                             <a:ToplamIslemAdedi>1</a:ToplamIslemAdedi>
                             <a:Urun>parça yönetimi 6</a:Urun>
                             <a:UrunBarkod>0000000066266mainbrkd</a:UrunBarkod>
                             <a:UrunId>944333635</a:UrunId>
                          </a:SiparisUrun>
                          <a:SiparisUrun>
                             <a:IndirimToplam>0</a:IndirimToplam>
                             <a:KdvDahilToplamTutar>41.25</a:KdvDahilToplamTutar>
                             <a:KdvOrani>10</a:KdvOrani>
                             <a:LineItemId>229049601</a:LineItemId>
                             <a:SiparisDurumu>iptal</a:SiparisDurumu>
                             <a:ToplamIslemAdedi>1</a:ToplamIslemAdedi>
                             <a:Urun>parça yönetimi 4</a:Urun>
                             <a:UrunBarkod>abs123456</a:UrunBarkod>
                          </a:SiparisUrun>
                       </a:SiparisUrunler>
                       <a:TCKN>67351072674</a:TCKN>
                       <a:TelefonNo>123123123</a:TelefonNo>
                       <a:VergiDaire/>
                       <a:VergiNo/>
                    </a:TedarikciSiparisKontrolV2>
                    <a:TedarikciSiparisKontrolV2>
                       <a:Barcodes xmlns:b="http://schemas.datacontract.org/2004/07/ePttAVMService.Model"/>
                       <a:IslemTarihi>2024-10-21T14:48:46</a:IslemTarihi>
                       <a:KargoBarkod/>
                       <a:MusteriAdi>test</a:MusteriAdi>
                       <a:MusteriSoyadi>test</a:MusteriSoyadi>
                       <a:SiparisNo>PTT-L6LI1VK2D-211024</a:SiparisNo>
                       <a:SiparisUrunler>
                          <a:SiparisUrun>
                             <a:IndirimToplam>3.72</a:IndirimToplam>
                             <a:KdvDahilToplamTutar>49.66</a:KdvDahilToplamTutar>
                             <a:KdvOrani>1</a:KdvOrani>
                             <a:LineItemId>232318670</a:LineItemId>
                             <a:SiparisDurumu>iptal</a:SiparisDurumu>
                             <a:ToplamIslemAdedi>1</a:ToplamIslemAdedi>
                             <a:Urun>Test ürünü Fırın</a:Urun>
                             <a:UrunBarkod>10102024-85224</a:UrunBarkod>
                          </a:SiparisUrun>
                       </a:SiparisUrunler>
                    </a:TedarikciSiparisKontrolV2>
                    <a:TedarikciSiparisKontrolV2>
                       <a:IslemTarihi>2026-10-21T09:15:00</a:IslemTarihi>
                       <a:KargoBarkod/>
                       <a:SiparisNo>PTT-OPEN12345-211026</a:SiparisNo>
                       <a:SiparisUrunler>
                          <a:SiparisUrun>
                             <a:IndirimToplam>10</a:IndirimToplam>
                             <a:KdvDahilToplamTutar>190</a:KdvDahilToplamTutar>
                             <a:KdvOrani>20</a:KdvOrani>
                             <a:LineItemId>300000001</a:LineItemId>
                             <a:SiparisDurumu>kargo_yapilmasi_bekleniyor</a:SiparisDurumu>
                             <a:ToplamIslemAdedi>2</a:ToplamIslemAdedi>
                             <a:Urun>Gömlek</a:Urun>
                             <a:UrunBarkod>8690000000017</a:UrunBarkod>
                          </a:SiparisUrun>
                          <a:SiparisUrun>
                             <a:IndirimToplam>0</a:IndirimToplam>
                             <a:KdvDahilToplamTutar>50</a:KdvDahilToplamTutar>
                             <a:KdvOrani>20</a:KdvOrani>
                             <a:LineItemId>300000002</a:LineItemId>
                             <a:SiparisDurumu>gönderilmiş</a:SiparisDurumu>
                             <a:ToplamIslemAdedi>1</a:ToplamIslemAdedi>
                             <a:Urun>Kemer</a:Urun>
                             <a:UrunBarkod>8690000000024</a:UrunBarkod>
                          </a:SiparisUrun>
                       </a:SiparisUrunler>
                    </a:TedarikciSiparisKontrolV2>
                 </SiparisKontrolListesiV2Result>
              </SiparisKontrolListesiV2Response>
           </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public async Task AnOrderWindowIsReadInOneRequestWithEveryStatus()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, OrderListAnswer));

        var result = await Client(handler).ReadOrdersAsync(Account(), new DateTime(2026, 9, 1), new DateTime(2026, 10, 6), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.False(result.Value!.HasMore);
        Assert.Equal(["PTT-A1712HP2E-071024", "PTT-L6LI1VK2D-211024", "PTT-OPEN12345-211026"], result.Value.Items.Select(x => x.ExternalOrderId));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("\"http://tempuri.org/IService/SiparisKontrolListesiV2\"", request.Headers["SOAPAction"]);
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        var body = XDocument.Parse(request.Body!).Descendants(tem + "SiparisKontrolListesiV2").Single();
        Assert.Equal("2026-09-01", body.Element(tem + "BaslangicTarihi")!.Value);
        Assert.Equal("2026-10-06", body.Element(tem + "BitisTarihi")!.Value);
        Assert.Equal("0", body.Element(tem + "AktifSiparisler")!.Value);
    }

    [Fact]
    public void AnOrderKeepsItsLinesCustomerAddressesAndCargoBarcode()
    {
        var order = OrderFromList("PTT-A1712HP2E-071024");

        // IslemTarihi carries no offset: Turkey time.
        Assert.Equal(new DateTimeOffset(2024, 10, 7, 14, 38, 29, TimeSpan.Zero), order.OrderedAt);
        Assert.Equal(["229049580", "229049601"], order.Lines.Select(x => x.ExternalLineId));
        var returned = order.Lines[0];
        Assert.Equal("Returned", returned.RawStatus);
        Assert.Equal("0000000066266mainbrkd", returned.Barcode);
        Assert.Equal("0000000066266mainbrkd", returned.Sku);
        Assert.Equal(36.63m, returned.UnitPrice);
        Assert.Equal(10m, returned.VatRate);
        Assert.Equal("Cancelled", order.Lines[1].RawStatus);
        Assert.Equal(1m, order.Lines[1].CancelledQuantity);
        // The cancelled line is no part of the value while the returned line is live.
        Assert.Equal(36.63m, order.GrossAmount);
        Assert.Equal(2.71m, order.DiscountAmount);
        Assert.Equal(33.92m, order.NetAmount);
        var package = Assert.Single(order.Packages);
        Assert.Equal("order:PTT-A1712HP2E-071024", package.ExternalPackageId);
        Assert.Equal("Returned", package.RawStatus);
        Assert.Equal("PTT Kargo", package.CargoProviderExternalId);
        Assert.Equal("2710243114843", package.CargoTrackingNumber);
        var customer = System.Text.Json.Nodes.JsonNode.Parse(order.CustomerSnapshotJson)!;
        Assert.Equal("Ayşe Yılmaz", (string?)customer["customerName"]);
        Assert.Equal("67351072674", (string?)customer["tcIdentityNumber"]);
        Assert.Equal(1330m, (decimal?)customer["shipmentAmount"]);
        var delivery = System.Text.Json.Nodes.JsonNode.Parse(order.ShipmentAddressSnapshotJson)!;
        Assert.Equal("Ankara", (string?)delivery["city"]);
        Assert.Equal("Keçiören", (string?)delivery["district"]);
        Assert.Equal("Ayşe Yılmaz", (string?)delivery["fullName"]);
        Assert.Equal("Fatura Sokak 1", (string?)System.Text.Json.Nodes.JsonNode.Parse(order.InvoiceAddressSnapshotJson)!["address1"]);
        Assert.DoesNotContain("SiparisUrunler", order.RawJson);
    }

    [Fact]
    public void AnOpenOrderWaitsWhereItsLeastAdvancedLineIs()
    {
        var order = OrderFromList("PTT-OPEN12345-211026");

        Assert.Equal(["Created", "Shipped"], order.Lines.Select(x => x.RawStatus));
        var package = Assert.Single(order.Packages);
        Assert.Equal("Created", package.RawStatus);
        Assert.Null(package.CargoTrackingNumber);
        Assert.Equal([0m, 1m], package.Allocations.Select(x => x.ShippedQuantity));
        Assert.Equal(100m, order.Lines[0].UnitPrice);
        Assert.Equal(250m, order.GrossAmount);
        Assert.Equal(240m, order.NetAmount);
        Assert.Equal(ShipmentPackageStatus.New, TrendyolShipmentPackageStatusPolicy.FromRemote(package.RawStatus));
    }

    [Fact]
    public void AFullyCancelledOrderKeepsTheValueItWasPlacedWith()
    {
        var order = OrderFromList("PTT-L6LI1VK2D-211024");

        Assert.Equal("Cancelled", Assert.Single(order.Packages).RawStatus);
        Assert.Equal(53.38m, order.GrossAmount);
        Assert.Equal(49.66m, order.NetAmount);
    }

    [Theory]
    [InlineData("kargo_yapilmasi_bekleniyor", "Created", ShipmentPackageStatus.New)]
    [InlineData("havale_onayi_bekleniyor", "Awaiting", ShipmentPackageStatus.PaymentPending)]
    [InlineData("onay_surecinde", "Awaiting", ShipmentPackageStatus.PaymentPending)]
    [InlineData("gonderilmiş", "Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData("Gönderilmiş", "Shipped", ShipmentPackageStatus.Shipped)]
    [InlineData("gondericisine_teslim_edildi", "Undelivered", ShipmentPackageStatus.Undelivered)]
    [InlineData("tamamlandi", "Delivered", ShipmentPackageStatus.Delivered)]
    [InlineData("iptal", "Cancelled", ShipmentPackageStatus.Cancelled)]
    [InlineData("odeme_gecersiz", "Cancelled", ShipmentPackageStatus.Cancelled)]
    [InlineData("iade", "Returned", ShipmentPackageStatus.Returned)]
    public void LineStatusesReadAsPanelPackageStatuses(string value, string raw, ShipmentPackageStatus status)
    {
        Assert.Equal(raw, PttAvmOrderMapper.LineStatus(value));
        Assert.Equal(status, TrendyolShipmentPackageStatusPolicy.FromRemote(raw));
    }

    [Fact]
    public void AnUnknownLineStatusIsKeptForReview() =>
        Assert.Equal("Unknown:yeni_durum", PttAvmOrderMapper.LineStatus("yeni_durum"));

    [Fact]
    public void EachPackageStatusIsItsOwnLaterEvent()
    {
        var ordered = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        string[] path = ["Awaiting", "Created", "Shipped", "Delivered", "Returned"];

        var times = path.Select(status => PttAvmOrderMapper.StatusObservedAt(ordered, status)).ToArray();

        Assert.Equal(times.OrderBy(x => x), times);
        Assert.Equal(times.Length, times.Distinct().Count());
        Assert.True(PttAvmOrderMapper.StatusObservedAt(ordered, "Cancelled") > PttAvmOrderMapper.StatusObservedAt(ordered, "Shipped"));
    }

    [Theory]
    [InlineData("PTT-A1712HP2E-071024", 2024, 10, 7)]
    [InlineData("PTT-L6LI1VK2D-211024", 2024, 10, 21)]
    public void TheOrderNumberNamesTheOrderDay(string orderNumber, int year, int month, int day) =>
        Assert.Equal(new DateTime(year, month, day), PttAvmOrderMapper.OrderDay(orderNumber));

    [Theory]
    [InlineData("PTT-A1712HP2E")]
    [InlineData("PTT-A1712HP2E-991324")]
    [InlineData("")]
    public void AnOrderNumberWithoutADayNamesNone(string orderNumber) => Assert.Null(PttAvmOrderMapper.OrderDay(orderNumber));

    [Fact]
    public async Task AnAnswerOfAnotherShapeIsAContractViolation()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, OrdersAnswer.Replace("TedarikciSiparisKontrolV2", "BaskaBirSey", StringComparison.Ordinal)));

        var result = await Client(handler).ReadOrdersAsync(Account(), new DateTime(2026, 10, 20), new DateTime(2026, 10, 21), CancellationToken.None);

        Assert.Equal("PTTAVM_ORDER_CONTRACT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void HistoryWindowsStayWithinPttAvmsFortyDays()
    {
        var now = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
        var start = PttAvmOrderHistoryPolicy.StartDay(true, null, now);

        var windows = PttAvmOrderHistoryPolicy.Windows(start, now);

        Assert.Equal(new DateTime(2026, 7, 21), start);
        Assert.Equal(start, windows[0].Start);
        Assert.Equal(new DateTime(2026, 10, 22), windows[^1].End);
        Assert.All(windows, window => Assert.True(window.End - window.Start <= PttAvmHttpClient.MaxOrderWindow));
        Assert.All(windows.Zip(windows.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
        // An incremental run re-reads the last two days.
        Assert.Equal(new DateTime(2026, 10, 19), PttAvmOrderHistoryPolicy.StartDay(false, now.AddMinutes(-2), now));
    }

    [Fact]
    public void TheOpenOrderScanStartsAtTheOldestRecentOpenOrder()
    {
        var now = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

        Assert.Null(PttAvmOrderHistoryPolicy.OpenOrderStartDay([], now));
        Assert.Null(PttAvmOrderHistoryPolicy.OpenOrderStartDay([now.AddMonths(-5)], now));
        Assert.Equal(new DateTime(2026, 9, 2), PttAvmOrderHistoryPolicy.OpenOrderStartDay([now.AddDays(-3), now.AddDays(-49), now.AddMonths(-5)], now));
    }

    [Fact]
    public void PttAvmOrderReadsHaveTheirOwnJobTypesLaneAndSchedule()
    {
        Assert.Equal(MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.OrderSync));
        Assert.Equal(MarketplaceJobTypes.PttAvmOrderRecoverySync, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.OrderRecoverySync));
        Assert.Equal(MarketplaceJobTypes.PttAvmOrderStatusSync, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.OrderStatusSync));
        foreach (var type in new[] { MarketplaceJobTypes.PttAvmOrderSync, MarketplaceJobTypes.PttAvmOrderRecoverySync, MarketplaceJobTypes.PttAvmOrderStatusSync })
        {
            Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(type));
            Assert.Equal("orders", MarketplaceSyncExecutionLock.GroupFor(type));
            Assert.Equal("pttavm-orders", WorkerLanes.LaneFor(type, JobPriorityPolicy.Realtime));
        }
        Assert.True(ScheduledJobProducer.SupportsPttAvmScheduledPolicy("ORDERS"));
        Assert.True(ScheduledJobProducer.SupportsPttAvmScheduledPolicy("ORDER_LIFECYCLE"));
        Assert.False(ScheduledJobProducer.SupportsPttAvmScheduledPolicy("QUESTIONS"));
        Assert.False(ScheduledJobProducer.DefaultPolicyEnabled("PTTAVM", MarketplaceExternalWritePolicies.Stock));
        Assert.Equal((120, 30), ScheduledOrderPollingCadencePolicy.ForPlatform("PTTAVM", "ORDERS", 60, 30));
        Assert.Equal((900, 30), ScheduledOrderPollingCadencePolicy.ForPlatform("PTTAVM", "ORDER_LIFECYCLE", 120, 60));
        var plan = MarketplaceConnectionService.CreateInitialDataSyncPlan("PTTAVM", Guid.NewGuid(), false);
        Assert.Equal([MarketplaceJobTypes.PttAvmReferenceSync, MarketplaceJobTypes.PttAvmOrderRecoverySync, MarketplaceJobTypes.ReturnSync], plan.Select(x => x.JobType));
        Assert.Contains("\"resourceType\":\"CATEGORIES\"", plan[0].PayloadJson);
    }

    // GetCategoryTree as published (one branch), with a second root and a repeated id.
    private const string CategoryTreeAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
           <s:Body>
              <GetCategoryTreeResponse xmlns="http://tempuri.org/">
                 <GetCategoryTreeResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                    <a:success>true</a:success>
                    <a:category_tree>
                       <a:Category>
                          <a:id>4</a:id><a:name>Kozmetik &amp; Kişisel Bakım</a:name><a:parent_id>0</a:parent_id><a:updated_at>10/14/2022 1:42:14 AM</a:updated_at>
                          <a:children>
                             <a:Category>
                                <a:id>868</a:id><a:name>Kozmetik</a:name><a:parent_id>4</a:parent_id>
                                <a:children>
                                   <a:Category>
                                      <a:id>869</a:id><a:name>Parfüm</a:name><a:parent_id>868</a:parent_id>
                                      <a:children>
                                         <a:Category><a:id>870</a:id><a:name>Erkek Parfüm</a:name><a:parent_id>869</a:parent_id><a:children i:nil="true"/></a:Category>
                                      </a:children>
                                   </a:Category>
                                </a:children>
                             </a:Category>
                          </a:children>
                       </a:Category>
                       <a:Category>
                          <a:id>20</a:id><a:name>Elektronik</a:name><a:parent_id>0</a:parent_id><a:children/>
                       </a:Category>
                       <a:Category>
                          <a:id>868</a:id><a:name>Kozmetik (tekrar)</a:name><a:parent_id>0</a:parent_id>
                       </a:Category>
                    </a:category_tree>
                    <a:error i:nil="true"/>
                 </GetCategoryTreeResult>
              </GetCategoryTreeResponse>
           </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public async Task TheCategoryTreeIsReadInOneRequestWithPathsAndLeaves()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, CategoryTreeAnswer));

        var result = await Client(handler).ReadCategoriesAsync(Account(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var items = result.Value!.Items;
        // Parents first (by depth, then path); the repeated 868 keeps its first place.
        Assert.Equal(["20", "4", "868", "869", "870"], items.Select(x => x.ExternalId));
        var leaf = items.Single(x => x.ExternalId == "870");
        Assert.Equal("869", leaf.ParentExternalId);
        Assert.Equal("Kozmetik & Kişisel Bakım / Kozmetik / Parfüm / Erkek Parfüm", leaf.Path);
        Assert.Equal(3, leaf.Depth);
        Assert.True(leaf.IsLeaf);
        Assert.False(items.Single(x => x.ExternalId == "869").IsLeaf);
        Assert.True(items.Single(x => x.ExternalId == "20").IsLeaf);
        Assert.Null(items.Single(x => x.ExternalId == "4").ParentExternalId);
        Assert.Equal("Kozmetik", items.Single(x => x.ExternalId == "868").Name);
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        var body = XDocument.Parse(Assert.Single(handler.Requests).Body!).Descendants(tem + "GetCategoryTree").Single();
        Assert.Equal("0", body.Element(tem + "parent_id")!.Value);
    }

    [Fact]
    public async Task AnUnsuccessfulCategoryAnswerIsAContractViolation()
    {
        var failed = CategoryTreeAnswer.Replace("<a:success>true</a:success>", "<a:success>false</a:success>", StringComparison.Ordinal);

        var result = await Client(new CapturingHandler(_ => Respond(HttpStatusCode.OK, failed))).ReadCategoriesAsync(Account(), CancellationToken.None);

        Assert.Equal("PTTAVM_REFERENCE_CONTRACT_INVALID", result.Error!.Code);
    }

    // StokKontrolListesi as published, with a second product carrying variants in the write-side shape.
    private const string ProductListAnswer = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
           <s:Body>
              <StokKontrolListesiResponse xmlns="http://tempuri.org/">
                 <StokKontrolListesiResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                    <a:StokKontrolDetay>
                       <a:Aciklama>Kısa açıklama</a:Aciklama>
                       <a:Aktif>true</a:Aktif>
                       <a:Barkod>markaekleme1</a:Barkod>
                       <a:Desi>1</a:Desi>
                       <a:KDVOran>20</a:KDVOran>
                       <a:KDVli>0</a:KDVli>
                       <a:KDVsiz>7499.167</a:KDVsiz>
                       <a:Miktar>1</a:Miktar>
                       <a:Resim1Stream i:nil="true"/>
                       <a:Resim1Url>https://cdn.example.com/a.jpg</a:Resim1Url>
                       <a:Resim2Url/>
                       <a:RowCount>4755</a:RowCount>
                       <a:UrunAdi>marka ekleme Soap UI</a:UrunAdi>
                       <a:UrunId>1234811128</a:UrunId>
                       <a:UrunKodu>MARKA_EKLEME_1</a:UrunKodu>
                       <a:UzunAciklama>uzun açıklama</a:UzunAciklama>
                       <a:VariantListesi i:nil="true"/>
                       <a:YeniKategoriId>1632</a:YeniKategoriId>
                    </a:StokKontrolDetay>
                    <a:StokKontrolDetay>
                       <a:Aktif>false</a:Aktif>
                       <a:Barkod>tshirt-1072</a:Barkod>
                       <a:KDVOran>10</a:KDVOran>
                       <a:KDVli>110</a:KDVli>
                       <a:Miktar>21</a:Miktar>
                       <a:RowCount>4755</a:RowCount>
                       <a:UrunAdi>Tişört</a:UrunAdi>
                       <a:UrunId>744445043</a:UrunId>
                       <a:VariantListesi>
                          <a:Variant>
                             <a:Attributes>
                                <a:Attribute><a:Definition>Renk</a:Definition><a:Value>Kırmızı</a:Value></a:Attribute>
                                <a:Attribute><a:Definition>Beden</a:Definition><a:Value>XL</a:Value></a:Attribute>
                             </a:Attributes>
                             <a:Price>10</a:Price>
                             <a:Quantity>16</a:Quantity>
                             <a:VariantBarcode>tshirt-kirmizi-xl</a:VariantBarcode>
                          </a:Variant>
                          <a:Variant>
                             <a:Attributes>
                                <a:Attribute><a:Definition>Renk</a:Definition><a:Value>Mavi</a:Value></a:Attribute>
                             </a:Attributes>
                             <a:Quantity>5</a:Quantity>
                          </a:Variant>
                       </a:VariantListesi>
                       <a:YeniKategoriId>0</a:YeniKategoriId>
                    </a:StokKontrolDetay>
                 </StokKontrolListesiResult>
              </StokKontrolListesiResponse>
           </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public async Task AProductPageMapsBarcodesPricesStockAndVariants()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, ProductListAnswer));

        var result = await Client(handler).ReadProductPageAsync(Account(), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var page = result.Value!;
        Assert.Equal(4755, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal("1:markaekleme1", page.NextCursor);
        var simple = page.Items[0];
        Assert.Equal("1234811128", simple.ExternalProductId);
        Assert.Equal("1632", simple.CategoryExternalId);
        Assert.Equal("uzun açıklama", simple.Description);
        Assert.Equal(["https://cdn.example.com/a.jpg"], simple.ImageUrls);
        var only = Assert.Single(simple.Variants);
        Assert.Equal("markaekleme1", only.Barcode);
        Assert.Equal("markaekleme1", only.Sku);
        Assert.Null(only.ModelCode);
        // KDVli is 0 in the sample: the price is worked out from KDVsiz and the VAT rate.
        Assert.Equal(8999.00m, only.SalePrice);
        Assert.Equal(20m, only.VatRate);
        Assert.Equal(1m, only.StockQuantity);
        Assert.False(only.Archived);
        var withVariants = page.Items[1];
        Assert.Null(withVariants.CategoryExternalId);
        var variant = Assert.Single(withVariants.Variants);
        Assert.Equal("tshirt-kirmizi-xl", variant.Barcode);
        Assert.Equal(120m, variant.SalePrice);
        Assert.Equal(16m, variant.StockQuantity);
        Assert.True(variant.Archived);
        Assert.Equal("Kırmızı", variant.Options["Renk"]);
        Assert.Equal("XL", variant.Options["Beden"]);
        // The variant without a barcode is reported, not imported.
        Assert.Contains(page.Issues!, issue => issue.Code == "PRODUCT_VARIANT_INVALID");
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        var body = XDocument.Parse(Assert.Single(handler.Requests).Body!).Descendants(tem + "StokKontrolListesi").Single();
        Assert.Equal(StoreId, body.Element(tem + "ShopId")!.Value);
        Assert.Equal("0", body.Element(tem + "SearchAktifPasif")!.Value);
        Assert.Equal("0", body.Element(tem + "SearchPage")!.Value);
    }

    [Fact]
    public async Task APageThatRepeatsTheFirstOneEndsTheScan()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, ProductListAnswer));

        var result = await Client(handler).ReadProductPageAsync(Account(), "3:markaekleme1", null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.False(result.Value.HasMore);
        Assert.Contains(result.Value.Issues!, issue => issue.Code == "PTTAVM_PRODUCT_PAGE_REPEATED");
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        Assert.Equal("3", XDocument.Parse(handler.Requests[0].Body!).Descendants(tem + "SearchPage").Single().Value);
    }

    [Fact]
    public async Task ABarcodeLookupReadsOnePageOnly()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, ProductListAnswer));

        var result = await Client(handler).ReadProductPageAsync(Account(), null, "tshirt-1072", CancellationToken.None);

        Assert.False(result.Value!.HasMore);
        XNamespace tem = PttAvmHttpClient.ServiceNamespace;
        Assert.Equal("tshirt-1072", XDocument.Parse(handler.Requests[0].Body!).Descendants(tem + "SearchBarkod").Single().Value);
    }

    [Theory]
    [InlineData(0, 100, 4755, true)]
    [InlineData(47, 100, 4755, false)]
    [InlineData(0, 100, null, true)]
    [InlineData(0, 3, 3, false)]
    public void TheScanStopsOncePagesCoverTheRowCount(int page, int items, int? total, bool expected) =>
        Assert.Equal(expected, PttAvmHttpClient.MorePages(page, items, total));

    [Fact]
    public void PttAvmCategoriesHaveTheirOwnReferenceJobAndDailySchedule()
    {
        Assert.Equal(MarketplaceJobTypes.PttAvmReferenceSync, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.ReferenceSync));
        Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(MarketplaceJobTypes.PttAvmReferenceSync));
        Assert.Equal(MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ForPlatform("PTTAVM", MarketplaceJobTypes.ProductSync));
        Assert.True(ScheduledJobProducer.SupportsPttAvmScheduledPolicy("REFERENCE_DATA"));
        Assert.True(CatalogService.TryProductPlatformFilter("PTTAVM:ACTIVE", out var code, out _));
        Assert.Equal("PTTAVM", code);
    }

    [Fact]
    public void ReturnedOrderLinesBecomeOneCompletedReturn()
    {
        var claim = PttAvmHttpClient.ReturnClaim(OrderFromList("PTT-A1712HP2E-071024"))!;

        Assert.Equal("iade:PTT-A1712HP2E-071024", claim.ExternalClaimId);
        Assert.Equal("PTT-A1712HP2E-071024", claim.ExternalOrderId);
        Assert.Equal(ReturnClaimStatus.Completed, MarketplaceReturnStatus.Canonicalize(claim.RawStatus, null));
        // Only the returned line, not the cancelled one.
        var line = Assert.Single(claim.Lines);
        Assert.Equal("229049580", line.ExternalOrderLineId);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal("2710243114843", claim.CargoTrackingNumber);
        Assert.Equal(new DateTimeOffset(2024, 10, 7, 14, 38, 29, TimeSpan.Zero), claim.OrderCreatedAt);
    }

    [Fact]
    public void AnOrderWithoutReturnedLinesHasNoReturn()
    {
        Assert.Null(PttAvmHttpClient.ReturnClaim(OrderFromList("PTT-OPEN12345-211026")));
        Assert.Null(PttAvmHttpClient.ReturnClaim(OrderFromList("PTT-L6LI1VK2D-211024")));
    }

    [Fact]
    public async Task ReturnDecisionsAreNeverSentToPttAvm()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException());
        IReturnPort port = Client(handler);

        var result = await port.ExecuteAsync(new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "c", "k", Now), new("iade:PTT-1", ["1"], "APPROVE", null, null, []), CancellationToken.None);

        Assert.Equal("PTTAVM_RETURN_ACTION_UNSUPPORTED", result.Error!.Code);
        Assert.Empty(handler.Requests);
        Assert.True(ScheduledJobProducer.SupportsPttAvmScheduledPolicy("RETURNS"));
    }

    private const string InvoicePayload = """{"invoiceLink":"https://panel.ravencia.com/api/v1/public/invoices/abc/document","invoiceNumber":"RVC2026000001","orderNumber":"PTT-A1712HP2E-071024","lineItemIds":["229049580","229049580","x"]}""";

    [Fact]
    public void AnInvoiceRequestCarriesThePdfLinkTheOrderAndItsLines()
    {
        var request = PttAvmInvoiceMapper.Request(InvoicePayload);

        Assert.Null(request.Error);
        Assert.Equal([229049580L], request.LineItemIds);
        var envelope = PttAvmHttpClient.SoapEnvelope(PttAvmHttpClient.SaveInvoice, Account(), Now, PttAvmInvoiceMapper.Parameters(request));
        XNamespace tem = PttAvmHttpClient.ServiceNamespace, arr = PttAvmHttpClient.ArrayNamespace;
        var body = XDocument.Parse(envelope).Descendants(tem + "SaveInvoince").Single();
        Assert.Equal(["lineItemId", "orderId", "content", "url"], body.Elements().Select(x => x.Name.LocalName));
        Assert.Equal("229049580", body.Element(tem + "lineItemId")!.Element(arr + "int")!.Value);
        Assert.Equal("PTT-A1712HP2E-071024", body.Element(tem + "orderId")!.Value);
        Assert.Equal(request.Link, body.Element(tem + "url")!.Value);
    }

    [Fact]
    public void AnInvoiceRequestWithoutLinesOrHttpsIsRefused()
    {
        Assert.NotNull(PttAvmInvoiceMapper.Request("""{"invoiceLink":"https://x.test/a.pdf","orderNumber":"PTT-1-010125","lineItemIds":[]}""").Error);
        Assert.NotNull(PttAvmInvoiceMapper.Request("""{"invoiceLink":"http://x.test/a.pdf","orderNumber":"PTT-1-010125","lineItemIds":["1"]}""").Error);
    }

    [Fact]
    public async Task NoInvoiceIsSentWhileExternalWritesAreOff()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException());
        var client = Client(handler, globalWrites: false);

        var blocked = client.WriteBlocked<InvoiceDeliveryResult>(Account(status: "ACTIVE", connectionWrites: true), "fatura linki");

        Assert.Equal("PTTAVM_EXTERNAL_WRITES_DISABLED", blocked!.Error!.Code);
        Assert.Null(Client(handler, globalWrites: true).WriteBlocked<InvoiceDeliveryResult>(Account(status: "ACTIVE", connectionWrites: true), "fatura linki"));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("<a:Error_Message i:nil=\"true\"/><a:Success>true</a:Success>", PttAvmInvoiceMapper.SaveOutcome.Saved)]
    [InlineData("<a:Error_Message>işlem gerçekleştirilmiştir.</a:Error_Message><a:Success>false</a:Success>", PttAvmInvoiceMapper.SaveOutcome.Unclear)]
    [InlineData("<a:Error_Message>Sipariş bulunamadı.</a:Error_Message><a:Success>false</a:Success>", PttAvmInvoiceMapper.SaveOutcome.Rejected)]
    public void TheInvoiceAnswerIsReadWithoutGuessing(string fields, PttAvmInvoiceMapper.SaveOutcome expected)
    {
        var result = XElement.Parse($"""<SaveInvoinceResult xmlns:a="{PttAvmHttpClient.ContractNamespace}" xmlns:i="{PttAvmHttpClient.InstanceNamespace}">{fields}</SaveInvoinceResult>""");

        Assert.Equal(expected, PttAvmInvoiceMapper.Outcome(result));
    }

    [Fact]
    public void TheOrderLinesTellWhetherAnInvoiceIsHeld()
    {
        // Lines without the flag answer nothing; the cancelled line never counts.
        Assert.Null(PttAvmInvoiceMapper.Invoiced(OrderFromList("PTT-A1712HP2E-071024")));
        Assert.False(PttAvmInvoiceMapper.Invoiced(WithLiveLineInvoiceFlag("false")));
        Assert.True(PttAvmInvoiceMapper.Invoiced(WithLiveLineInvoiceFlag("true")));
    }

    private static RemoteOrder WithLiveLineInvoiceFlag(string flag)
    {
        var answer = OrderListAnswer.Replace("<a:IndirimPttavm>2.71</a:IndirimPttavm>", $"<a:IndirimPttavm>2.71</a:IndirimPttavm><a:IsInvoice>{flag}</a:IsInvoice>", StringComparison.Ordinal);
        var (result, _) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, answer, PttAvmHttpClient.Orders);
        return PttAvmOrderMapper.Page(result!).Items.Single(x => x.ExternalOrderId == "PTT-A1712HP2E-071024");
    }

    [Fact]
    public void APriceStockPayloadCarriesBarcodeVatPriceAndClampedStock()
    {
        var line = new PriceInventoryPushLine(Guid.NewGuid(), Guid.NewGuid(), "tshirt-1072", 12000m, 149.999m, 149.999m, "TRY", 3, 2, "hash");
        var payload = PttAvmPriceInventoryRules.Payload([(line, 20m), (line with { Barcode = "tshirt-1075", Quantity = -4 }, 18m)]);

        var items = PttAvmPriceInventoryRules.Items(payload)!;

        Assert.Equal(2, items.Count);
        Assert.Equal(new PttAvmPriceInventoryRules.Item("tshirt-1072", 150.00m, 20m, 9999), items[0]);
        // 18 % is not a PTT AVM rate: the VAT rate is left out and PTT AVM keeps its own.
        Assert.Equal(new PttAvmPriceInventoryRules.Item("tshirt-1075", 150.00m, null, 0), items[1]);
        var envelope = PttAvmHttpClient.SoapEnvelope(PttAvmHttpClient.StockPriceUpdate, Account(), Now, [new("items", PttAvmPriceInventoryRules.ItemsXml(items), Raw: true)]);
        XNamespace tem = PttAvmHttpClient.ServiceNamespace, ept = PttAvmHttpClient.RequestsNamespace;
        var requests = XDocument.Parse(envelope).Descendants(ept + "ProductStockPriceRequest").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(["Barcode", "PriceWithVAT", "Quantity", "VATRate"], requests[0].Elements().Select(x => x.Name.LocalName));
        Assert.Equal("150.00", requests[0].Element(ept + "PriceWithVAT")!.Value);
        Assert.Equal("20", requests[0].Element(ept + "VATRate")!.Value);
        Assert.Null(requests[1].Element(ept + "VATRate"));
        Assert.Single(XDocument.Parse(envelope).Descendants(tem + "items"));
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"barcode":"a","priceWithVat":1,"quantity":1}]}""")]
    [InlineData("""{"items":[{"barcode":"a","priceWithVat":10,"quantity":10000}]}""")]
    [InlineData("""{"items":[{"barcode":"a","priceWithVat":10,"quantity":1},{"barcode":"A","priceWithVat":10,"quantity":1}]}""")]
    [InlineData("""{"items":[{"barcode":"a","priceWithVat":10,"quantity":1,"vatRate":18}]}""")]
    public void AnUnacceptablePriceStockPayloadIsRefused(string payload) => Assert.Null(PttAvmPriceInventoryRules.Items(payload));

    // GetProductsTrackingResult as published (three of its five barcodes).
    private const string TrackingAnswer = """
        <GetProductsTrackingResultResult xmlns:a="http://schemas.datacontract.org/2004/07/ePttAVMService.Model.Responses" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
           <a:CreatedAt>2024-10-24T09:46:41.595Z</a:CreatedAt>
           <a:Progress>100</a:Progress>
           <a:Status>COMPLETED</a:Status>
           <a:TrackingId>671a178183f9c51388bbb169</a:TrackingId>
           <a:ProductsSubTrackingResult>
              <a:ProductBasedInfos>
                 <a:ProductBasedInfo>
                    <a:FailureReasons xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"><b:string>Ürünün KDV tanımı 0, 1, 10, 20 değerleri olabilir.</b:string></a:FailureReasons>
                    <a:Message>Ürün güncellemesi başarısız.</a:Message><a:Status>CANCELLED</a:Status><a:Barcode>tshirt-1071</a:Barcode><a:ProductId>744445042</a:ProductId>
                 </a:ProductBasedInfo>
                 <a:ProductBasedInfo>
                    <a:FailureReasons xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"/>
                    <a:Message>Ürün bilgilerinde herhangi bir değişiklik tespit edilemedi.</a:Message><a:Status>CANCELLED</a:Status><a:Barcode>tshirt-1073</a:Barcode><a:ProductId>744445044</a:ProductId>
                 </a:ProductBasedInfo>
                 <a:ProductBasedInfo>
                    <a:FailureReasons xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"><b:string>Fiyat kilidinden dolayı ürünün fiyatı güncellenemedi.</b:string></a:FailureReasons>
                    <a:Message>Ürün güncellendi.</a:Message><a:Status>COMPLETED</a:Status><a:Barcode>tshirt-1075</a:Barcode>
                 </a:ProductBasedInfo>
                 <a:ProductBasedInfo>
                    <a:FailureReasons xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"/>
                    <a:Message>Ürün güncellendi.</a:Message><a:Status>COMPLETED</a:Status><a:Barcode>tshirt-1076</a:Barcode>
                 </a:ProductBasedInfo>
              </a:ProductBasedInfos>
           </a:ProductsSubTrackingResult>
        </GetProductsTrackingResultResult>
        """;

    [Fact]
    public void TrackingResultsSayWhichBarcodesTookTheirPriceAndStock()
    {
        var status = PttAvmPriceInventoryRules.Tracking("t:671a", XElement.Parse(TrackingAnswer))!;

        Assert.Equal("COMPLETED", status.Status);
        var lines = status.Lines.ToDictionary(x => x.ExternalKey);
        Assert.False(lines["tshirt-1071"].Succeeded);
        Assert.Equal("PTTAVM_PRODUCT_UPDATE_CANCELLED", lines["tshirt-1071"].ErrorCode);
        Assert.Contains("KDV tanımı", lines["tshirt-1071"].Status);
        // Nothing to change counts as done.
        Assert.True(lines["tshirt-1073"].Succeeded);
        // Completed with a lock reason did not take everything.
        Assert.Equal("PTTAVM_PRODUCT_UPDATE_PARTIAL", lines["tshirt-1075"].ErrorCode);
        Assert.True(lines["tshirt-1076"].Succeeded);
        Assert.Equal("744445042", lines["tshirt-1071"].ExternalId);
    }

    [Fact]
    public void ATrackingStillWaitingIsInProgress()
    {
        var waiting = TrackingAnswer.Replace("<a:Status>COMPLETED</a:Status>\n           <a:TrackingId>", "<a:Status>InProgress</a:Status>\n           <a:TrackingId>", StringComparison.Ordinal)
            .Replace("<a:Status>COMPLETED</a:Status><a:Barcode>tshirt-1076</a:Barcode>", "<a:Status>Waiting</a:Status><a:Barcode>tshirt-1076</a:Barcode>", StringComparison.Ordinal);

        Assert.Equal("IN_PROGRESS", PttAvmPriceInventoryRules.Tracking("t:671a", XElement.Parse(waiting))!.Status);
    }

    [Fact]
    public void NoPriceOrStockIsSentWhileExternalWritesAreOff() =>
        Assert.Equal("PTTAVM_EXTERNAL_WRITES_DISABLED", Client(new CapturingHandler(_ => throw new InvalidOperationException())).WriteBlocked<RemoteOperationRef>(Account(status: "VERIFIED", connectionWrites: false), "fiyat-stok")!.Error!.Code);

    private static RemoteOrder OrderFromList(string orderNumber)
    {
        var (result, error) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, OrderListAnswer, PttAvmHttpClient.Orders);
        Assert.Null(error);
        var page = PttAvmOrderMapper.Page(result!);
        Assert.Empty(page.Issues ?? []);
        return page.Items.Single(x => x.ExternalOrderId == orderNumber);
    }

    private static string Operation(HttpRequestMessage request) =>
        request.Headers.GetValues("SOAPAction").Single().Trim('"')[PttAvmHttpClient.SoapActionPrefix.Length..];

    private static PttAvmHttpClient Client(CapturingHandler handler, bool globalWrites = false, ILogger<PttAvmHttpClient>? logger = null) => new(
        new SingleClientFactory(handler), null!, Options.Create(new PttAvmOptions()), new FixedClock(Now), logger ?? new CapturingLogger(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = globalWrites ? "true" : "false" }).Build());

    private static PttAvmRequestContext Account(string environment = "PRODUCTION", string status = "DRAFT", bool connectionWrites = false, string storeId = StoreId) => new(
        new PlatformConnection
        {
            Id = Guid.NewGuid(),
            PlatformCode = "PTTAVM",
            Environment = environment,
            Status = status,
            DisplayName = "PTT AVM",
            ExternalStoreId = storeId,
            ApiVersion = "V1",
            SettingsJson = connectionWrites ? """{"ExternalWritesEnabled":true}""" : """{"ExternalWritesEnabled":false}"""
        },
        Username, Password);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, body.TrimStart().StartsWith("<!DOCTYPE", StringComparison.Ordinal) ? "text/html" : "text/xml") };

    private sealed record CapturedRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? ContentType, string? Body);

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.ToString(), headers, request.Content?.Headers.ContentType?.ToString(), body));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("PttAvm", name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger : ILogger<PttAvmHttpClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + " " + state);
    }
}
