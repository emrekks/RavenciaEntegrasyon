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

    public static string IncompleteSummary(string marketplaceName, int missing, int sent) =>
        $"{marketplaceName} batch sonucu {sent} varyantın {missing} tanesini listelemedi; bu varyantların durumu onay kontrolünde barkodla sorgulanıyor.";
}
