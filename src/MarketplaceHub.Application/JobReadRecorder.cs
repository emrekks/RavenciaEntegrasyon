using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Application;

// One record a read job pulled: its reference (order, package or claim number), a second number (the claim's order),
// the marketplace status it came with, what the read did with it and why it was skipped or failed. Never a customer's
// name, address or phone.
public sealed record JobReadRecord(
    [property: JsonPropertyName("r")] string Reference,
    [property: JsonPropertyName("s")] string? Secondary,
    [property: JsonPropertyName("st")] string? Status,
    [property: JsonPropertyName("o")] string Outcome,
    [property: JsonPropertyName("why")] string? Reason = null);

public static class JobReadOutcomes
{
    public const string New = "NEW";
    public const string Updated = "UPDATED";
    public const string Unchanged = "UNCHANGED";
    public const string Skipped = "SKIPPED";
    public const string Failed = "FAILED";

    // A record read twice in one run keeps its strongest outcome: an error, then new, updated, skipped, unchanged.
    public static int Rank(string outcome) => outcome switch
    {
        Failed => 4,
        New => 3,
        Updated => 2,
        Skipped => 1,
        _ => 0
    };
}

public sealed record JobReadCounts(int Received, int Inserted, int Updated, int Unchanged, int Skipped, int Failed)
{
    public static readonly JobReadCounts Zero = new(0, 0, 0, 0, 0, 0);
    public JobReadCounts Plus(JobReadCounts other) => new(Received + other.Received, Inserted + other.Inserted, Updated + other.Updated,
        Unchanged + other.Unchanged, Skipped + other.Skipped, Failed + other.Failed);
}

public sealed record JobReadSummaryView(string Kind, JobReadCounts Counts, int Omitted, IReadOnlyList<JobReadRecord> Records, string Sentence);

// Collects what one run of a read job pulled. Every record counts once, so the counts always add up
// (Received = Inserted + Updated + Unchanged + Skipped + Failed); the stored list keeps the problems first and is capped.
public sealed class JobReadRecorder
{
    public const int MaxRecords = 100;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Dictionary<string, JobReadRecord> records = new(StringComparer.Ordinal);
    private readonly List<string> order = [];

    public string? Kind { get; private set; }
    public bool IsEmpty => records.Count == 0;

    public void Reset()
    {
        records.Clear();
        order.Clear();
        Kind = null;
    }

    public void Add(string kind, JobReadRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Reference)) return;
        Kind ??= kind;
        var key = $"{kind}:{record.Reference}";
        if (records.TryGetValue(key, out var existing))
        {
            if (JobReadOutcomes.Rank(record.Outcome) > JobReadOutcomes.Rank(existing.Outcome)) records[key] = record;
            return;
        }
        records[key] = record;
        order.Add(key);
    }

    public JobReadCounts Counts() => CountsOf(records.Values);

    public IReadOnlyList<JobReadRecord> Records() => order.Select(x => records[x]).ToList();

    public static JobReadCounts CountsOf(IEnumerable<JobReadRecord> list)
    {
        int inserted = 0, updated = 0, unchanged = 0, skipped = 0, failed = 0;
        foreach (var record in list)
        {
            switch (record.Outcome)
            {
                case JobReadOutcomes.New: inserted++; break;
                case JobReadOutcomes.Updated: updated++; break;
                case JobReadOutcomes.Skipped: skipped++; break;
                case JobReadOutcomes.Failed: failed++; break;
                default: unchanged++; break;
            }
        }
        return new(inserted + updated + unchanged + skipped + failed, inserted, updated, unchanged, skipped, failed);
    }

    // This run's records joined to what earlier runs of the same job stored (the slices of a long read, or a retry after
    // a failure). While every earlier record is still listed, a record read again counts once (its strongest outcome),
    // so a retry does not double the counts; past the cap the counts add. The list keeps the problems first, then new
    // and updated records, and holds at most MaxRecords.
    public (JobReadCounts Counts, IReadOnlyList<JobReadRecord> Kept, int Omitted) MergeWith(JobReadCounts previousCounts, IReadOnlyList<JobReadRecord> previousRecords, int previousOmitted)
    {
        List<JobReadRecord> all;
        JobReadCounts counts;
        if (previousOmitted == 0 && previousRecords.Count == previousCounts.Received)
        {
            var byReference = new Dictionary<string, JobReadRecord>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var record in previousRecords.Concat(Records()))
            {
                if (byReference.TryGetValue(record.Reference, out var existing))
                {
                    if (JobReadOutcomes.Rank(record.Outcome) > JobReadOutcomes.Rank(existing.Outcome)) byReference[record.Reference] = record;
                    continue;
                }
                byReference[record.Reference] = record;
                order.Add(record.Reference);
            }
            all = order.Select(x => byReference[x]).ToList();
            counts = CountsOf(all);
        }
        else
        {
            all = previousRecords.Concat(Records()).ToList();
            counts = previousCounts.Plus(Counts());
        }
        var sorted = all.Select((record, index) => (record, index))
            .OrderByDescending(x => JobReadOutcomes.Rank(x.record.Outcome))
            .ThenBy(x => x.index)
            .Select(x => x.record)
            .ToList();
        var kept = sorted.Take(MaxRecords).ToList();
        return (counts, kept, Math.Max(0, counts.Received - kept.Count));
    }

    public static string Serialize(IReadOnlyList<JobReadRecord> list) => JsonSerializer.Serialize(list, Json);

    public static IReadOnlyList<JobReadRecord> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<JobReadRecord>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}

// The plain sentence a read job's detail opens with, from what it recorded: "Trendyol'dan 4 iade talebi okundu; hepsi
// panelde zaten günceldi."
public static class JobReadSentence
{
    public static string Describe(string kind, string marketplace, JobReadCounts counts)
    {
        var noun = Noun(kind);
        var source = From(marketplace);
        if (counts.Received == 0) return $"{source} okunacak {noun} gelmedi; panelde değişiklik yapılmadı.";
        var head = $"{source} {counts.Received} {noun} okundu";
        if (counts.Unchanged == counts.Received) return $"{head}; {(counts.Received == 1 ? "panelde zaten günceldi" : "hepsi panelde zaten günceldi")}.";
        var parts = new List<string>();
        if (counts.Inserted > 0) parts.Add($"{counts.Inserted} yeni eklendi");
        if (counts.Updated > 0) parts.Add($"{counts.Updated} güncellendi");
        if (counts.Unchanged > 0) parts.Add($"{counts.Unchanged} değişmedi");
        if (counts.Skipped > 0) parts.Add($"{counts.Skipped} atlandı");
        if (counts.Failed > 0) parts.Add($"{counts.Failed} işlenemedi");
        return $"{head}: {string.Join(", ", parts)}.";
    }

    // The detail's heading for a read whose type has no text of its own, in Title Case.
    public static string Title(string kind) => kind switch
    {
        "ORDER" => "Sipariş Okuma",
        "RETURN" => "İade Talebi Okuma",
        "QUESTION" => "Müşteri Sorusu Okuma",
        "INVOICE" => "Paket Fatura Taraması",
        "PRODUCT" => "Ürün Okuma",
        "FINANCE" => "Finans Hareketi Okuma",
        _ => "Kayıt Okuma"
    };

    public static string Noun(string kind) => kind switch
    {
        "ORDER" => "sipariş",
        "RETURN" => "iade talebi",
        "QUESTION" => "müşteri sorusu",
        "INVOICE" => "paketin fatura durumu",
        "PRODUCT" => "ürün",
        "FINANCE" => "finans hareketi",
        _ => "kayıt"
    };

    // Turkish ablative after a marketplace name: Trendyol'dan, N11'den, PTT AVM'den.
    public static string From(string marketplace) => marketplace switch
    {
        "N11" => "N11'den",
        "PttAVM" => "PttAVM'den",
        "E-Faturam" => "E-Faturam'dan",
        "Ravencia" => "Panelden",
        _ => $"{marketplace}'dan"
    };
}
