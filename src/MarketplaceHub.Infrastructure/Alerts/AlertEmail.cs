using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Infrastructure.Alerts;

// Alert e-mail (security plan Y1): the operator's Gmail account sends the panel's and the host's alerts.
// The same three values live in two places, kept in step by deploy/scripts/alert-settings-sync.sh:
//   - the panel (Settings → Alarm Bildirimleri), per tenant in tenant_settings "alert-email", the app password
//     encrypted with Data Protection; the source of truth once it is set;
//   - the root-only host file deploy/secrets/alert_smtp.txt that send-alert.sh falls back to when the API is down.

public sealed record AlertEmailCredentials(string SenderAddress, IReadOnlyList<string> Recipients, string AppPassword);

public sealed class AlertEmailException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class AlertEmailRules
{
    public const int MaximumRecipients = 5;
    public const string TestMessage = "Ravencia alarm testi — e-posta kanalı çalışıyor";

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^[a-z]{16}$")]
    private static partial Regex AppPasswordPattern();

    public static string NormalizeAddress(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsAddress(string value) => value.Length <= 254 && AddressPattern().IsMatch(value);

    // Gmail sends from the account itself: a non-Gmail sender would be rewritten or land in spam (SPF/DMARC).
    public static bool IsGmailAddress(string value) => IsAddress(value) && (value.EndsWith("@gmail.com", StringComparison.Ordinal) || value.EndsWith("@googlemail.com", StringComparison.Ordinal));

    // Google shows the app password as four groups of four letters; spaces are dropped.
    public static string NormalizeAppPassword(string? value) => new string((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)).ToArray()).ToLowerInvariant();

    public static bool IsAppPassword(string value) => AppPasswordPattern().IsMatch(value);

    // Recipients from a list or one "a@x, b@y" line; empty entries dropped, duplicates removed, order kept.
    public static List<string> NormalizeRecipients(IEnumerable<string?>? values) =>
        (values ?? [])
            .SelectMany(value => (value ?? string.Empty).Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeAddress)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // Empty when valid, otherwise a message for the person.
    public static string? Validate(string senderAddress, IReadOnlyList<string> recipients)
    {
        if (!IsGmailAddress(senderAddress)) return "Gönderen adres bir Gmail adresi olmalıdır (ör. ravencia.alarm@gmail.com).";
        if (recipients.Count == 0) return "En az bir alıcı adresi girin.";
        if (recipients.Count > MaximumRecipients) return $"En fazla {MaximumRecipients} alıcı girilebilir.";
        var invalid = recipients.FirstOrDefault(recipient => !IsAddress(recipient));
        return invalid is null ? null : $"Geçersiz alıcı adresi: {invalid}";
    }

    // The host file's three lines (sender, recipients separated by commas, app password).
    public static string ToHostFile(AlertEmailCredentials credentials) =>
        $"{credentials.SenderAddress}\n{string.Join(',', credentials.Recipients)}\n{credentials.AppPassword}\n";

    public static AlertEmailCredentials? FromHostFile(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3) return null;
        var sender = NormalizeAddress(lines[0]);
        var recipients = NormalizeRecipients([lines[1]]);
        var password = NormalizeAppPassword(lines[2]);
        return Validate(sender, recipients) is null && password.Length > 0 ? new AlertEmailCredentials(sender, recipients, password) : null;
    }

    // RFC 5322 message, as send-alert.sh writes it: UTF-8 text body, subject "[Ravencia] <first 80 characters>".
    public static string BuildMessage(string senderAddress, IReadOnlyList<string> recipients, string message, DateTimeOffset now, string host)
    {
        var clean = new string(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        var subject = $"[Ravencia] {(clean.Length > 80 ? clean[..80] : clean)}";
        var builder = new StringBuilder();
        builder.Append("From: Ravencia <").Append(senderAddress).Append(">\r\n");
        builder.Append("To: ").Append(string.Join(", ", recipients.Select(recipient => $"<{recipient}>"))).Append("\r\n");
        builder.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))).Append("?=\r\n");
        builder.Append("Date: ").Append(now.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Message-ID: <").Append(Guid.CreateVersion7().ToString("N")).Append(".ravencia@").Append(host).Append(">\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        var body = $"{message}\n\n-- \n{host}, {now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body.Replace("\n", "\r\n", StringComparison.Ordinal)));
        for (var index = 0; index < encoded.Length; index += 76)
            builder.Append(encoded, index, Math.Min(76, encoded.Length - index)).Append("\r\n");
        return builder.ToString();
    }
}

public interface IAlertEmailSender
{
    Task SendAsync(AlertEmailCredentials credentials, string message, CancellationToken cancellationToken);
}

// Gmail over implicit TLS on 465, the same server and port as the host's curl (send-alert.sh). A small SMTP
// dialogue (EHLO, AUTH PLAIN, MAIL, RCPT, DATA) instead of System.Net.Mail, which only speaks STARTTLS.
// Failures surface as AlertEmailException with Gmail's reply code; the password never appears in a message.
public sealed class GmailSmtpAlertSender(TimeProvider timeProvider) : IAlertEmailSender
{
    public const string Host = "smtp.gmail.com";
    public const int Port = 465;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(AlertEmailCredentials credentials, string message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, timeout.Token);
            var body = AlertEmailRules.BuildMessage(credentials.SenderAddress, credentials.Recipients, message, timeProvider.GetUtcNow(), "panel");
            await SmtpDialogue.RunAsync(tls, credentials, body, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlertEmailException("ALERT_EMAIL_TIMEOUT", "Gmail SMTP sunucusu 20 saniyede yanıt vermedi.");
        }
        catch (SocketException)
        {
            throw new AlertEmailException("ALERT_EMAIL_UNREACHABLE", "Gmail SMTP sunucusuna (smtp.gmail.com:465) bağlanılamadı.");
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            throw new AlertEmailException("ALERT_EMAIL_TLS_FAILED", "Gmail SMTP sunucusuyla güvenli bağlantı kurulamadı.");
        }
        catch (IOException)
        {
            throw new AlertEmailException("ALERT_EMAIL_CONNECTION_LOST", "Gmail SMTP bağlantısı yarıda kesildi.");
        }
    }
}

public static class SmtpDialogue
{
    public static async Task RunAsync(Stream stream, AlertEmailCredentials credentials, string message, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await Expect(reader, 220, "ALERT_EMAIL_REJECTED", cancellationToken);
        await Command(stream, reader, "EHLO ravencia", 250, "ALERT_EMAIL_REJECTED", cancellationToken);
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{credentials.SenderAddress}\0{credentials.AppPassword}"));
        await Command(stream, reader, $"AUTH PLAIN {plain}", 235, "ALERT_EMAIL_AUTH_FAILED", cancellationToken, logLine: "AUTH PLAIN");
        await Command(stream, reader, $"MAIL FROM:<{credentials.SenderAddress}>", 250, "ALERT_EMAIL_REJECTED", cancellationToken);
        foreach (var recipient in credentials.Recipients)
            await Command(stream, reader, $"RCPT TO:<{recipient}>", 250, "ALERT_EMAIL_RECIPIENT_REJECTED", cancellationToken, alsoAccepted: 251);
        await Command(stream, reader, "DATA", 354, "ALERT_EMAIL_REJECTED", cancellationToken);
        // Dot-stuffing: a line that starts with "." gets a second one.
        var stuffed = string.Join("\r\n", message.TrimEnd('\r', '\n').Split("\r\n").Select(line => line.StartsWith('.') ? "." + line : line));
        await Command(stream, reader, stuffed + "\r\n.", 250, "ALERT_EMAIL_REJECTED", cancellationToken);
        await Write(stream, "QUIT", cancellationToken);
    }

    private static async Task Command(Stream stream, StreamReader reader, string line, int expected, string code, CancellationToken cancellationToken, int? alsoAccepted = null, string? logLine = null)
    {
        await Write(stream, line, cancellationToken);
        await Expect(reader, expected, code, cancellationToken, alsoAccepted, logLine ?? line.Split(' ', 2)[0]);
    }

    private static async Task Write(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    // Reads one (possibly multi-line, "250-...") reply. The reply text is Gmail's, never the password.
    private static async Task Expect(StreamReader reader, int expected, string code, CancellationToken cancellationToken, int? alsoAccepted = null, string step = "connect")
    {
        string? line;
        var text = new StringBuilder();
        do
        {
            line = await reader.ReadLineAsync(cancellationToken) ?? throw new AlertEmailException("ALERT_EMAIL_CONNECTION_LOST", "Gmail SMTP bağlantısı yarıda kesildi.");
            text.Append(line.Length > 4 ? line[4..] : string.Empty).Append(' ');
        }
        while (line.Length > 3 && line[3] == '-');
        if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new AlertEmailException("ALERT_EMAIL_REJECTED", "Gmail SMTP sunucusundan beklenmeyen yanıt alındı.");
        if (status == expected || status == alsoAccepted) return;
        var reply = text.ToString().Trim();
        if (reply.Length > 200) reply = reply[..200];
        var message = code switch
        {
            "ALERT_EMAIL_AUTH_FAILED" => $"Gmail giriş bilgilerini kabul etmedi ({status}). Gönderen adresi ve uygulama şifresini kontrol edin; hesapta iki adımlı doğrulama açık olmalıdır.",
            "ALERT_EMAIL_RECIPIENT_REJECTED" => $"Gmail bir alıcı adresini kabul etmedi ({status}): {reply}",
            _ => $"Gmail SMTP adımı {step} reddedildi ({status}): {reply}"
        };
        throw new AlertEmailException(code, message);
    }
}
