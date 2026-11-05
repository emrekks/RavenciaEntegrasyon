using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// The finance mappers' shared wording for a record they leave out (no id, no readable main amount or no date), so the
// finance jobs can name it in an issue (FinanceRecordIssues) instead of storing a made-up 0 or "now".
internal static class SkippedFinanceRecords
{
    // A record without an id is known by a hash of its text.
    internal static string KeyOf(string? id, string raw) =>
        string.IsNullOrWhiteSpace(id) ? "#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16] : id.Trim();

    // "kayıt 123 (sipariş 456, Payment): tutarı ve tarihi yok ya da okunamadı".
    internal static RemoteSkippedFinanceRecord Skip(string? id, string raw, string? orderNumber, string? type, bool noId, bool noAmount, bool noDate)
    {
        var lacks = new List<string>();
        if (noId) lacks.Add("kimliği");
        if (noAmount) lacks.Add("tutarı");
        if (noDate) lacks.Add("tarihi");
        var what = lacks.Count <= 1 ? lacks.FirstOrDefault() ?? "bilgisi" : string.Join(", ", lacks.Take(lacks.Count - 1)) + " ve " + lacks[^1];
        var details = new[] { string.IsNullOrWhiteSpace(orderNumber) ? null : $"sipariş {orderNumber.Trim()}", type?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var name = (string.IsNullOrWhiteSpace(id) ? "kimliksiz kayıt" : $"kayıt {id.Trim()}") + (details.Count == 0 ? "" : $" ({string.Join(", ", details)})");
        return new(KeyOf(id, raw), $"{name}: {what} yok ya da okunamadı");
    }
}
