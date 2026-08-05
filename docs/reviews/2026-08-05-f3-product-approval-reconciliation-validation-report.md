# Ravencia MarketplaceHub — F3 Product Create Onay Uzlaştırması Doğrulama Raporu

**İnceleme tarihi:** 2026-08-05
**İncelenen dal:** `production-hardening-v7`
**Başlangıç HEAD:** `303c5cd`
**Kapsam:** Product Create batch kabulünden sonra approved/unapproved barkod read-back, onay/ret/pending durum uzlaştırması, uzak kimlik linkleri, kısmi batch davranışı, Worker dispatch ve proje belgesi uyumu

## 1. Sonuç özeti

Önceki durumda Product Create batch içindeki tüm satırlar başarılı olduğunda profil `APPROVAL_PENDING` durumunda kalıyor, fakat Trendyol onay sonucu tekrar okunmadığı için `LIVE` veya `REJECTED` durumu kesinleştirilemiyordu. Kısmi batch içinde kabul edilen satırlar için de ayrı onay izleme işi oluşmuyordu.

Bu çalışma ile aşağıdaki durable read-back zinciri kodlandı:

1. Create batch içinde en az bir satır kabul edilirse `TRENDYOL_PRODUCT_APPROVAL_RECONCILE` işi oluşturulur.
2. Batch aşamasında `CREATE_REJECTED` olan satırlar kendi hata kodlarıyla korunur ve onay read-back dışında bırakılır.
3. Worker kabul edilen her barkodu önce `products/approved` servisinde arar.
4. Barkod approved cevapta bire bir bulunmazsa `products/unapproved` servisine fallback yapılır.
5. Approved satırın `contentId` ve `variantId` kimlikleri doğrulanıp tenant + connection kapsamlı link tablolarına idempotent kaydedilir.
6. `pendingApproval` veya iki listede henüz görünmeyen barkod pending kalır ve yeniden denenir.
7. `rejected` satırın ret nedeni korunur; archived, locked, blacklisted, bilinmeyen durum ve kimlik çatışması fail-closed biçimde manuel incelemeye gider.
8. Approval job payload hash’i güncel listing state ile karşılaştırılır; daha yeni yayın denemesi varsa eski iş `PRODUCT_APPROVAL_SUPERSEDED` ile uzak sorgu ve mutasyon yapmadan durur.
9. Deadline, contract veya kimlik hatasında yalnız create aşamasında kabul edilmiş satırlar incelemeye alınır; önceden `CREATE_REJECTED` olan satırların kanıtı ezilmez.
10. Read-back akışı yeni bir dış yazma etkisi üretmez.

**Production kararı değişmemiştir: `BLOCKED`.** Kod, fixture, test ve belgeler güncellenmiştir; exact .NET/PostgreSQL, Docker ve gerçek Trendyol Stage kanıtı bulunmadan F3 kapanmış sayılmaz.

## 2. Resmî sözleşme dayanağı

| Sözleşme | Uygulanan davranış |
| --- | --- |
| Approved product filter | `GET product/sellers/{sellerId}/products/approved`, barkod filtresi, sayfa boyutu en fazla 100; response içindeki `contentId`, `variantId`, `barcode`, `locked`, `archived`, `blacklisted` alanları kullanılır. |
| Unapproved product filter | `GET product/sellers/{sellerId}/products/unapproved`, barkod filtresi, sayfa boyutu en fazla 1000; `status` için `rejected` ve `pendingApproval`, ayrıca `rejectReasonDetails` kullanılır. |
| Görünürlük fallback'i | Barkod approved cevapta yoksa unapproved okunur. Her iki cevapta da yoksa doğrudan ret verilmez; geçici görünürlük gecikmesi olarak `NOT_FOUND -> APPROVAL_PENDING` uygulanır. |

Resmî kaynaklar:

- <https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2>
- <https://developers.trendyol.com/v2.0/docs/product-filtering-unapproved-products-v2>

## 3. Uygulanan teknik kapsam

| Alan | Son durum |
| --- | --- |
| Application sözleşmesi | `ProductApprovalReconciliationJobPayload`, `RemotePublicationStatus` ve `IProductPort.GetPublicationStatusAsync` eklendi. |
| Adapter endpoint'i | Approved barkod sorgusu ve bire bir sonuç yoksa unapproved fallback eklendi. |
| Mapper | Approved content/variant kimliği ile archived/locked/blacklisted; unapproved pending/rejected ve ilk ret nedeni ayrıştırılır. |
| Worker dispatch | Yeni job türü F3 processor zincirine ve Worker dispatch allow-list'ine eklendi. |
| Job üretimi | Tam veya kısmi create batch içinde en az bir kabul edilen satır varsa deterministic dedup anahtarıyla onay işi oluşturulur. |
| Polling sınırı | Pending/not-found 5 dakika sonra retry edilir. Yedi günlük deadline Trendyol SLA'sı olarak değil sonsuz otomatik polling'i durduran yerel operasyon koruması olarak uygulanır. |
| Satır durumu | `LIVE`, `APPROVAL_PENDING`, `REJECTED`, `ARCHIVED`, `LOCKED`, `BLACKLISTED`, `MANUAL_REVIEW`; create aşamasında reddedilen satır `CREATE_REJECTED` olarak korunur. |
| Profil durumu | `LIVE`, `APPROVAL_PENDING`, `APPROVAL_PARTIAL_PENDING`, `REJECTED`, `PARTIAL_REJECTED`, `MANUAL_REVIEW`. |
| Uzak kimlik | Onaylı content/variant kimlikleri `MarketplaceProductLink` ve `MarketplaceVariantLink` kayıtlarına yazılır. |
| Kimlik güvenliği | Uzak kimliğin başka yerel kayda bağlı olması veya yerel kaydın farklı uzak kimlikle bağlı olması otomatik rewire edilmez. |
| Stale-job güvenliği | Approval payload hash’i güncel listing state ile eşleşmiyorsa job superseded kabul edilir; adapter read çağrısı ve yerel durum değişikliği yapılmaz. |
| Publication status | Son Product Create veya approval reconciliation job'ı publication-status görünümünde seçilebilir. |

## 4. Fail-closed kararlar

- Approved yanıtında `contentId` veya `variantId` eksikse başarı kaydedilmez.
- Tek yerel ürünün kabul edilen varyantları birden fazla `contentId` altında görünürse `PRODUCT_APPROVAL_CONTENT_SPLIT` ile manuel inceleme gerekir.
- İki barkod aynı `variantId` ile dönerse sözleşme geçersiz kabul edilir.
- Aynı uzak content/variant kimliği başka yerel kayda bağlıysa link değiştirilmez.
- Yerel ürün/varyant zaten farklı uzak kimlikle bağlıysa sessiz yeniden bağlama yapılmaz.
- Job payload hash’i güncel listing state hash’inden eskiyse job superseded kabul edilir ve uzak servise çıkılmaz.
- Adapter contract veya kalıcı hata, profile ve kabul edilmiş satırları doğrudan yanlış başarıya taşımaz; create aşamasında reddedilmiş satırlar kendi ret kanıtıyla korunur.
- Approved ve unapproved listelerinde henüz görünmeyen barkod ret sayılmaz.
- Onay read-back çağrıları salt-okunurdur; `ExternalEffectRecord` veya yeni marketplace write oluşturmaz.

## 5. Kodlanan doğrulama senaryoları

| Senaryo | Beklenen sonuç |
| --- | --- |
| Tam create batch kabulü | Approval job oluşur; approved cevapla profile/satır `LIVE`, content/variant linkleri kaydedilir. |
| Kısmi create batch | Kabul edilen satır read-back ile `LIVE`; batch reddi alan satır `CREATE_REJECTED` kalır; profile `PARTIAL_REJECTED`. |
| Onay sürecinde kısmi ret | Bir satır `LIVE`, diğer satır `REJECTED`; profile `PARTIAL_REJECTED`, ret nedeni korunur. |
| İki listede görünmeyen barkod | `APPROVAL_PENDING`, job retry; uzak kimlik linki oluşmaz. |
| Önceden farklı kimliğe bağlı yerel kayıt | `PRODUCT_APPROVAL_IDENTITY_CONFLICT`, `MANUAL_REVIEW`; mevcut linkler değiştirilmez. |
| Daha yeni publication payload’ı | Eski approval job `PRODUCT_APPROVAL_SUPERSEDED` ile durur; remote read sayısı sıfır, güncel profile/listing state değişmez. |
| Approved fixture | Barkod, `contentId`, `variantId` ve approved durumu ayrıştırılır. |
| Unapproved fixture | `rejected`, ret nedeni ve `pendingApproval` ayrıştırılır. |

## 6. Doğrulama durumu

| Kontrol | Sonuç | Sınır |
| --- | --- | --- |
| C# lexical delimiter taraması | `PASS_STATIC` | 140 C# dosyası; compiler/typecheck yerine geçmez. |
| JSON parse | `PASS_STATIC` | 34 JSON dosyası, yeni anonim unapproved fixture dahil. |
| YAML parse | `PASS_STATIC` | 7 YAML/YML dosyası. |
| XML/MSBuild parse | `PASS_STATIC` | 15 proje/props/targets/XML dosyası. |
| Shell syntax | `PASS_STATIC` | 5 shell dosyası `bash -n`. |
| Python syntax | `PASS_STATIC` | 2 Python scripti AST ile ayrıştırıldı. |
| Repository cleanliness | `PASS` | Yasak build/cache/secret çıktısı bulunmadı. |
| Dokümantasyon transaction | `PASS` | Ana plan 6.3, status/current phase, F3 evidence, capability, risk, traceability ve changelog birlikte güncellendi. |
| Git whitespace | `PASS` | `git diff --check`. |
| `.NET restore/build/test/format` | `BLOCKED_ENVIRONMENT` | `dotnet` kurulu değil; repository exact SDK `10.0.302` ister. Resmî binary bulundu ancak araç gzip indirmesini reddetti; kabuk ortamında ilgili host için DNS çözümlemesi yoktu. |
| PostgreSQL/Testcontainers | `BLOCKED_ENVIRONMENT` | Docker CLI/daemon kurulu değil. |
| Frontend exact toolchain | `BLOCKED_ENVIRONMENT` | Ortam Node `22.16.0`/npm `10.9.2`; proje Node `24.18.1`/npm `11.12.1` ister. Bu geliştirme frontend değiştirmedi. |
| Trendyol Stage read-back | `BLOCKED_EXTERNAL` | Gerçek Stage credential, kontrollü create barkodu, onay/ret örneği ve operasyon onayı gerekir. |

## 7. Açık kalan zorunlu işler

1. Exact .NET `10.0.302` ve Docker/PostgreSQL ortamında contract ve End-to-End testleri çalıştırmak.
2. Gerçek Trendyol Stage barkodunda create batch sonrası approved/unapproved görünürlük gecikmesini ve terminal sonucu ölçmek.
3. Publication status ve job takip ekranında approval job, pending, ret nedeni ve uzak kimlikleri kullanıcıya görünür kılmak.
4. Deadline sonrası yeni bir kontrollü reconciliation job üretme/operatör prosedürünü tamamlamak.
5. Product Update ve uzak archive komutlarını Product Create sözleşmesinden ayrı uygulamak.
6. Stok ve fiyatı resmî birleşik `price-and-inventory` komutuyla tamamlamak.
7. F3 safe-write/read-back, rollback ve capability kapatma kanıtını kaydetmek.

## 8. Son karar

Product Create batch sonucu ile gerçek Trendyol ürün onayı arasındaki önceki boşluk kaynak kodu ve dokümantasyon düzeyinde kapatılmıştır. Akış approved -> unapproved fallback, pending görünürlük koruması, satır/profile durumları, uzak kimlik kalıcılığı, kısmi create batch desteği ve fail-closed identity conflict sınırı içerir.

Dinamik toolchain ve Stage kanıtı bulunmadığından sonuç **`CODED_STATIC_VERIFIED / DYNAMIC_AND_STAGE_REVALIDATION_REQUIRED`** olarak tutulur. Production açma kararı verilmemiştir.
