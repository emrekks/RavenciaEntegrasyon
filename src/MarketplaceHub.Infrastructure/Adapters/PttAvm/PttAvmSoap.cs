using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// SOAP 1.1 client for the PTT AVM web service (https://ws.pttavm.com:93/service.svc, WCF binding
// WsSecured_IService). The envelope follows the WSDL and the samples on developers.pttavm.com:
// document/literal, target namespace http://tempuri.org/ with qualified child elements (prefix tem),
// SOAPAction http://tempuri.org/IService/<Operation>, and a WS-Security header with a Timestamp and a
// UsernameToken (PasswordText) over HTTPS. A null value is always written as i:nil="true": WCF answers a
// malformed element with the same "error ... verifying security" fault it gives for a wrong password.
public sealed partial class PttAvmHttpClient
{
    internal const string ServiceNamespace = "http://tempuri.org/";
    internal const string SoapActionPrefix = "http://tempuri.org/IService/";
    internal const string EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    internal const string SecurityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    internal const string UtilityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    internal const string PasswordTextType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";
    internal const string InstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    internal const string ContractNamespace = "http://schemas.datacontract.org/2004/07/ePttAVMService";
    internal const string ArrayNamespace = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
    // The service rejects a request whose Timestamp has expired; five minutes is what its own answers carry.
    internal static readonly TimeSpan TimestampLifetime = TimeSpan.FromMinutes(5);

    // One request parameter: <tem:Name>value</tem:Name>, or <tem:Name i:nil="true"/> for null. A raw parameter carries
    // markup already built (an arr:int list), which goes in unescaped.
    internal readonly record struct SoapParameter(string Name, string? Value, bool Raw = false);

    internal static string SoapEnvelope(string operation, PttAvmRequestContext account, DateTimeOffset now, IReadOnlyList<SoapParameter> parameters)
    {
        var created = now.ToUniversalTime();
        var body = new StringBuilder();
        foreach (var parameter in parameters) body.Append(parameter.Raw ? $"<tem:{parameter.Name}>{parameter.Value}</tem:{parameter.Name}>" : SoapElement(parameter.Name, parameter.Value));
        var operationElement = body.Length == 0 ? $"<tem:{operation}/>" : $"<tem:{operation}>{body}</tem:{operation}>";
        return $"""<soapenv:Envelope xmlns:soapenv="{EnvelopeNamespace}" xmlns:tem="{ServiceNamespace}" xmlns:i="{InstanceNamespace}" xmlns:arr="{ArrayNamespace}"><soapenv:Header><wsse:Security soapenv:mustUnderstand="1" xmlns:wsse="{SecurityNamespace}" xmlns:wsu="{UtilityNamespace}"><wsu:Timestamp wsu:Id="TS-{Guid.NewGuid():N}"><wsu:Created>{SoapTime(created)}</wsu:Created><wsu:Expires>{SoapTime(created + TimestampLifetime)}</wsu:Expires></wsu:Timestamp><wsse:UsernameToken wsu:Id="UsernameToken-{Guid.NewGuid():N}"><wsse:Username>{SecurityElement.Escape(account.Username)}</wsse:Username><wsse:Password Type="{PasswordTextType}">{SecurityElement.Escape(account.Password)}</wsse:Password></wsse:UsernameToken></wsse:Security></soapenv:Header><soapenv:Body>{operationElement}</soapenv:Body></soapenv:Envelope>""";
    }

    internal static string SoapElement(string name, string? value) =>
        value is null ? $"""<tem:{name} i:nil="true"/>""" : $"<tem:{name}>{SecurityElement.Escape(value)}</tem:{name}>";

    internal static string SoapTime(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static HttpRequestMessage BuildSoapRequest(PttAvmRequestContext account, Uri address, string operation, DateTimeOffset now, IReadOnlyList<SoapParameter> parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(SoapEnvelope(operation, account, now, parameters), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{SoapActionPrefix}{operation}\"");
        request.Options.Set(PttAvmRateLimitHandler.AccountKeyOption, PttAvmRateLimitHandler.AccountKey(account.Username));
        request.Options.Set(PttAvmRateLimitHandler.OperationKey, operation);
        return request;
    }

    // Returns the <{operation}Result> element (it may carry i:nil="true") when PTT AVM answered without a fault.
    private async Task<AdapterResult<XElement>> SendSoapAsync(PttAvmRequestContext account, string operation, IReadOnlyList<SoapParameter> parameters, CancellationToken cancellationToken)
    {
        using var request = BuildSoapRequest(account, settings.SoapBaseAddress, operation, timeProvider.GetUtcNow(), parameters);
        // The request timeout starts when the quota token is taken; the wait for it has its own, longer cap.
        using var timeout = new QuotaAwareTimeout(request, PttAvmRateLimitHandler.QuotaAcquiredKey, RequestTimeout, cancellationToken);
        try
        {
            using var response = await clients.CreateBoundedClient(ClientName).SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
            var parsed = SoapResponse(response.StatusCode, body, operation, retryAfter);
            if (parsed.Error is { } error)
            {
                // The outline of the answer (never its values, never the request) tells a gateway page from a SOAP fault.
                logger.LogWarning("PTT AVM SOAP isteği reddedildi. ConnectionId: {ConnectionId}, Operation: {Operation}, Status: {Status}, Code: {Code}, Message: {Message}, Body: {BodyShape} ({ContentType}, {BodyLength} bayt)",
                    account.Connection.Id, operation, (int)response.StatusCode, error.Code, error.SafeMessage, BodyShape(body), response.Content.Headers.ContentType?.MediaType ?? "içerik tipi yok", body.Length);
                return AdapterResult<XElement>.Failure(error, error.RetryAfter is null ? null : new RateLimitMetadata(null, null, error.RetryAfter));
            }
            return AdapterResult<XElement>.Success(parsed.Result!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Never sent: the quota stayed full. This is not an "outcome unknown" write.
            if (!timeout.QuotaAcquired)
                return AdapterResult<XElement>.Failure(new(AdapterErrorClass.RateLimit, "PTTAVM_LOCAL_QUOTA_WAIT", "PTT AVM istek kotası dolu olduğu için istek gönderilmedi; yeniden denenecek.", 429, TimeSpan.FromSeconds(15), null));
            return AdapterResult<XElement>.Failure(new(AdapterErrorClass.TransientNetwork, "PTTAVM_TIMEOUT", "PTT AVM isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "PTT AVM SOAP isteği başarısız. ConnectionId: {ConnectionId}, Operation: {Operation}", account.Connection.Id, operation);
            return AdapterResult<XElement>.Failure(new(AdapterErrorClass.TransientNetwork, "PTTAVM_NETWORK_ERROR", "PTT AVM bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    internal static (XElement? Result, AdapterError? Error) SoapResponse(HttpStatusCode status, string body, string operation, TimeSpan? retryAfter = null)
    {
        // A page from the security gateway in front of the service never reached it; this comes and goes.
        if (LooksLikeHtml(body))
            return (null, new(AdapterErrorClass.RateLimit, "PTTAVM_GATEWAY_BLOCKED",
                "PTT AVM'nin güvenlik geçidi isteği engelledi (servis yerine HTML sayfa döndü). Geçici olabilir; 15 dakika sonra yeniden denenir.", (int)status, TimeSpan.FromMinutes(15), null));
        if (status == HttpStatusCode.TooManyRequests)
            return (null, new(AdapterErrorClass.RateLimit, "PTTAVM_RATE_LIMITED", "PTT AVM istek sınırına ulaşıldı; işlem biraz sonra yeniden denenecek.", 429, retryAfter ?? TimeSpan.FromSeconds(60), null));
        XDocument? document = null;
        try { if (!string.IsNullOrWhiteSpace(body)) document = XDocument.Parse(body); }
        catch (XmlException) { }
        // WCF answers a fault with HTTP 500; the fault decides, not the status.
        if (document?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fault" && x.Name.NamespaceName == EnvelopeNamespace) is { } fault)
            return (null, FaultError(fault, (int)status));
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (null, new(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", PttAvmErrorAdvice.CredentialsRejected, (int)status, null, null));
        if (status == HttpStatusCode.RequestTimeout)
            return (null, new(AdapterErrorClass.TransientNetwork, "PTTAVM_REMOTE_TIMEOUT", "PTT AVM isteği kendi tarafında zaman aşımına uğrattı; işlem yeniden denenecek.", 408, retryAfter ?? TimeSpan.FromSeconds(15), null));
        if ((int)status >= 500)
            return (null, new(AdapterErrorClass.Remote5xx, "PTTAVM_REMOTE_ERROR", "PTT AVM servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(30), null));
        if ((int)status >= 400)
            return (null, new(AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", "PTT AVM isteği reddetti.", (int)status, null, null));
        if (document is null)
            return (null, new(AdapterErrorClass.ContractViolation, "PTTAVM_SOAP_INVALID", "PTT AVM geçerli bir SOAP yanıtı vermedi.", (int)status, null, null));
        var response = document.Descendants().FirstOrDefault(x => x.Name == XName.Get(operation + "Response", ServiceNamespace));
        var result = response?.Elements().FirstOrDefault(x => x.Name == XName.Get(operation + "Result", ServiceNamespace));
        if (response is null)
            return (null, new(AdapterErrorClass.ContractViolation, "PTTAVM_SOAP_CONTRACT_INVALID", $"PTT AVM {operation} yanıtı beklenen yapıda değil (yanıt öğeleri: {ElementShape(document.Root)}).", (int)status, null, null));
        // The result element is optional (minOccurs=0); an absent one reads as nil.
        return (result ?? new XElement(XName.Get(operation + "Result", ServiceNamespace), new XAttribute(XName.Get("nil", InstanceNamespace), "true")), null);
    }

    // WCF security faults: a wrong user name or password, an expired Timestamp and a malformed request all come
    // back as "An error occurred when verifying security for the message" (code InvalidSecurity or
    // FailedAuthentication). Our envelopes always send i:nil for nulls, so here it means the credentials.
    internal static AdapterError FaultError(XElement fault, int? status)
    {
        // SOAP 1.1 carries faultcode/faultstring; a SOAP 1.2 style Code/Value and Reason/Text are read too.
        var codes = string.Join(" ", fault.Descendants().Where(x => x.Name.LocalName is "faultcode" or "Value").Select(x => x.Value.Trim()));
        var reason = fault.Descendants().FirstOrDefault(x => x.Name.LocalName is "faultstring" or "Text")?.Value.Trim();
        if (IsSecurityFault(codes, reason))
            return new(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", PttAvmErrorAdvice.CredentialsRejected, status, null, null);
        if (PttAvmProductPayloadRules.IsRepeatRequestRefusal(reason)) return PttAvmProductPayloadRules.RepeatRequestError(status);
        var message = Clip(reason);
        return new(AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED",
            message is null ? "PTT AVM isteği reddetti (SOAP hatası)." : $"PTT AVM isteği reddetti: {PttAvmErrorAdvice.WithAdvice(message)}", status, null, null);
    }

    internal static bool IsSecurityFault(string codes, string? reason) =>
        codes.Contains("InvalidSecurity", StringComparison.OrdinalIgnoreCase)
        || codes.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase)
        || codes.Contains("SecurityTokenUnavailable", StringComparison.OrdinalIgnoreCase)
        || (reason?.Contains("verifying security", StringComparison.OrdinalIgnoreCase) ?? false)
        || (reason?.Contains("authenticat", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static bool IsNil(XElement element) =>
        string.Equals(element.Attribute(XName.Get("nil", InstanceNamespace))?.Value, "true", StringComparison.OrdinalIgnoreCase);

    // A data-contract child (namespace ePttAVMService) of a result element; null when absent, nil or empty.
    internal static string? ContractValue(XElement? element, string name)
    {
        var child = element?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        if (child is null || IsNil(child)) return null;
        var value = child.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    internal static IEnumerable<XElement> ContractItems(XElement result, string itemName) =>
        IsNil(result) ? [] : result.Elements().Where(x => x.Name.LocalName == itemName);

    internal static bool LooksLikeHtml(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        return trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    // Element names of an answer that did not have the expected shape (never the values).
    internal static string ElementShape(XElement? root)
    {
        if (root is null) return "boş";
        var names = root.Descendants().Select(x => x.Name.LocalName).Distinct().Take(12).ToList();
        return names.Count == 0 ? root.Name.LocalName : string.Join(", ", names);
    }

    internal static string BodyShape(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "boş";
        if (LooksLikeHtml(body)) return "HTML";
        try { return ElementShape(XDocument.Parse(body).Root); }
        catch (XmlException) { return "XML değil"; }
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
