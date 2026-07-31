# F0 İş Otoriteleri ve Güvenli Varsayılanlar

## Bağlayıcı kararlar

| Konu | Otorite / varsayılan | Güvenlik davranışı |
| --- | --- | --- |
| Stok | Merkezi `StockLedger` tek stok otoritesidir. | Platform bakiyesi doğrudan merkez gerçeği yapılmaz; uzlaştırma gerekir. |
| Fiyat | Merkezi fiyat esastır; kanal farkı yalnız açık channel override ile uygulanır. | Örtük kur veya platform dönüşümü fiyat otoritesi değildir. |
| Depo | Başlangıçta tek mantıksal lokasyon: `MAIN`. | Doğrulanmamış çoklu depo eşlemesi yapılmaz. |
| Safety stock | Başlangıç değeri `0`. | Açık iş kararı olmadan otomatik artırılmaz. |
| İade restock | Yalnız kalite sonucu `PASS` ise stoklanır. | `UNKNOWN`, bekleyen veya başarısız kalite sonucu stok artırmaz. |
| Otomatik fatura | Kapalı. | Ayrı capability, yetki ve iş onayı olmadan fatura gönderilmez. |
| Dış yazma | Tüm write kill switch'leri kapalı. | `UNKNOWN`, `NOT_SUPPORTED` veya `TEMPORARILY_UNAVAILABLE` yazmayı reddeder. |

## Değişiklik kapısı

Bu kararlar ancak yetkili şartnameyi ihlal etmeyen, kullanıcıca onaylanmış iş kararı ve ilgili ADR güncellemesiyle değişebilir. Platform dokümanı tek başına merkezi iş otoritesini değiştiremez.
