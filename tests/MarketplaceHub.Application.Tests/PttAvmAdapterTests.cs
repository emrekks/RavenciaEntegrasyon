using System.Net;
using System.Text;
using System.Xml.Linq;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
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
    public void PttAvmConnectionsCarryOnlyThePhaseOneReadCapabilities() =>
        Assert.Equal(
            [MarketplaceCapabilities.ConnectionTest, MarketplaceCapabilities.OrderRead, MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.ReferenceRead],
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
    public async Task ANonNumericStoreIdIsRefusedBeforeAnyCall()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException());

        var result = await Client(handler).TestAsync(Account(storeId: "Ravencia"), CancellationToken.None);

        Assert.Equal("PTTAVM_STORE_ID_INVALID", result.Error!.Code);
        Assert.Empty(handler.Requests);
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
