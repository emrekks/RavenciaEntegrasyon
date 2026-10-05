using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

public static class ProductImportFailureReasonPolicy
{
    private const string DetailMarker = "Veritabanı ayrıntısı: ";

    public static IReadOnlyList<JobFailureReasonView> Build(IEnumerable<OperationalIssue> issues)
    {
        return issues
            .Select(issue => (Issue: issue, Cause: Explain(TechnicalDetail(issue.Summary))))
            .GroupBy(item => item.Cause.Key, StringComparer.Ordinal)
            .Select(group => new JobFailureReasonView(
                group.Key,
                group.First().Cause.Title,
                group.First().Cause.Description,
                group.First().Cause.TechnicalDetail,
                group.Count(),
                group.Select(item => ProductId(item.Issue.DedupeKey))
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToArray()))
            .OrderByDescending(reason => reason.AffectedRecords)
            .ThenBy(reason => reason.Title, StringComparer.Ordinal)
            .ToArray();
    }

    private static Cause Explain(string technicalDetail)
    {
        if (technicalDetail.Contains("The connection is already in a transaction", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "database-transaction-already-open",
                "Açık veritabanı işlemi üzerine ikinci işlem başlatıldı",
                "Ürün kaydı işlenirken kategori ve özellik referansı yenilemesi aynı bağlantıda açık bir işlem varken ikinci işlemi başlatmaya çalıştı. İçe aktarım bu nedenle ilgili ürün grubunu kaydedemedi. Transaction yönetimi düzeltildi; bu kayıtlar sonraki ürün senkronizasyonunda yeniden işlenecek.",
                technicalDetail);
        }

        if (technicalDetail.Contains("Sequence contains more than one matching element", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "multiple-mapped-attributes",
                "Bir panel özelliği birden fazla kategori alanıyla eşleşti",
                "Kategori içindeki farklı pazaryeri alanları aynı panel özelliğine bağlanmış. Ürün aktarımı bunlardan tek bir alan bulmayı beklediği için kayıt hata verdi. Eşleşmeler artık panel özelliği kimliğine göre tekilleştiriliyor; bu kayıtlar sonraki ürün senkronizasyonunda yeniden işlenecek.",
                technicalDetail);
        }

        if (technicalDetail.Contains("sqlState=23505", StringComparison.OrdinalIgnoreCase))
        {
            return new("postgres-unique-conflict", "Veritabanında yinelenen kayıt çakışması", "Aktarılan bilgi, veritabanında zaten bulunan tekil bir kayıtla çakıştı. Ayrıntıdaki tablo ve kısıt adı çakışan alanı gösterir.", technicalDetail);
        }

        if (technicalDetail.Contains("sqlState=23503", StringComparison.OrdinalIgnoreCase))
        {
            return new("postgres-reference-missing", "Bağlı kayıt bulunamadı", "Ürün kaydı eklenirken bağlı olması gereken bir kategori, özellik veya başka bir kayıt bulunamadı. Ayrıntıdaki tablo ve kısıt adı eksik bağlantıyı gösterir.", technicalDetail);
        }

        if (technicalDetail.Contains("sqlState=23502", StringComparison.OrdinalIgnoreCase))
        {
            return new("postgres-required-value-missing", "Zorunlu alan boş geldi", "Ürün aktarımında veritabanının zorunlu tuttuğu bir değer boş kaldı. Teknik ayrıntı eksik alanı gösterir.", technicalDetail);
        }

        if (technicalDetail.Contains("sqlState=22001", StringComparison.OrdinalIgnoreCase))
        {
            return new("postgres-value-too-long", "Metin alanı izin verilen uzunluğu aştı", "Pazaryerinden gelen bir metin, yerel alandaki karakter sınırından uzun. Teknik ayrıntı ilgili alanı gösterir.", technicalDetail);
        }

        var exceptionType = ValueAfter(technicalDetail, "exception=") ?? "Bilinmeyen hata";
        var detail = ValueAfter(technicalDetail, "detail=") ?? technicalDetail;
        return new($"{exceptionType}:{detail}", $"{exceptionType} nedeniyle ürün aktarılamadı", "Ürün grubu kaydedilirken teknik bir hata oluştu. Aşağıdaki ayrıntı, hatanın kaynağını belirlemek ve düzeltmek için kaydedildi.", technicalDetail);
    }

    private static string TechnicalDetail(string summary)
    {
        var marker = summary.IndexOf(DetailMarker, StringComparison.Ordinal);
        return marker < 0 ? "Hata oluştu; ayrıntılı teknik neden bu kayıt için saklanmamış." : summary[(marker + DetailMarker.Length)..].Trim();
    }

    private static string? ValueAfter(string value, string marker)
    {
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = value.IndexOf(';', start);
        return (end < 0 ? value[start..] : value[start..end]).Trim();
    }

    private static string? ProductId(string dedupeKey)
    {
        var separator = dedupeKey.LastIndexOf(':');
        return separator < 0 || separator == dedupeKey.Length - 1 ? null : dedupeKey[(separator + 1)..];
    }

    private sealed record Cause(string Key, string Title, string Description, string TechnicalDetail);
}
