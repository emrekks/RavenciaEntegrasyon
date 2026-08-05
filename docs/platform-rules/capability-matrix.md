# Trendyol ve E-Faturam Capability Matrisi

## Durum sözleşmesi

- `SUPPORTED`: Resmî kaynak, tarihli environment/store scope kanıtı ve gerekli fixture vardır.
- `UNKNOWN`: Kod bulunabilir; güvenli dış çalışma kanıtı yoktur.
- `NOT_SUPPORTED`: Platform veya proje kararı desteklemez.
- Read kanıtı write yetkisi değildir. Write için global + connection anahtarı ve Stage/SIT SHA-256 fixture evidence zorunludur.

## Trendyol Türkiye CORE

| Capability | Kod durumu | Dış kanıt / çıkış durumu |
| --- | --- | --- |
| Connection test | Uygulandı | Gerçek hesapla yeniden kabul edilmeli |
| Reference read | Uygulandı | Büyük ağaç/pagination Stage testi gerekli |
| Product read | Uygulandı | Approved/unapproved, 100 limit, page→token cursor; Stage katalog testi gerekli |
| Product create | Uygulandı | Durable batch + approval read-back; safe-write fixture gerekli |
| Product update | Uygulandı | Unapproved ve approved content/variant/delivery fazları; Stage fixture gerekli |
| Product archive/unarchive | Uygulandı | Batch + publication read-back; Stage fixture gerekli |
| Inventory + price write | Uygulandı | Tek batch ve stale-version koruması; Stage fixture gerekli |
| Order read | Uygulandı | Stream + `/v2/orders`; overlap/duplicate Stage testi gerekli |
| Webhook ingest | Uygulandı | Gerçek subscription/auth delivery kanıtı gerekli |
| Shipment write | Uygulandı, fail-closed | Yalnız evidence constraints `allowedActions`; Stage action fixture gerekli |
| Label read/write | Uygulandı | Common label create/poll/private storage; gerçek ZPL/PDF testi gerekli |
| Return read/write | Uygulandı | Exact claim read, approve/reject/evidence; Stage claim fixture gerekli |
| Invoice link delivery | Uygulandı | POST sonrası `SUBMITTED`; gerçek package ve URL retention testi gerekli |
| Invoice delivery terminal query | Doğrulanmadı | Sahte başarı yok; manuel teyit veya resmî query kanıtı gerekir |

## E-Faturam

F4 kapsamıdır. Sign-in/submit/PDF URL kodu vardır; taxpayer, status ve cancel ile gerçek mali E2E tamamlanmamıştır.

## Açılma kuralı

Capability `UNKNOWN` iken API/UI dış iş oluşturamaz. Evidence endpoint'i yalnız Owner/Administrator tarafından ETag ve audit ile güncellenir; resmî HTTPS kaynak, environment/store bire bir eşleşmesi ve write için 64 haneli fixture SHA-256 ister.

## Kanal sınırı

Bu matris `storeFrontCode=TR` ve `channels=["CORE"]` kapsamındadır. LUXE veya uluslararası storefront ayrı ADR/evidence olmadan desteklenmez.
