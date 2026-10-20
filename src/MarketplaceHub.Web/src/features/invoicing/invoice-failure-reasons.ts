// Readable Turkish text for the invoice error codes the workspace shows on a failed or blocked invoice.
export function invoiceFailureReason(code: string | null) {
  const labels: Record<string, string> = {
    EFATURAM_FISCAL_PAYLOAD_INVALID: 'Fatura bilgileri sağlayıcının istediği biçimde değil.',
    EFATURAM_REQUEST_REJECTED: 'E-Faturam isteği reddetti.',
    EFATURAM_APPLICATION_NOT_ACTIVE: 'E-Faturam fatura uygulaması bu hesap için aktif değil.',
    EFATURAM_AUTHENTICATION_FAILED: 'E-Faturam kimlik doğrulamasını kabul etmedi.',
    EFATURAM_ACCESS_TOKEN_REJECTED: 'E-Faturam erişim anahtarını reddetti.',
    EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING: 'Bu hesapta fatura oluşturma yetkisi bulunmuyor.',
    EFATURAM_CARRIER_CATALOG_MISS: 'Kargo firmasının fatura bilgisi sağlayıcıya tanımlı değil.',
    HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500: 'Fatura oluşturuldu; Hepsiburada belge iletiminde tekrarlanan sunucu hatası verdi.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'Fatura sağlayıcısı ve pazaryeri farklı ortamlarda. Platform iletimi engellendi.',
    REMOTE_INVOICE_REJECTED: 'Pazaryeri faturayı reddetti.',
    INVOICE_SUBMISSION_OUTCOME_UNKNOWN: 'Sağlayıcıya gönderim sonucu alınamadı. Faturanın portalda oluşup oluşmadığını kontrol edin; oluşmadıysa yeniden gönderebilirsiniz.',
    INVOICE_SUBMISSION_CANCELLED: 'Gönderim sırasında bağlantı kesildi. Faturanın portalda oluşup oluşmadığını kontrol edin.',
    INVOICE_SUBMISSION_TRANSPORT_UNKNOWN: 'Sağlayıcıdan yanıt alınamadı. Faturanın portalda oluşup oluşmadığını kontrol edin.',
    REMOTE_INVOICE_NOT_PRESENT: 'Pazaryeri iletilen faturayı göstermedi; iletim yeniden denenecek.',
    PLATFORM_INVOICE_ALREADY_PRESENT: 'Pazaryerinde bu pakete ait bir fatura zaten görünüyor; üzerine yazılmadı. Pazaryeri panelinde kontrol edin.',
    MARKETPLACE_INVOICE_IDENTITY_MISMATCH: 'Pazaryeri bir fatura gösteriyor ama bu faturayla eşleştirilemedi. Pazaryeri panelinde kontrol edin.',
    DELIVERY_RETRY_PAYLOAD_CHANGED: 'Önceki iletim sonucu belirsiz kaldı ve fatura bilgisi değişti. Pazaryeri panelinde kontrol edin.',
    HEPSIBURADA_INVOICE_DELIVERY_READBACK_MISSING: 'Hepsiburada isteği aldı ama faturayı henüz göstermiyor. Birkaç dakika içinde yeniden kontrol edilir.',
    HEPSIBURADA_REMOTE_ERROR: 'Hepsiburada fatura iletimini kabul etmedi.',
    PLATFORM_INVOICE_STATUS_NOT_CLEAR: 'Pazaryeri fatura durumunu net bildirmedi. Pazaryeri panelinde kontrol edin.',
    OPERATOR_CONFIRMED_NO_REMOTE_INVOICE: 'Portalda fatura olmadığı onaylandı; yeniden gönderilebilir.',
    REMOTE_INVOICE_NUMBER_MISMATCH: 'Pazaryerinde bu faturayla eşleştirilemeyen bir fatura görünüyor. Pazaryeri panelinde kontrol edin.',
    HEPSIBURADA_INVOICE_ALREADY_PRESENT: 'Hepsiburada pakette zaten bir fatura gösteriyor; üzerine yazılmadı.',
    ONE_TIME_INVOICE_DELIVERY_ALREADY_ATTEMPTED: 'Önceki iletim belirsiz kaldı; ikinci kez otomatik gönderilmedi. Pazaryeri panelinde kontrol edin.',
    EINVOICE_RECIPIENT_REQUIRES_EFATURA: 'Alıcı e-Fatura mükellefi; e-Arşiv kesilemez. Faturayı E-Faturam portalında e-Fatura olarak düzenleyin.',
    INVOICE_EXPORT_FLOW_REQUIRED: 'Mikro ihracat veya TRY dışı sipariş; yurt içi e-Arşiv kesilemez.',
    INVOICE_ADJUSTMENT_FLOW_UNAVAILABLE: 'İade/düzeltme faturası akışı henüz hazır değil.',
    EFATURAM_RECIPIENT_TAX_ID_INVALID: 'Alıcının vergi numarası veya T.C. kimlik numarası geçersiz (kontrol hanesi tutmuyor).',
    EFATURAM_SIGNIN_UNAVAILABLE: 'E-Faturam oturumu açılamadı; fatura gönderilmedi. Otomatik olarak yeniden denenecek.',
    EFATURAM_OPERATION_FORBIDDEN: 'E-Faturam hesabı bu işlem için yetkili değil; fatura oluşturulmadı.',
    CAPABILITY_NOT_VERIFIED: 'E-Faturam dış gönderim izni kapalı; fatura oluşturulmadı.',
    INVOICE_ADJUSTMENT_REQUIRED: 'Sipariş fatura kesildikten sonra iptal edildi veya iadesi kabul edildi; e-Arşiv iptali ya da iade faturası gerekli.'
  }
  return labels[code ?? ''] ?? (code ? `İletilemedi: ${code}` : 'Ayrıntılar için faturayı açın.')
}
export function invoiceFailureGuidance(code: string | null) {
  const labels: Record<string, string> = {
    EFATURAM_FISCAL_PAYLOAD_INVALID: 'Fatura detaylarını açıp satır, tutar, vergi ve adres bilgilerini kontrol edin.',
    EFATURAM_REQUEST_REJECTED: 'Fatura sağlayıcısının yanıtını kontrol edin; bilgiler düzeltildikten sonra Tekrar Dene seçeneğini kullanın.',
    EFATURAM_APPLICATION_NOT_ACTIVE: 'Entegrasyonlar ekranından E-Faturam uygulamasını etkinleştirip bağlantıyı yeniden test edin.',
    EFATURAM_AUTHENTICATION_FAILED: 'Entegrasyonlar ekranından E-Faturam kimlik bilgilerini yenileyip bağlantıyı test edin.',
    EFATURAM_ACCESS_TOKEN_REJECTED: 'E-Faturam bağlantısını yeniden yetkilendirin, ardından faturayı tekrar deneyin.',
    EFATURAM_INVOICE_CREATE_PRIVILEGE_MISSING: 'E-Faturam hesabında fatura oluşturma yetkisini açtırın.',
    EFATURAM_CARRIER_CATALOG_MISS: 'Faturayı tekrar deneyin; kargo taşıyıcı bilgisi doğrulamaya eklendi.',
    HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500: 'Platforma iletimi tekrar dene ile mevcut belgeyi kontrol edin. Yeni mali fatura oluşturulmaz.',
    INVOICE_DELIVERY_ENVIRONMENT_MISMATCH: 'İletim önizlemesindeki ortam bilgisini kontrol edin. Canlı sipariş için canlı ortamda düzenlenmiş belge gereklidir.',
    REMOTE_INVOICE_REJECTED: 'Fatura sağlayıcısının veya pazaryerinin ret nedenini kontrol edip gerekli bilgileri düzelttikten sonra tekrar deneyin.',
    EINVOICE_RECIPIENT_REQUIRES_EFATURA: 'E-Faturam portalında e-Fatura düzenleyip belgeyi bu siparişe manuel fatura olarak ekleyin.',
    INVOICE_EXPORT_FLOW_REQUIRED: 'İhracat faturasını E-Faturam portalında düzenleyip belgeyi siparişe manuel olarak ekleyin.',
    EFATURAM_RECIPIENT_TAX_ID_INVALID: 'Alıcının vergi/T.C. kimlik numarasını pazaryerinde düzeltip siparişi yeniden eşitleyin.',
    EFATURAM_SIGNIN_UNAVAILABLE: 'Bekleyin; geçmezse Entegrasyonlar ekranından E-Faturam bağlantısını test edin.',
    EFATURAM_OPERATION_FORBIDDEN: 'E-Faturam hesabında fatura oluşturma yetkisini açtırıp Tekrar dene seçeneğini kullanın.',
    INVOICE_ADJUSTMENT_REQUIRED: 'E-Arşiv iptal isteği gönderin veya iade faturasını E-Faturam portalında düzenleyin.'
  }
  return labels[code ?? ''] ?? 'Fatura ayrıntılarını açıp son hata kodunu ve fatura sağlayıcısı denemelerini kontrol edin.'
}
