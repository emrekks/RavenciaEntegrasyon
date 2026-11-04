using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Imports;

// The validity days the seller picks for a tariff (Geçerlilik Başlangıcı / Bitişi): whole Istanbul calendar days, both
// included. A tariff then covers From 00:00 to the midnight after To (exclusive), kept as UTC instants (Npgsql's
// timestamptz takes offset 0 only). The file's own period text is kept as it is: the Excel of the seller's picks
// matches the file's rows by it and writes it back to Trendyol.
public static class CommissionTariffValidity
{
    public const int MaximumDays = 366;
    private const string Code = "COMMISSION_TARIFF_PERIOD_INVALID";
    private static readonly string[] Months = ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    public static ServiceError? Invalid(DateOnly from, DateOnly to)
    {
        if (from.Year < 2000 || to.Year > 2100) return new(Code, "Geçerlilik tarihleri geçersiz.", 400);
        if (from > to) return new(Code, "Geçerlilik başlangıcı, geçerlilik bitişinden sonra olamaz.", 400);
        if (to.DayNumber - from.DayNumber + 1 > MaximumDays) return new(Code, $"Geçerlilik aralığı en fazla {MaximumDays} gün olabilir.", 400);
        return null;
    }

    // From 00:00 Istanbul to the midnight after To, as UTC instants.
    public static (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly from, DateOnly to) => (Midnight(from), Midnight(to.AddDays(1)));

    // The Istanbul days a stored period touches: the day of its start and the day of its last moment.
    public static (DateOnly From, DateOnly To) Days(DateTimeOffset start, DateTimeOffset end) =>
        (DateOnly.FromDateTime(start.ToOffset(CommissionTariffFileParser.TurkeyOffset).DateTime), DateOnly.FromDateTime(end.AddTicks(-1).ToOffset(CommissionTariffFileParser.TurkeyOffset).DateTime));

    // "29 Ekim – 5 Kasım 2026"; "29 Aralık 2026 – 2 Ocak 2027" across a year; "5 Kasım 2026" for one day.
    public static string Text(DateOnly from, DateOnly to)
    {
        var end = $"{to.Day} {Months[to.Month - 1]} {to.Year}";
        if (from == to) return end;
        return from.Year == to.Year ? $"{from.Day} {Months[from.Month - 1]} – {end}" : $"{from.Day} {Months[from.Month - 1]} {from.Year} – {end}";
    }

    public static string Text(DateTimeOffset start, DateTimeOffset end)
    {
        var (from, to) = Days(start, end);
        return Text(from, to);
    }

    // A file's periods (the earliest first) with the picked days as their outer bounds: the first period starts on From,
    // the last ends after To, the boundaries between the periods stay where the file put them and must stay inside.
    public static ServiceResult<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> Outer(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> periods, DateOnly from, DateOnly to)
    {
        if (Invalid(from, to) is { } invalid) return ServiceResult<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>>.Fail(invalid.Code, invalid.Message, invalid.Status);
        if (periods.Count == 0) return ServiceResult<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>>.Fail(Code, "Tarifenin dönemi yok.", 400);
        var (start, end) = Bounds(from, to);
        if (periods.Count > 1 && (start >= periods[0].End || end <= periods[^1].Start))
            return ServiceResult<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>>.Fail(Code,
                $"Dosyada {periods.Count} dönem var; seçilen günler ilk dönemin bitişini ({Moment(periods[0].End)}) ve son dönemin başlangıcını ({Moment(periods[^1].Start)}) kapsamalıdır.", 400);
        var result = periods.ToList();
        result[0] = (start, result[0].End);
        result[^1] = (result[^1].Start, end);
        return ServiceResult<IReadOnlyList<(DateTimeOffset, DateTimeOffset)>>.Ok(result);
    }

    // One period of a file moved to the picked days; it may not overlap the file's other periods.
    public static ServiceResult<(DateTimeOffset Start, DateTimeOffset End)> Single(DateOnly from, DateOnly to, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> siblings)
    {
        if (Invalid(from, to) is { } invalid) return ServiceResult<(DateTimeOffset, DateTimeOffset)>.Fail(invalid.Code, invalid.Message, invalid.Status);
        var (start, end) = Bounds(from, to);
        foreach (var sibling in siblings)
            if (start < sibling.End && sibling.Start < end)
                return ServiceResult<(DateTimeOffset, DateTimeOffset)>.Fail(Code, $"Seçilen günler dosyanın diğer dönemiyle ({Text(sibling.Start, sibling.End)}) çakışıyor.", 400);
        return ServiceResult<(DateTimeOffset, DateTimeOffset)>.Ok((start, end));
    }

    private static string Moment(DateTimeOffset value)
    {
        var local = value.ToOffset(CommissionTariffFileParser.TurkeyOffset);
        return $"{local.Day} {Months[local.Month - 1]} {local:HH:mm}";
    }

    private static DateTimeOffset Midnight(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), CommissionTariffFileParser.TurkeyOffset).ToUniversalTime();
}
