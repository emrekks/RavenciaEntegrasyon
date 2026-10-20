# İade Faturası (İADE) Araştırması — Trendyol E-Faturam

Tarih: 20 Ekim 2026 · Kapsam: tarama raporu F5 (ikinci yarı) · Durum: **uygulanmadı, sahip kararı ve sağlayıcı teyidi bekliyor**

Panel bugün `POST /invoices` isteğinde `OriginalInvoiceId` gelirse **422** döner (19–20 Ekim sahip kararı). Bu belge,
gerçek bir İADE faturası akışının E-Faturam üzerinden kurulup kurulamayacağını araştırır. Sonuç: istek alanlarının adları
belgelenmiş, ancak bu panelin satış senaryosunda (pazaryerinde nihai tüketiciye satış) İADE faturasını **kimin, kime**
keseceği ve E-Faturam'ın bu senaryoyu kabul edip etmediği belgelenmemiş. Mali belge geri alınamadığı için tahminle kod
yazılmadı; 422 korunuyor.

## 1. E-Faturam dokümanında bulunanlar

Kaynak: [createEArchive](https://developers.trendyolefaturam.com/OpenApi/eAr%C5%9Fiv/create-e-archive) (POST
`/api/invoice/documents/earchive`), aynı alanlar giden e-Fatura isteğinde de var.

| Alan | Belgelenen | Not |
|---|---|---|
| `invoiceInfo.invoiceTypeCode` | `SATIS, IADE, TEVKIFAT, ISTISNA, OZELMATRAH, IHRACKAYITLI, TEVKIFATIADE, SEVK, MATBUDAN, …` | Panel bugün sabit `SATIS` gönderiyor. |
| `invoiceInfo.invoiceType` | `EARSIVFATURA, TEMELFATURA, TICARIFATURA, …` | Senaryo. |
| `billingReference` | `{ invoiceId, issueDate, typeCode }` — "İade faturaları için iade edilen fatura bilgisi" | Tek fatura iadesi. |
| `billingReference.invoiceId` | zorunlu, `^[A-Z0-9]{3}20[0-9]{2}[0-9]{9}$` ("Fatura numarası") | ETTN değil, **16 haneli fatura numarası**. |
| `billingReference.issueDate` | zorunlu, `date` ("Fatura kesilme tarihi") | Saat yok, yalnız gün. |
| `billingReference.typeCode` | `[IADE]`, varsayılan `IADE` | |
| `billingReferences[]` | aynı yapı, "birden fazla iade edilen fatura olması durumunda" | |
| `invoiceLines[]` | satış faturasıyla aynı yapı (`quantity`, `unitPriceAmount`, `taxPercent`, `totalTax`…) | Tutarlar kuruş. |
| `recipientInfo` | zorunlu; `taxId` (VKN/TCKN, ≥10), `name`, `city`… | İadede alıcının kim olacağı yazmıyor. |

Dokümanın Giriş, SSS ve Enum sayfalarında İADE faturası için ayrıca kural, örnek istek veya hata kodu **yok**
([Giriş](https://developers.trendyolefaturam.com/OpenApi/trendyol-e-faturam-entegrasyon-dokumani),
[SSS](https://developers.trendyolefaturam.com/faq),
[Statü kodları](https://developers.trendyolefaturam.com/OpenApi/Enum%20ve%20Kod%20A%C3%A7%C4%B1klamalar%C4%B1/variables-info)).
Pazaryeri entegratörü dokümanında da İADE geçmiyor
([marketplace-docs](https://developers.trendyolefaturam.com/marketplace-docs)).

## 2. Neden uygulanmadı — kesin olmayanlar

1. **Kim keser, kime keser?** Vergi uygulamasında iade faturasını malı geri gönderen (alıcı) düzenler. Alıcı vergi
   mükellefi değilse (pazaryeri müşterisinin neredeyse tamamı) satıcı iade için **gider pusulası** düzenler; e-Arşiv
   faturayla yapılan internet satışlarında faturanın iade bölümü de gider pusulası yerine geçebilir (VUK 509 sıra no'lu
   tebliğe atıf yapan ikincil kaynaklar, ör. [alomaliye](https://www.alomaliye.com/2015/01/02/nihai-tuketicilerden-iade-alinan-mallar-icin-uygulanacak-prosedur/)).
   Yani satıcının tüketiciye "İADE tipli e-Arşiv" kesmesi mevzuata uygun olmayabilir. Bunu sağlayıcı dokümanı
   çözmüyor; mali müşavir kararı gerekir.
2. **Alıcı bilgisi:** İADE faturasında `recipientInfo` asıl satıcı mı (malı iade eden mükellefin faturası), yoksa
   müşteri mi olmalı? Doküman bu durumu tarif etmiyor.
3. **E-Faturam doğrulaması:** `EARSIVFATURA` + `IADE` + TCKN'li bireysel alıcı kombinasyonunu kabul edip etmediği,
   hangi hata kodunu döndüğü belgelenmemiş. Stage hesabında denenmedi (dış API çağrısı yapılmadı).
4. **İnternet satışı alanları:** Satış e-Arşivinde zorunlu olan `paymentInfo`/`deliveryInfo` (1 Temmuz 2024 kuralı) İADE
   faturasında da zorunlu mu, belli değil.
5. **Numara serisi:** İADE için ayrı `prefix` (seri) gerekip gerekmediği belli değil.
6. **Kısmi iade:** `billingReference` fatura düzeyinde; satır bazında hangi satırın iade edildiğini bağlayan bir alan
   yok. Kısmi iadede yalnız iade edilen adetlerin satır olarak yazılması yeterli mi, belli değil.
7. **Pazaryerine iletim:** Trendyol/Hepsiburada fatura linki servisleri iade belgesi için ayrı bir tür tanımlamıyor;
   İADE faturasının linki pakete iletilmeli mi, iletilmemeli mi, belgelenmemiş.

## 3. Sahibin E-Faturam desteğine (ve mali müşavire) sorması gerekenler

1. Pazaryerinde nihai tüketiciye (TCKN'li, mükellef olmayan) yapılan satışın iadesinde, satıcı olarak **İADE tipli
   e-Arşiv** düzenlememiz doğru mu, yoksa gider pusulası mı düzenlemeliyiz? (Mali müşavir.)
2. İADE tipli e-Arşivde `recipientInfo` kim olmalı? Örnek bir tam istek (JSON) paylaşabilir misiniz?
3. `billingReference.invoiceId` için asıl faturanın 16 haneli numarası, `issueDate` için asıl faturanın düzenlenme günü
   yeterli mi? ETTN gerekmiyor mu?
4. İADE faturasında `paymentInfo` ve `deliveryInfo` zorunlu mu?
5. İADE faturası için ayrı seri (`prefix`) tanımlamamız gerekir mi?
6. Kısmi iadede satırlar yalnız iade edilen adetlerle mi yazılmalı; tutarların asıl faturayı aşmaması dışında bir
   doğrulama var mı?
7. Bu kombinasyonun Stage ortamında denenebileceği bir test senaryosu veya hata kodu listesi var mı?

## 4. Yanıtlar gelince yapılacaklar (tahmini iş)

- `TrendyolEFaturamInvoicePayload`: `invoiceTypeCode` ve `billingReference` alanlarını taslaktan almak (bugün sabit `SATIS`).
- Taslak: aynı siparişin Accepted/Completed SATIŞ faturasına bağlı; satırlar iade satırlarından (iade kalemi → sipariş
  satırı) yalnız iade edilen adetlerle; tutarlar asıl faturayı aşamaz; mevcut gönderim kapılarından geçer.
- Fatura detayında "İade Faturası Oluştur" işlemi: önizleme + panelin kendi onay penceresi; otomatik çalışmaz.
- `POST /invoices` içinde `OriginalInvoiceId` ile gelen genel yol 422 olarak kalır.
