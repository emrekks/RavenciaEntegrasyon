# Test kapsamı, belge-kod sapması, Telemetri ve Realtime (SignalR) denetimi

Kapsam: `tests/MarketplaceHub.Application.Tests` (327 .cs dosyası), web `*.test.ts(x)` (252 dosya), `docs/`, `.claude/skills/marketplace-docs/*`, `src/MarketplaceHub.Infrastructure/Telemetry`, `src/MarketplaceHub.Api/Realtime` ve onları besleyen outbox (`AppDbContext.AppendDataChangeOutboxEvents`) ile istemci (`useOperationsRealtime.ts`).
Yöntem: Yalnız kod okuması yapıldı. dotnet kurulu değil, testler koşturulmadı. Canlı sunucuya bakılmadı.
Önceki notlar (`research_notes/Ravencia proje hata denetimi/*.md`) tarandı. Orada geçen bulgular tekrar edilmedi. Yalnız "bir test o hatayı doğru diye sabitliyor" ya da "belge ondan farklı söylüyor" gibi yeni bir açı olduğunda onlara atıf yapıldı.
Etiketler: şiddet **Kritik/Yüksek/Orta/Düşük**, güven **[Kesin]/[Muhtemel]/[Tahmin]**.

## 1. Test paketi: kritik davranışlarda kapsam boşlukları, yanlış davranışı sabitleyen testler, başarısız olamayan testler, atlanan testler

### Takeaway
Test paketi geniş. Para/KDV (`InvoiceAmounts` 9 dosya), MFA uçtan uca, oturum, kiracı izolasyonu, İstanbul saati (`TurkeyTime` 5 dosya) ve outbox yeniden teslimi test ediliyor. PostgreSQL testleri CI'da gerçekten koşuyor. Zayıf noktalar başka yerde:
- Belgede "zorunlu senaryo" diye listelenen testlerin bir kısmı hiçbir davranışı ölçmüyor.
- Üç test, önceki denetimde hata diye raporlanmış davranışı "doğru" diye sabitliyor.
- Realtime istemcisinin kopma sonrası yenileme yolu, telemetrinin zaman aşımı sınıflaması, MFA kapatma ve kurtarma kodunun ikinci kullanımı hiç test edilmiyor.

### Cited Findings

**Başarısız olamayan / ölçtüğünü iddia ettiği şeyi ölçmeyen testler**
- **[Orta][Kesin] "Zorunlu senaryo" testleri ölçtüğünü söylediği davranışı ölçmüyor.**
  - `Scenario04_WorkerOfflineFortyFiveDays_RecoveryLaneRemainsSeparateFromHotLane` yalnız iki sabit metnin farklı olduğunu doğruluyor (`Assert.NotEqual(MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync)`). 45 günlük kesintiden kurtarma mantığına hiç dokunmuyor.
  - `Scenario05_PartialCancel_ReleasesOnlyCancelledLine` bir siparişi kısmen iptal etmiyor. `DesiredQuantity(1,0)`, `(1,1)`, `(1,0)` çağrılarını tek tek deniyor.
  - `Scenario02` aynı saf fonksiyonu 10 kez aynı girdiyle çağırıyor.
  - Bu testler politika gerçekten bozulsa da geçer. — [tests/.../MandatorySynchronizationScenariosTests.cs:19-41](tests/MarketplaceHub.Application.Tests/MandatorySynchronizationScenariosTests.cs)
- **[Düşük][Kesin] Var olmayan bir metodun yokluğunu doğrulayan test.** `TheRealtimeDispatcherNoLongerScansForNewJobs`, reflection ile `AnnounceNewJobsAsync` metodunun bulunmadığını ve `JobEventResource == "jobs"` olduğunu doğruluyor. Bu test davranış değil, isim denetliyor. Aynı işi farklı adlı bir metod yeniden yapsa da geçer. — [tests/.../BackgroundProcessingAuditTests.cs:325-332](tests/MarketplaceHub.Application.Tests/BackgroundProcessingAuditTests.cs)

**Önceki denetimde hata sayılan davranışı "doğru" diye sabitleyen testler** (hata ayrıntısı önceki notlarda var; burada yeni olan, testin düzeltmeyi kırmızıya çevirecek olması)
- **[Orta][Kesin] KDV oranı bilinmezliği.** `TheInvoiceGateStopsALineWhoseMarketplaceDidNotReportItsVatRate` şu girdiler için `false` ("biliniyor") bekliyor: `("HEPSIBURADA", null)`, `("N11", "{}")`, `("PAZARAMA", "not json")`. Bu davranış domain_application.md'de §3 Orta olarak raporlanmıştı. Sistem belgesi de "KDV oranı bilinmeyen satır varsa fatura kesilmez" diyor. Düzeltme bu üç InlineData'yı kırar. — [tests/.../OrderLineVatRateTests.cs:93-98](tests/MarketplaceHub.Application.Tests/OrderLineVatRateTests.cs); [docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md:504](docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md)
- **[Düşük][Kesin] HB `Undelivered → OnHold` eşlemesi** "documented statuses" adıyla test ediliyor (önceki §4 bulgusu). — [tests/.../MandatorySynchronizationScenariosTests.cs:149](tests/MarketplaceHub.Application.Tests/MandatorySynchronizationScenariosTests.cs)
- **[Düşük][Kesin] Ayar yoksa faturalama açık sayılıyor**, ve bu yalnız HB için değil TRENDYOL için de sabitlenmiş: `MissingSettingKeepsExistingMarketplaceConnectionsEnabled("TRENDYOL"|"HEPSIBURADA")`. Belge ise yalnız HB'yi istisna sayıyor (bkz. §2). — [tests/.../MarketplaceInvoiceCreationPolicyTests.cs:8-14](tests/MarketplaceHub.Application.Tests/MarketplaceInvoiceCreationPolicyTests.cs)

**Kapsam boşlukları (testi hiç olmayan kritik yollar)**
- **[Orta][Kesin] Realtime istemcisinin tam kopma sonrası yeniden başlaması test edilmiyor.** `useOperationsRealtime.test.tsx` başlatma denemelerini, 401'i ve aç/kapa'yı sınıyor. Ama `onclose → start()` sonrasında sorguların yenilenip yenilenmediğini sınamıyor. Bu yolda gerçek bir hata var (bkz. §3, R1). — [src/MarketplaceHub.Web/src/app/hooks/useOperationsRealtime.test.tsx:33-67](src/MarketplaceHub.Web/src/app/hooks/useOperationsRealtime.test.tsx)
- **[Düşük][Kesin] Telemetri testi tek bir durum sınıyor.** `MarketplaceTelemetryTests` yalnız 429 durum etiketini sınıyor. İptal, zaman aşımı ve istisna yolları (`status="cancelled"`/`"error"`) ve süre histogramı test edilmiyor. — [tests/.../MarketplaceTelemetryTests.cs:10-35](tests/MarketplaceHub.Application.Tests/MarketplaceTelemetryTests.cs)
- **[Düşük][Kesin] MFA'da iki yol testsiz.**
  - `POST /auth/mfa/disable` hiçbir testte geçmiyor. Grep sonucu: `mfa/disable|MfaDisable` için 0 dosya.
  - Kullanılmış bir kurtarma kodunun ikinci kez reddedildiğini doğrulayan test yok. Tüketim atomik (`ExecuteUpdate ... UsedAt == null`), yani kod doğru görünüyor, ama regresyon koruması yok.
  - Var olan testler kurulum, onay, challenge, 5 hatada oturum kapanması ve faktör değişimini kapsıyor.
  - Kaynaklar: [src/MarketplaceHub.Api/Security/AuthEndpoints.cs:35,451-453](src/MarketplaceHub.Api/Security/AuthEndpoints.cs); [tests/.../ApiSecurityEndToEndTests.cs:23-75](tests/MarketplaceHub.Application.Tests/ApiSecurityEndToEndTests.cs); [tests/.../AuthSecurityTests.cs:18-34](tests/MarketplaceHub.Application.Tests/AuthSecurityTests.cs)
- **[Düşük][Kesin] Fiyat girişi testinde Türkçe binlik eksik.** `normalizePriceInput` testi `'1.249,90'`, `'249,5 ₺'` ve `'249.5'` girdilerini sınıyor. Önceki frontend_part2 Yüksek bulgusundaki virgülsüz Türkçe binlik (`'1.250'`) hiç sınanmıyor. Hata bu yüzden yakalanmadı. — [src/MarketplaceHub.Web/src/features/catalog/quick-price-edit.test.ts:266-268](src/MarketplaceHub.Web/src/features/catalog/quick-price-edit.test.ts)
- **[Düşük][Kesin] Outbox dışı yazımların realtime olayı üretmemesi test edilmiyor** (bkz. §3, R2).

**Atlanan testler**
- **[Bilgi][Kesin] Atlanan testler CI'da koşuyor.** Kalıcı `Skip` ya da `it.skip` yok.
  - `PostgreSqlFact` ve `PostgreSqlTheory`, `MARKETPLACEHUB_TEST_CONNECTION` yoksa atlanıyor. CI bu değişkeni `backend-postgres` işinde veriyor ve `Category=PostgreSql` filtresiyle koşuyor. — [tests/.../PostgreSqlTenantIsolationTests.cs:9346-9369](tests/MarketplaceHub.Application.Tests/PostgreSqlTenantIsolationTests.cs); [.github/workflows/ci.yml:247,309](.github/workflows/ci.yml)
  - `ApiEndToEndFixture.InitializeAsync` bağlantı yoksa sessizce `return` ediyor. Bu fixture'ı kullanan testler `PostgreSqlFact` olduğu için yerelde atlanıyor, CI'da koşuyor. — [tests/.../ApiEndToEndTests.cs:215,225](tests/MarketplaceHub.Application.Tests/ApiEndToEndTests.cs)
  - Web'deki tek koşullu atlama `job-categories.test.ts:19` (`it.skipIf(!serverSourceAvailable)`). CI checkout'ta kaynak olduğu için koşuyor. Yalnız edge imaj derlemesinde atlanıyor. — [src/MarketplaceHub.Web/src/features/operations/job-categories.test.ts:4-23](src/MarketplaceHub.Web/src/features/operations/job-categories.test.ts)

**Kontrol edildi, iyi kapsanmış**
- Outbox'ın "gönderildi ama işaretlenmeden durdu" durumunda aynı `eventId` ile yeniden teslimi gerçek PostgreSQL'de test ediliyor. — [tests/.../PostgreSqlTenantIsolationTests.cs:342](tests/MarketplaceHub.Application.Tests/PostgreSqlTenantIsolationTests.cs)
- Hub oturum doğrulama sorgusunun HTTP oturum denetimiyle eşdeğerliği test ediliyor. — [tests/.../OperationsHubSessionTests.cs:13-38](tests/MarketplaceHub.Application.Tests/OperationsHubSessionTests.cs)

### Inferences
- Belgenin (TRENDYOL-SYNC-ARCHITECTURE.md:104) "zorunlu senaryolar kapsanıyor" iddiası, tautolojik testler yüzünden gerçekte olduğundan güçlü bir güvence izlenimi veriyor.
- Önceki denetimin KDV ve faturalama bulguları düzeltilirken ilgili InlineData satırları da bilerek değiştirilmeli. Aksi halde CI düzeltmeyi reddeder.

### Gaps
- 327 backend test dosyasının tamamı satır satır okunmadı. Boşluk taraması hedefli grep ve örnekleme ile yapıldı. Para/KDV, fatura teslimi ve cursor pencerelerinde testi olmayan başka bir dal kalmış olabilir.
- Testler koşturulmadı (dotnet yok). Hangilerinin bugün yeşil olduğu doğrulanmadı.

## 2. Belge-kod sapması (docs/ ve .claude/skills/marketplace-docs)

### Takeaway
marketplace-docs'ta kontrol edilen sayılar kodla uyumlu: n11 14/15 gün, Pazarama 28 gün ve 60/30 dk kotası, PTT AVM 35/40 gün, Shopify 45 sn zaman aşımı, HB UNDELIVERED→OnHold. Sapmalar çoğunlukla `docs/` altında:
- Senkron sağlık eşikleri belgedekinin yarısı.
- Realtime'ın ne zaman tetiklendiği yanlış anlatılıyor.
- Faturalama varsayılanı, veri silme onayı ve CI ayrıntıları kodla tutarsız.

### Cited Findings
- **[Orta][Kesin] Senkron sağlık eşikleri belgede farklı.**
  - Belge: Degraded varsayılanı 600 sn, Offline 1800 sn.
  - Kod ve appsettings: Degraded 300 sn, Offline 900 sn.
  - Sonuç: panel bir bağlantıyı belgenin söylediğinin yarısı kadar sürede "çevrimdışı" gösteriyor. Belgeye göre alarm yorumlayan biri yanılır.
  - Kaynaklar: [docs/TRENDYOL-SYNC-ARCHITECTURE.md:96-98](docs/TRENDYOL-SYNC-ARCHITECTURE.md); [src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs:618-620](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs); [src/MarketplaceHub.Api/appsettings.json:7](src/MarketplaceHub.Api/appsettings.json); [src/MarketplaceHub.Worker/appsettings.json:21](src/MarketplaceHub.Worker/appsettings.json)
- **[Düşük][Kesin] Realtime'ın tetiklenme anı yanlış anlatılıyor.**
  - Belge: "Her başarılı job sonrasında SignalR invalidation gönderilir" ve "tamamlanan job sonrası ... sipariş, iade, ürün, stok ve işlem takibi sorgularını yeniden doğrular".
  - Kod: olaylar job bitişinde değil, `SaveChanges` sırasında izlenen varlık değiştikçe outbox'a yazılıyor. Job durum değişiklikleri yalnız `"jobs"` kaynağıyla yayınlanıyor. İstemci `"jobs"` olayında yalnız `['jobs']` ve `['job']` sorgularını yeniliyor.
  - Kaynaklar: [docs/TRENDYOL-SYNC-ARCHITECTURE.md:48,100](docs/TRENDYOL-SYNC-ARCHITECTURE.md); [src/MarketplaceHub.Infrastructure/Persistence/AppDbContext.cs:214-267](src/MarketplaceHub.Infrastructure/Persistence/AppDbContext.cs); [src/MarketplaceHub.Infrastructure/Persistence/JobLeaseService.cs:405-407](src/MarketplaceHub.Infrastructure/Persistence/JobLeaseService.cs); [src/MarketplaceHub.Web/src/app/realtime-query-keys.ts:19,34](src/MarketplaceHub.Web/src/app/realtime-query-keys.ts)
- **[Orta][Kesin] "Zorunlu senaryolar" listesinin karşılığı yok.**
  - Belge testin şu senaryoları kapsadığını söylüyor: out-of-order status, dört saat worker kesintisi, aynı sipariş numarası, güvenlik örtüşmesi, tekrar webhook, çift worker lease, Trendyol dışı provider sınırı.
  - Dosyada Scenario03, 08 ve 09 yok. 01-15 numaralı testlerden birkaçı da tautolojik (§1).
  - Out-of-order için tek eşleşme başka bir dosyada: `PackageIngestionSafetyTests.cs:165`.
  - Kaynaklar: [docs/TRENDYOL-SYNC-ARCHITECTURE.md:104](docs/TRENDYOL-SYNC-ARCHITECTURE.md); [tests/.../MandatorySynchronizationScenariosTests.cs:13-264](tests/MarketplaceHub.Application.Tests/MandatorySynchronizationScenariosTests.cs)
- **[Düşük][Kesin] Faturalama varsayılanında istisnanın kapsamı farklı.**
  - Belge: "Hepsiburada'nın eski kayıtlarında alan yoktur ve açık okunur."
  - Kod ve test: alan yoksa Shopify dışındaki **bütün** pazaryerlerinde (TRENDYOL dahil) faturalama açık sayılıyor. Kod davranışı önceki notta raporlanmıştı; burada yeni olan belgeyle çelişki.
  - Kaynaklar: [docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md:624](docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md); [tests/.../MarketplaceInvoiceCreationPolicyTests.cs:8-14](tests/MarketplaceHub.Application.Tests/MarketplaceInvoiceCreationPolicyTests.cs)
- **[Düşük][Kesin] Veri silme onay metni farklı.**
  - Belge: "Kullanıcıdan tam olarak `VERİLERİ SİL` onayı alınır."
  - Kod `"Verileri sil"`i de kabul ediyor ve hata mesajı kullanıcıya "tam olarak Verileri sil yazın" diyor.
  - Kaynaklar: [docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md](docs/RAVENCIA-SISTEM-ISLEYIS-BELGESI.md) §14.1; [src/MarketplaceHub.Infrastructure/Persistence/OperationalDataMaintenanceService.cs:20](src/MarketplaceHub.Infrastructure/Persistence/OperationalDataMaintenanceService.cs)
- **[Düşük][Kesin] CI süre sınırları eksik anlatılıyor.** Belge "İşlerin süre sınırı 5 ya da 15 dakika" diyor ve iş listesinde `nuget-cache` yok. ci.yml'de `nuget-cache` işi var ve sınırı 10 dakika. — [docs/CI-CD-DEPLOY.md:527-531](docs/CI-CD-DEPLOY.md); [.github/workflows/ci.yml:316-319](.github/workflows/ci.yml)
- **[Düşük][Muhtemel] "Lint her zaman çalışır" ifadesi yanıltıcı.** Belge "Ön yüz `npm run lint` adımı her zaman çalışır" diyor. Lint, `frontend` işinin içinde koşulsuz bir adım. Ama işin tamamı `needs.plan.outputs.frontend_needed == 'true'` koşuluna bağlı, aynı içerik daha önce geçtiyse atlanıyor. Önceki not içerik-hash atlamasını raporladı; belge ifadesi ayrıca yanıltıcı. — [docs/CI-CD-DEPLOY.md:543](docs/CI-CD-DEPLOY.md); [.github/workflows/ci.yml:417-443](.github/workflows/ci.yml)
- **Kontrol edildi, uyumlu:** n11 en çok 15 gün/kod 14 ve sayfa ≤100; Pazarama 28 gün, 60/30 dk kotası, 2 gün artımlı okuma; PTT AVM 40/35 gün; Shopify 45 sn; HB `UNDELIVERED → OnHold` (belge koda uyuyor, davranışın kendisi önceki notta tartışıldı); migration zaman aşımı 1800 sn; ro oturumunda `statement_timeout` 60 sn; healthwatch 5 dakikada bir; CSS bütçesi 672 KiB; Trivy v0.70.0. Kaynaklar:
  - [.claude/skills/marketplace-docs/n11.md:22](.claude/skills/marketplace-docs/n11.md) ↔ [src/MarketplaceHub.Application/SynchronizationPolicies.cs:24](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
  - [pazarama.md:21-27](.claude/skills/marketplace-docs/pazarama.md) ↔ [PazaramaOptions.cs:16-17](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaOptions.cs) ve [SynchronizationPolicies.cs:49-53](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
  - [pttavm.md:25](.claude/skills/marketplace-docs/pttavm.md) ↔ [SynchronizationPolicies.cs:85](src/MarketplaceHub.Application/SynchronizationPolicies.cs)
  - [shopify.md:18](.claude/skills/marketplace-docs/shopify.md) ↔ [ShopifyOptions.cs:9](src/MarketplaceHub.Infrastructure/Adapters/Shopify/ShopifyOptions.cs)
  - [deploy/scripts/fast-deploy.sh:187](deploy/scripts/fast-deploy.sh); [deploy/scripts/readonly-entrypoint.sh:87](deploy/scripts/readonly-entrypoint.sh); [deploy/systemd/ravencia-healthwatch.timer:7](deploy/systemd/ravencia-healthwatch.timer); [src/MarketplaceHub.Web/scripts/check-bundle-budget.mjs:7](src/MarketplaceHub.Web/scripts/check-bundle-budget.mjs)

### Inferences
- `docs/TRENDYOL-SYNC-ARCHITECTURE.md` en bayat belge: eşikler, realtime modeli ve test listesi kodun gerisinde kalmış. `marketplace-docs` ise bakımlı görünüyor.

### Gaps
- `RUNBOOK-OLAY.md`, `RUNBOOK-GERI-ALMA.md` ve plan belgeleri (N11, Pazarama, PTT AVM, STOK-ESITLEME) satır satır karşılaştırılmadı. Önceki build notu bu runbook'lardaki AWS ve disk referanslarını zaten listeledi.
- `RAVENCIA-SISTEM-ISLEYIS-BELGESI.md` sayısal iddia açısından fakir (çoğu nitel). Fatura durum listesi (§11.3) enum'la birebir eşleştirilmedi.

## 3. Telemetri ve realtime (SignalR): kayıp olaylar, yanlış kiracı dağıtımı, log ve metriklerde kişisel veri

### Takeaway
Kiracı dağıtımı doğru. Grup adı, claim (`Guid.ToString()` = "D") ve yayıncıdaki `ToString("D")` ile aynı biçimde üretiliyor. Bağlantılar dakikalık oturum denetimiyle kapatılıyor. Yük yalnız kimlik ve kaynak adı taşıyor, PII yok. Metrik etiketleri düşük kardinaliteli ve PII'siz.
Asıl sorun kayıp güncellemeler:
- Otomatik yeniden bağlanma pes ettikten sonra istemci elle yeniden bağlanıyor, ama ekranları yenilemiyor.
- Change tracker dışında yapılan silme ve güncellemeler hiç olay üretmiyor.

### Cited Findings
- **R1 — [Orta][Kesin] Uzun kopmadan sonra elle yeniden bağlanma ekranları yenilemiyor.**
  - İstemci `withAutomaticReconnect([0, 1000, 3000, 10_000])` kullanıyor, yani toplam yaklaşık 14 sn deniyor. Yenileme (`realtimeReconnectQueryKeys` invalidation) yalnız `onreconnected` içinde yapılıyor.
  - Otomatik deneme tükenince `onclose → scheduleStart → connection.start()` çalışıyor. SignalR bu yolda `onreconnected` tetiklemiyor, `start()` başarısından sonra da invalidation yok.
  - Sunucu tarafı tekrar oynatma yapmıyor. Olay, bağlı istemci olmasa da `PublishedAt` ile işaretleniyor. Retention yorumu da "panels get no replay on reconnect (they re-read their data)" diyor.
  - Senaryo: deploy sırasında API 30 sn kapalı kalır. Yeni API ayağa kalkınca birikmiş outbox olaylarını 1 sn içinde yayınlar. Tarayıcı ancak sonraki deneme aralığında bağlanır (3 sn ve sonrası). Arada gelen sipariş, fatura ve iade değişiklikleri kaybolur, açık ekranlar staleTime veya elle yenilemeye kadar eski veriyi gösterir.
  - Live host 7/24 açık ve her validated push deploy ediyor, yani bu yol düzenli tetikleniyor.
  - Kaynaklar: [src/MarketplaceHub.Web/src/app/hooks/useOperationsRealtime.ts:23,40-62](src/MarketplaceHub.Web/src/app/hooks/useOperationsRealtime.ts); [src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs:172-179](src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs); [src/MarketplaceHub.Api/Realtime/OperationsRealtimeOutboxRetention.cs:7-17](src/MarketplaceHub.Api/Realtime/OperationsRealtimeOutboxRetention.cs)
- **R2 — [Orta][Kesin] İptal edilen iade talebinin silinmesi realtime olayı üretmiyor.**
  - Outbox olayları yalnız `EntityState.Added/Modified` izlenen varlıklardan üretiliyor. `Deleted` ve `ExecuteUpdate/ExecuteDelete` kapsam dışı.
  - `CancelledReturnClaimCleanup.RemoveAsync` iade talebini ve satırlarını `ExecuteDeleteAsync` ile siliyor. Çağıran yollar (`MarketplaceJobProcessor.RemoveCancelledReturnClaim`, `MarketplaceSalesService` readback) `false` dönünce elle bir `"returns"` olayı eklemiyor. Kıyas: sipariş yolu `AddOrderDataChangeEvent` ile bunu yapıyor.
  - Senaryo: pazaryeri bir iadeyi iptal eder, iade senkronu kaydı siler. Açık İadeler ekranı ve dashboard rozetleri silinen talebi göstermeye devam eder. Yalnız `"jobs"` olayı gelir, o da sadece `['jobs']` sorgusunu yeniler.
  - Kaynaklar: [src/MarketplaceHub.Infrastructure/Persistence/AppDbContext.cs:220-224](src/MarketplaceHub.Infrastructure/Persistence/AppDbContext.cs); [src/MarketplaceHub.Infrastructure/Persistence/CancelledReturnClaimCleanup.cs:17-20,53-63](src/MarketplaceHub.Infrastructure/Persistence/CancelledReturnClaimCleanup.cs); [src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs:12445-12453](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs); karşılaştırma: [MarketplaceJobProcessor.cs:5435-5447,5467-5480](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceJobProcessor.cs)
- **R3 — [Düşük][Kesin] "Etiket basıldı" işareti diğer oturumlara yayılmıyor.** `LabelPrintedAt`, `ExecuteUpdateAsync` ile yazılıyor ve outbox olayı eklenmiyor. İşlemi yapan sekme kendisi yeniliyor, ama başka kullanıcı ya da sekme siparişin etiketinin basıldığını görmüyor. Aynı paket iki kez basılabilir. — [src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs:238-244](src/MarketplaceHub.Infrastructure/Persistence/MarketplaceSalesService.cs); [src/MarketplaceHub.Web/src/features/marketplace/orders/label-printed.ts:12](src/MarketplaceHub.Web/src/features/marketplace/orders/label-printed.ts)
- **R4 — [Düşük][Kesin kod / Tahmin tetiklenme] Birden çok API örneğinde olaylar diğer örneklere ulaşmıyor.**
  - `OperationsRealtimeDispatchLease` "only one API instance dispatches" diyerek çoklu örneğe hazırlanıyor. Ama `AddSignalR` bir backplane (Redis vb.) olmadan kurulmuş.
  - Bu durumda kilidi tutan örnek, `Clients.Group(...)` ile yalnız kendi bağlantılarına gönderir. Olay "published" işaretlendiği için diğer örneğe bağlı tarayıcılar onu hiç almaz.
  - Bugün canlıda tek `marketplacehub-api-1` var, yani etkisi yok. Ölçeklendirme anında sessizce bozulur.
  - Kaynaklar: [src/MarketplaceHub.Api/Realtime/OperationsRealtimeDispatchLease.cs:6-9](src/MarketplaceHub.Api/Realtime/OperationsRealtimeDispatchLease.cs); [src/MarketplaceHub.Api/Program.cs:59](src/MarketplaceHub.Api/Program.cs); [src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs:172-179](src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs)
- **T1 — [Düşük][Kesin] Zaman aşımları metrikte "cancelled" görünüyor.** `MarketplaceRequestMetricsHandler`, `HttpClient.Timeout` kaynaklı `TaskCanceledException`'ı da `OperationCanceledException` olarak yakalıyor ve `status="cancelled"` etiketi veriyor. Pazaryeri zaman aşımları, kapanış veya kullanıcı iptalinden ayırt edilemiyor, zaman aşımı oranı izlenemiyor. — [src/MarketplaceHub.Infrastructure/Telemetry/MarketplaceHubTelemetry.cs:36-56](src/MarketplaceHub.Infrastructure/Telemetry/MarketplaceHubTelemetry.cs)
- **T2 — [Düşük][Kesin] Metrikler hiçbir yere aktarılmıyor.** Kodda OpenTelemetry exporter ya da `AddMeter` yok. Dosyanın kendi yorumu "by an OpenTelemetry exporter once one is added" diyor. Metrikler yalnız bir operatör `dotnet-counters` ile konteynere bağlandığı anda görülebiliyor. Geçmiş, alarm veya pano yok. Belge de böyle anlatıyor. — [src/MarketplaceHub.Infrastructure/Telemetry/MarketplaceHubTelemetry.cs:6-7](src/MarketplaceHub.Infrastructure/Telemetry/MarketplaceHubTelemetry.cs); [docs/CI-CD-DEPLOY.md:481-483](docs/CI-CD-DEPLOY.md)
- **Kontrol edildi, sorun yok:**
  - **Kiracı grubu:** claim `tenantId.ToString()` ile yazılıyor ve hub ham claim'i kullanıyor. Yayıncı `ToString("D")` kullanıyor. İkisi aynı biçimi üretiyor. Kiracı veya oturum geçersizleşince bağlantı en geç 1 dakikada kapanıyor; bu gecikme önceki api_security notunda 1.5 olarak var. Kaynaklar: [src/MarketplaceHub.Api/Security/SessionAuthMiddleware.cs:34](src/MarketplaceHub.Api/Security/SessionAuthMiddleware.cs); [src/MarketplaceHub.Api/Realtime/OperationsHub.cs:14-23,97-109](src/MarketplaceHub.Api/Realtime/OperationsHub.cs)
  - **Origin filtresi:** her hub bağlantısında aynı köken denetimi yapılıyor. — [src/MarketplaceHub.Api/Realtime/HubOriginFilter.cs](src/MarketplaceHub.Api/Realtime/HubOriginFilter.cs)
  - **Yük içeriği:** yayınlanan alanlar `eventId, resource, operation, aggregateId, version, occurredAt`. PII yok. — [src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs:52-60](src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs)
  - **Metrik etiketleri:** `job.type`, `outcome`, `lane`, `lock.group`, `client`, `method`, `status`. URL, sipariş numarası ya da müşteri bilgisi yok. Trace etiketleri yalnız kimlikler. — [src/MarketplaceHub.Worker/Worker.cs:234-235,329-330,377-381](src/MarketplaceHub.Worker/Worker.cs)
  - **Loglar:** e-posta, telefon, adres veya ham gövde loglayan bir `Log*` çağrısı grep'te bulunmadı. Pazarama ve PTT AVM ret logları gövdenin yalnız biçimini ve uzunluğunu yazıyor. `System.Net.Http.HttpClient` logları Warning'e çekilmiş. Kaynaklar: [src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaHttpClient.cs:179](src/MarketplaceHub.Infrastructure/Adapters/Pazarama/PazaramaHttpClient.cs); [src/MarketplaceHub.Api/Program.cs:46](src/MarketplaceHub.Api/Program.cs)
  - **Başarısız gönderim:** olaylar üstel geri çekilmeyle (en çok 60 sn) yeniden deneniyor. Yayımlanmamış olaylar silinmiyor. — [src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs:124-137](src/MarketplaceHub.Api/Realtime/OperationsRealtimeBroadcaster.cs)

### Inferences
- R1 ile R2 aynı türden bir açık: "olay kaçarsa veri yeniden okunur" varsayımı tek bir yola, `onreconnected`'a dayanıyor, ve change tracker dışındaki yazımlar bu varsayımın dışında kalıyor. Elle yapılan `start()` sonrasında da `realtimeReconnectQueryKeys` invalidation'ı çağrılırsa R1 kapanır. Silme ve ExecuteUpdate yollarına elle `"returns"` veya `"orders"` olayı eklenirse R2 ve R3 kapanır.

### Gaps
- `ExecuteUpdate/ExecuteDelete` kullanan 60'tan fazla çağrının hepsi tek tek incelenmedi. Yalnız outbox'a eşlenen varlıklara (Order, ReturnClaim) dokunanlar doğrulandı. `MarketplaceJobProcessor.ListingVerify` (ürün listeleme alanları) ve `OperationalDataMaintenanceService` (toplu silme) gibi yollarda da olay üretilmiyor olabilir [Tahmin].
- Canlıdaki outbox birikimi ve `LastDispatchError` değerleri salt okunur SQL ile kontrol edilmedi.
