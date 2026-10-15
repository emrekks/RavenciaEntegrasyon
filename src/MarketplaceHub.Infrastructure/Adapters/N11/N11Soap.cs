using System.Net;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Minimal SOAP 1.1 client for the n11 services that have no REST form (invoice link, returns,
// questions). Envelopes follow the public WSDLs at https://api.n11.com/ws/*.wsdl: document/literal,
// namespace http://www.n11.com/ws/schemas, unqualified child elements and an <auth> block.
public sealed partial class N11HttpClient
{
    internal const string SoapNamespace = "http://www.n11.com/ws/schemas";

    internal static string SoapEnvelope(string operation, N11RequestContext account, string innerXml) =>
        $"""<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:sch="{SoapNamespace}"><soapenv:Header/><soapenv:Body><sch:{operation}Request><auth><appKey>{SecurityElement.Escape(account.AppKey)}</appKey><appSecret>{SecurityElement.Escape(account.AppSecret)}</appSecret></auth>{innerXml}</sch:{operation}Request></soapenv:Body></soapenv:Envelope>""";

    internal static string SoapElement(string name, string? value) =>
        $"<{name}>{SecurityElement.Escape(value ?? "")}</{name}>";

    // Returns the <{operation}Response> element when n11 answered result/status success.
    private async Task<AdapterResult<XElement>> SendSoapAsync(N11RequestContext account, string servicePath, string operation, string innerXml, CancellationToken cancellationToken, bool requireResult = true, string? rateBucket = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseAddress, servicePath))
        {
            Content = new StringContent(SoapEnvelope(operation, account, innerXml), System.Text.Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
        request.Options.Set(N11RateLimitHandler.AccountKeyOption, account.AppKey);
        if (rateBucket is not null) request.Options.Set(N11RateLimitHandler.BucketKey, rateBucket);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // A named slow bucket (1/min) may queue the call for up to a minute before it is sent.
            timeout.CancelAfter((settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30)) + (rateBucket is null ? TimeSpan.Zero : TimeSpan.FromSeconds(75)));
            using var response = await clients.CreateClient("N11").SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return AdapterResult<XElement>.Failure(new(AdapterErrorClass.RateLimit, "N11_RATE_LIMITED", "N11 istek sınırına ulaşıldı; işlem biraz sonra yeniden denenecek.", 429, response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60), null));
            if ((int)response.StatusCode >= 500 && !body.Contains(operation + "Response", StringComparison.Ordinal))
                return AdapterResult<XElement>.Failure(new(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR", "N11 servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)response.StatusCode, TimeSpan.FromSeconds(30), null));
            var parsed = SoapResponse(body, operation, requireResult);
            if (parsed.Error is { } error)
            {
                logger.LogWarning("N11 SOAP isteği reddedildi. ConnectionId: {ConnectionId}, Operation: {Operation}, Code: {Code}, Message: {Message}", account.Connection.Id, operation, error.Code, error.SafeMessage);
                return AdapterResult<XElement>.Failure(error);
            }
            return AdapterResult<XElement>.Success(parsed.Response!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdapterResult<XElement>.Failure(new(AdapterErrorClass.TransientNetwork, "N11_TIMEOUT", "N11 isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "N11 SOAP isteği başarısız. ConnectionId: {ConnectionId}, Operation: {Operation}", account.Connection.Id, operation);
            return AdapterResult<XElement>.Failure(new(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR", "N11 bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    internal static (XElement? Response, AdapterError? Error) SoapResponse(string body, string operation, bool requireResult = true)
    {
        XDocument document;
        try { document = XDocument.Parse(body); }
        catch (XmlException) { return (null, new(AdapterErrorClass.ContractViolation, "N11_SOAP_INVALID", "N11 geçerli bir SOAP yanıtı vermedi.", null, null, null)); }
        var fault = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fault");
        if (fault is not null)
        {
            var reason = fault.Descendants().FirstOrDefault(x => x.Name.LocalName == "faultstring")?.Value.Trim();
            return (null, new(AdapterErrorClass.Validation, "N11_SOAP_FAULT", $"N11 isteği reddetti: {Trim(reason ?? "SOAP hatası")}", null, null, null));
        }
        var response = document.Descendants().FirstOrDefault(x => x.Name.LocalName == operation + "Response");
        var result = response?.Elements().FirstOrDefault(x => x.Name.LocalName == "result");
        var status = Child(result, "status");
        // Some read operations (question list and detail) answer without a result block.
        if (response is not null && result is null && !requireResult) return (response, null);
        if (response is null || result is null)
            return (null, new(AdapterErrorClass.ContractViolation, "N11_SOAP_CONTRACT_INVALID", $"N11 {operation} yanıtında sonuç bilgisi yok.", null, null, null));
        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)) return (response, null);
        var errorCode = Child(result, "errorCode") ?? "";
        var message = Child(result, "errorMessage") ?? errorCode;
        var authentication = errorCode.Contains("authentication", StringComparison.OrdinalIgnoreCase);
        return (null, new(authentication ? AdapterErrorClass.Authentication : AdapterErrorClass.Validation,
            authentication ? "N11_CREDENTIALS_REJECTED" : "N11_REQUEST_REJECTED",
            string.IsNullOrWhiteSpace(message) ? "N11 isteği reddetti." : $"N11 isteği reddetti: {Trim(message)}", null, null, null));
    }

    internal static string? Child(XElement? element, string name)
    {
        var value = element?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
