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

## Trendyol E-Faturam

Gönderen mali hesap E-Faturam tarafından yönetilir. Panel yalnız doğrudan API kullanıcı credentialını saklar; company/user scope sign-in tokenından okunur, varsayılan seri provider hesabından gelir. Belge türü sipariş snapshotından otomatik seçilir. Payment/delivery kullanıcı ayarı değildir; E-Arşiv için gerekli alanlar Trendyol siparişi ve resmî carrier kataloğundan üretilir.

| Capability | Kod durumu | Dış kanıt / çıkış durumu |
| --- | --- | --- |
| Connection test | Uygulandı | API_USER sign-in ve token company/user scope Stage hesabıyla doğrulanmalı |
| Automatic invoice routing | Uygulandı | Corporate + eInvoiceAvailable ve individual/E-Arşiv fixtureları doğrulanmalı |
| Invoice submit | Uygulandı | E-Fatura/E-Arşiv safe-write Stage fixture gerekli |
| E-Arşiv status read | Uygulandı | UUID ve 10/20/29/30/40/50/100/105/200/205/305/405 kodları Stage'de doğrulanmalı |
| Giden E-Fatura status read | Fail-closed | Exact göreli endpoint deployment ayarı yalnız Stage/SIT evidence sonrasında girilir |
| Invoice document read | Uygulandı | Resmî permanent URL hostu, PDF ve retention E2E gerekli |
| E-Arşiv cancel | Uygulandı | 305 terminal read-back fixture gerekli |
| E-Fatura cancel | Otomatik desteklenmez | Mevzuata uygun manuel itiraz/iptal süreci |
| Trendyol invoice delivery | Uygulandı | Provider permanent URL ve gerçek package E2E; terminal teyit kanıtı yoksa manuel review |

## Açılma kuralı

Capability `UNKNOWN` iken API/UI dış iş oluşturamaz. Evidence endpoint'i yalnız Owner/Administrator tarafından ETag ve audit ile güncellenir; platforma göre `developers.trendyol.com` veya `developers.trendyolefaturam.com` resmî HTTPS kaynağı, environment/store bire bir eşleşmesi ve write için 64 haneli fixture SHA-256 ister.

## Kanal sınırı

Bu matris `storeFrontCode=TR` ve `channels=["CORE"]` kapsamındadır. LUXE veya uluslararası storefront ayrı ADR/evidence olmadan desteklenmez.
