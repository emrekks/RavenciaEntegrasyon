namespace MarketplaceHub.Application;

// How a product create job reads a batch result that does not list every item it sent. Trendyol answers a create batch
// as COMPLETED while it is still adding items to the result (live 2026-11-02: 21 of 28 barcodes listed 16 s after the
// send, none refused), so a short answer is first a result still being written: the job asks again with the usual
// backoff. Only once the grace has passed does it settle: the listed items get their own outcome and reason, and an
// item still missing stays unknown (never assumed created or refused); the approval check then reads it by barcode.
public static class ProductBatchResultPolicy
{
    // A listing whose create batch ended without a line for it: its outcome is not known yet.
    public const string ResultUnknownStatus = "CREATE_RESULT_UNKNOWN";

    public const string IncompleteResultCode = "PRODUCT_BATCH_RESULT_INCOMPLETE";

    public static readonly TimeSpan IncompleteGrace = TimeSpan.FromMinutes(30);

    // True while a result missing some sent items is still young enough to be one Trendyol has not finished writing.
    public static bool KeepWaiting(int missingCount, TimeSpan sinceSubmit) => missingCount > 0 && sinceSubmit < IncompleteGrace;

    public static string WaitingMessage(string marketplaceName, int listed, int sent) =>
        $"{marketplaceName} sonucu henüz gönderilen {sent} varyantın {listed} tanesini listeliyor; kalanların sonucu bekleniyor.";

    public static string MissingReason(string marketplaceName) =>
        $"{marketplaceName} batch sonucunda bu barkod yer almadı; durumu barkodla sorgulanıyor.";

    // Trendyol refuses a create whose barcode the store already holds ("Aynı barkodlu bir ürününüz bulunduğundan yeni ürün
    // oluşturulamaz. Barkod: …"): the product is there, only the panel does not know it (live 5.11.2026: MZ021Y-CD-34..36,
    // created at 00:59, refused at 09:30 and 10:19). Such a line is no refusal: like an unlisted one it stays unknown and
    // the approval check reads it by barcode, so the panel matches the product instead of sending it again.
    public const string BarcodeExistsMarker = "Aynı barkodlu bir ürününüz bulunduğundan";

    public static bool SaysBarcodeExists(string? reason) =>
        reason?.Contains(BarcodeExistsMarker, StringComparison.OrdinalIgnoreCase) == true;

    // What the job's summary says about the variants it left to the approval check: the ones the result did not list and
    // the ones Trendyol named as already in the store, each in its own words.
    public static string IncompleteSummary(string marketplaceName, int missing, int alreadyThere, int sent)
    {
        var parts = new List<string>(2);
        if (missing > 0) parts.Add($"{marketplaceName} batch sonucu {sent} varyantın {missing} tanesini listelemedi");
        if (alreadyThere > 0) parts.Add($"{marketplaceName} {sent} varyantın {alreadyThere} tanesinin barkodunu mağazada zaten buldu");
        return parts.Count == 0 ? string.Empty : $"{string.Join("; ", parts)}; bu varyantların durumu onay kontrolünde barkodla sorgulanıp eşleştiriliyor.";
    }
}
