# Trendyol ve E-Faturam Capability Matrisi

## Durum sözleşmesi

- `SUPPORTED`: Resmî kaynak + tarihli test hesabı/anonim fixture + kapsam kaydı vardır.
- `UNKNOWN`: Kod veya doküman olabilir; güvenli dış çalışma kanıtı yoktur.
- `NOT_SUPPORTED`: Platform veya proje kararı açıkça desteklemez.
- `TEMPORARILY_UNAVAILABLE`: Daha önce kanıtlı capability geçici olarak erişilemiyordur.

Read kanıtı write yetkisi değildir. Bütün write işlemleri global ve connection düzeyindeki iki anahtar açılmadan çalışmaz.

## Güncel durum

| Platform | Capability | Kod durumu | Dış kanıt / çıkış durumu |
| --- | --- | --- | --- |
| Trendyol | Connection test | Uygulandı | Gerçek Stage/Production hesapla yeniden kabul edilmeli |
| Trendyol | Category/brand/attribute/value read | Uygulandı | Büyük kategori ağacı, pagination ve leaf seçimi E2E testi gerekli |
| Trendyol | Approved product read | Uygulandı | Pagination/token ve mapping fixture kapsamı genişletilmeli |
| Trendyol | Product create + batch result | Adapter + application durable job + satır sonucu uygulandı | Exact dinamik test, approved-products onay reconciliation ve Stage safe-write gerekli |
| Trendyol | Product update/archive | Uygulanmadı | `UNKNOWN`; create ile update aynı işlem gibi gösterilmemeli |
| Trendyol | Stock + price write | Uygulanmadı | Mevcut ayrı portlar birleşik uzak komuta dönüştürülmeli |
| Trendyol | Order/package read | Uygulandı | Duplicate, overlap, pagination ve durum geçişi gerçek fixture ile doğrulanmalı |
| Trendyol | Webhook ingest | Yerel sınır uygulandı | Resmî signed delivery ve reconciliation kanıtı gerekli |
| Trendyol | Shipment/package write | Uygulanmadı | Exact action endpoint ve izin seti doğrulanmadan kapalı |
| Trendyol | Return read | Uygulandı | Boş kaynak/404 davranışı ve gerçek claim fixture gerekli |
| Trendyol | Return action | Uygulanmadı | Approve/reject/dispute ayrı capability olarak kanıtlanmalı |
| Trendyol | Invoice link delivery | Uygulandı | Gerçek package ile Stage testi, sonuç uzlaştırması ve URL için 8 yıllık erişilebilirlik kanıtı gerekli |
| Trendyol | Invoice delivery status | Uygulanmadı | POST 2xx tek başına nihai teslim kanıtı sayılmamalı |
| E-Faturam | Sign-in / connection test | Uygulandı | Test firma ve doğru entegrasyon modeliyle kabul edilmeli |
| E-Faturam | Taxpayer query | Uygulanmadı | Firma/user scope ve endpoint sözleşmesi doğrulanmalı |
| E-Faturam | Invoice submit | Uygulandı | Mali policy, e-Fatura/e-Arşiv ayrımı ve Stage E2E gerekli |
| E-Faturam | Invoice status read | Uygulanmadı | Polling/reconciliation tamamlanmalı |
| E-Faturam | Permanent PDF URL | Uygulandı | Exact download host allow-list, redirect/IP guard, PDF imza kontrolü, URL ömrü ve private download E2E gerekli |
| E-Faturam | Invoice cancel | Uygulanmadı | Mali yetki, süre ve durum kuralları onaylanmalı |

## Açılma kuralı

Bir capability `UNKNOWN` iken API/UI iş oluşturamaz. Capability satırı yalnız bağlantı, environment, seller/company, API version, scope, kaynak URL, doğrulama tarihi ve evidence note ile `SUPPORTED` yapılır.
