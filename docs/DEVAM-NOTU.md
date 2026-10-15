# Devam notu (15 Ekim 2026)

Bu not, başka bir bilgisayarda yeni bir Claude oturumuyla kaldığı yerden devam etmek için yazıldı. Yeni oturuma ilk mesaj olarak: "docs/DEVAM-NOTU.md dosyasını oku ve kaldığın yerden devam et" yazın.

## Çalışma kuralları

- Türkçe konuş.
- Biten her değişikliği sormadan `main`'e commit + push et. Push sonrası GitHub Actions "Validate" (build + test + PostgreSQL testleri) çalışır; geçerse "Deploy" otomatik olarak sunucuya çıkar. Deploy bitince sonucu kullanıcıya bildir.
- Commit mesajı sonuna: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- Değişiklikleri küçük, geri alınabilir commit'lere böl. Validate sonucunu görmeden riskli değişiklikleri üst üste push'lama. `main` için CI'da aynı anda tek Validate çalışır; bekleyen eski çalışma yenisi gelince iptal edilir (sorun değil, son commit hepsini kapsar).
- Deploy, Validate'i geçen commit'i sunucuya çıkarır. Sunucuya gerçekten neyin çıktığını çalışan konteynerin imaj etiketinden doğrula (`marketplacehub-app:manual-<commit>`), git HEAD'den değil.
- Sunucuda yalnız salt-okunur kontrol yap: `docker logs`, `BEGIN READ ONLY; ... ROLLBACK;` ile psql. Hiçbir şeyi değiştirme.

## Yeni bilgisayarda kurulum

1. Repoyu klonla: `git clone https://github.com/emrekks/RavenciaEntegrasyon.git`. Push yetkisi olan hesapla giriş yapılmalı (repo `emrekks` hesabında).
2. GitHub CLI kur ve `gh auth login` ile giriş yap. Actions takibi `gh run list -R emrekks/RavenciaEntegrasyon` ile yapılır; anonim API kotası (saatte 60) hızla doluyor.
3. Mümkünse .NET 10 SDK ve Node kur. Önceki bilgisayarda yoktu, derleme ve testler yalnız CI'da doğrulandı.
4. Sunucu: `ubuntu@63.180.140.51`, SSH anahtarı `ravencia.pem`. Anahtarı güvenli yoldan (USB) taşı; e-posta, sohbet ya da repoya koyma. Docker komutları `sudo -n docker ...` ile çalışır. Konteynerler: `marketplacehub-api-1`, `-worker-1`, `-postgres-1`, `-caddy-1`. Sunucudaki repo: `/home/ubuntu/RavenciaEntegrasyon`.
   - Veritabanı sorgusu örneği: `cat sorgu.sql | ssh -i ravencia.pem ubuntu@63.180.140.51 'sudo -n docker exec -i marketplacehub-postgres-1 sh -c "psql -U \"\${POSTGRES_USER:-postgres}\" -d \"\${POSTGRES_DB:-postgres}\" -At"'`
   - Worker logları Serilog compact JSON; konteyner her deploy'da yeniden oluşturulduğu için eski loglar silinir.

## Durum

Sunucuda `aa57d0ba` çalışıyor (API ve worker sağlıklı). Aşağıdakilerin hepsi `main`'de ve canlıda.

### Worker (önceki oturumlar)
- Faz 0 tamam: worker kendi appsettings'ini yükler, kapanmada iş deneme hakkı yakmadan kuyruğa döner, varsayılan 60 dk timeout, bağımsız lease reaper, meşgul bağlantıda işler `Deferred` (deneme iadeli), merkezi `JobPriorityPolicy`.
- Faz 1: paralel şerit slotları (`Worker:LaneSlots:<şerit>`), işi almadan bağlantı/kilit grubu kontrolü, müşteriler arası adalet, LISTEN/NOTIFY ile uyandırma, kota dolan bağlantıyı geri çekme, kuyruk metrikleri, takılan kuyruk sağlık kontrolü, eski işlerin temizliği (bunların bir kısmı ikinci bilgisayardaki oturumda yapıldı).

### N11 (bu oturum)
- Kanallar arası stok: `STOCK_RECONCILE_*` N11'de açık (dış yazma + STOCK_WRITE açıksa gönderir). Bütün görevi reddeden `REJECT` artık "reddedildi" sayılıyor.
- Hesap bazlı ortak hız sınırı (`N11RateLimitHandler`): siparişler 600/dk, ürün/görev 120/dk, kategori 30/dk, SOAP 60/dk, soru listesi 1/dk. Ayarlar `N11:*RequestsPerMinute`.
- Sipariş mutabakatı: `ORDER_RECONCILE_*` N11'de açık.
- Kargo: ilk kabul edilen paket işleminden (PICKING/SPLIT/MULTI_SPLIT) sonra SHIPMENT_WRITE kanıtı kaydediliyor.
- Fatura linki: SOAP `SaveLinkSellerInvoice`. N11 linkin uzantı ile bitmesini istediği için API belgeyi `.../invoice.pdf` olarak da sunuyor. Bağlantıda "Fatura oluşturma" açılmadıkça çalışmaz.
- İadeler: SOAP `ClaimReturnList` ile okuma (45 gün, ilk seferde 90 gün), `ClaimReturnApprove` ile onay. İade reddi N11 panelinden yapılıyor (gerekli kargo alanları belgelenmemiş).
- Müşteri soruları: SOAP `GetProductQuestionList` / `GetProductQuestionDetail` / `SaveProductAnswer`; her çalışmada tek liste sayfası.

### İlk canlı kullanımda doğrulanacaklar (N11 belgesinde netleşmeyenler)
- Fatura `invoiceDateTime` biçimi: `dd/MM/yyyy HH:mm` (Türkiye saati) varsayıldı.
- İade ve soru listelerinde tarih biçimi: `dd/MM/yyyy` varsayıldı; iade listesinde `status=ALL` gönderiliyor.
- Soru listesinde WSDL `productId`'yi zorunlu gösteriyor; gönderilmiyor. İlk soru senkronunun hata verip vermediğine bak (Sorular sayfası senkron durumu).
- Bunlar hatalıysa ilgili iş hata mesajıyla düşer; yanlış veri yazılmaz.

## Yapılacaklar (sırayla)

1. **N11 ürün yükleme** — `main`'de ve canlıda (da5f398c). Dış yazma kapalıyken ya da bağlantıda kargo şablonu boşken hiçbir şey N11'e gitmez. Panelde N11 bağlantı sayfasında "N11 ürün açma ayarları" (kargo şablonu adı + hazırlık süresi) eklendi; 21 birim testi yazıldı.
   - **Kalan:** ilk canlı deneme kullanıcıyla birlikte yapılacak (yayın işi → `CreateAsync` → görev sorgusu → onay takibi → `MarketplaceVariantLinks.ExternalId` = stok kodu). N11'de test ortamı yok; tek ürünle, izleyerek açın. Ürün güncelleme (`product-update`), satıştan kaldırma/açma, silme ve SearchCatalog hızlı yükleme henüz yok.
   - Sözleşme: `POST ms/product/tasks/product-create`, en fazla 1000 SKU, marka = özellik id 1 (`customValue`), `stockCode` = panel SKU, sonuç `ms/product/task-details/page-query` ile. Ayrıntı: `wip/n11-urun-yayinlama` dalındaki `docs/N11-URUN-YAYINLAMA-WIP.md`.
2. **N11 kargo ekleri:** adetle bölme + kısmi iptal (`POST rest/delivery/v1/splitPackageByQuantity`, iptal nedenleri 61–65), işçilik bedeli (`PUT rest/order/v1/labor-costs`), toplama talebi (HLZ/CEVA), kampanya kodlu etiket.
3. **N11 sipariş detayı alanları:** `cargoSenderNumber`, `cargoTrackingLink`, `deliveryAddressType`, komisyon, N11 indirimi (model + ekran).
4. **N11 hata mesajı eşleyici** (gerçek hata metinleri görüldükçe).
5. **Worker:** `TRENDYOL_ORDER_INVOICE_RECONCILIATION` ve `TRENDYOL_ORDER_SYNC` işlerinde saatlik `LEASE_EXPIRED` vardı (heartbeat başarısız olduğu düşünülüyor). İkinci bilgisayardaki worker değişikliklerinden sonra hâlâ oluyor mu, `integration.job_attempts` üzerinden kontrol et; oluyorsa loglardan kanıt alıp düzelt.
6. **Kategori/özellik eşleştirme otomasyonu:** plan sunuldu, kullanıcı hangi aşamadan başlanacağını henüz seçmedi (risk düzeltmeleri → kategoriden bağımsız değer şablonları → öneri motoru).

## Notlar
- Eski bilgisayarda `stash@{0}` ("Preserve existing local edits before Ravencia invoice and returns fixes", 8 Ekim) yalnız o bilgisayarda; kullanıcı bırakılmasını istedi.
- `docs/N11-ENTEGRASYON-PLANI.md` N11 planının tamamı; üstteki durum bölümü bu nota göre güncel tutulmalı.
