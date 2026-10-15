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
2. **N11 kargo ekleri** — tamam (e00e909f). Yeni paket aksiyonları: `SPLIT_BY_QUANTITY` (miktarla bölme + isteğe bağlı `cancelledItems`, neden 61–65), `LABOR_COST` (işçilik bedeli), `COLLECTION_REQUEST` (HLZ/CEVA/BL toplama talebi). Gönderi detayındaki aksiyon formundan ham JSON ile gönderilir (örnekler formun altında); SHIPMENT_WRITE kanıtlanınca hepsi izinli listede görünür. Canlıda denenmedi. Kampanya kodlu etiket ve `ClaimCancelPartial` (SOAP) yapılmadı.
3. **N11 sipariş detayı alanları** — tamam (899d569d). Sipariş detayında (telefon düzeni) "Kargo takip no" (N11 kargo linkiyle), "Teslimat tipi", "Komisyon oranı", "N11 indirimi", "Satıcı indirimi" gösteriliyor; `OrderListView.PlatformInfo` ile gelir (`MarketplaceSalesService.N11OrderInfo`). Canlıda doğrulandı.
4. **N11 hata mesajı eşleyici:** eşlenecek gerçek hata metni henüz toplanmadı. Soru listesindeki "1 dakikada bir" reddi giderildi: SOAP reddi hız sınırı sayılıyor ve 1/dk kovasında %25 ek aralık var (046b7521); soru eşitlemesi hatasız çalışıyor.
5. **Worker `LEASE_EXPIRED`:** son 48 saatte 136 (çoğu 13–14 Ekim gecesi), 15 Ekim 07:00 UTC'den sonra yok; sorun çözülmüş görünüyor. İzlemeye devam et: `integration.job_attempts` üzerinde `ErrorCode='LEASE_EXPIRED'`.
6. **Kategori/özellik eşleştirme otomasyonu:** Aşama 1 (kategoriden bağımsız değer şablonları, platform varsayılanları, "Şimdi eşle") ve Aşama 2 (tek normalizer, eş anlamlılar, güven puanı + toplu onay, platformlar arası köprü, kategori önerisi) `main`'de ve canlıda. N11 eşleştirme sekmesi de çalışıyor (3.722 yaprak kategori). Açık/isteğe bağlı: Aşama 3 (insan onaylı yapay zekâ önerileri) ve Pazarama; başlamadan kullanıcıya sor.

**Dış yazma gerektirenler (kullanıcı onayı şart):** N11 ilk ürün açma, bölme/işçilik/toplama aksiyonları, fiyat-stok toplu gönderimi.

## N11 denetimi (15 Ekim 2026) ve sonrası

Plan-kod denetimi yapıldı. Kapatılanlar: tüm N11 yazmaları adaptörde tek kapıdan geçiyor (`N11HttpClient.WriteBlocked`: genel anahtar + bağlantı anahtarı + canlı ortam + doğrulanmış/aktif bağlantı); işlem takibi N11 bağlantısındaki ortak işleri "N11" gösteriyor; soru ekranında N11 tek seferlik cevap uyarısı ve onayı; N11 paket aksiyonlarında onay penceresi (iptalde ayrı uyarı); AppSecret `ToString`'ten gizli; N11 ürün/görev reddi hata mesajlarına öneri eklendi (`N11ErrorAdvice`); eşleşmeyen iade satırı sorun kaydı açıyor (`N11_RETURN_LINE_UNMATCHED`).

Bilerek yapılmayanlar (karar/doğrulama gerekir):
- **Ürün güncelleme, satıştan kaldırma/açma, silme:** N11 mapping-only ile gelen ürünlerin panelde listing profili yok; mevcut güncelleme/arşiv akışları profile dayanıyor. Önce "profil olmayan N11 ürünleri nasıl yönetilsin" kararı lazım. API: `POST ms/product/tasks/product-update` (status Active/Suspended, preparingDay, shipmentTemplate, description, vatRate, attributes) ve `product-delete`; ayrıntı resmi dokümanda.
- **İade reddi/erteleme:** N11 belgesi gerekli kargo alanlarını vermiyor; N11 satıcı panelinden yapılır.
- **`ClaimCancelPartial` (SOAP), kampanya kodlu etiket, SearchCatalog hızlı yükleme.**
- **Otomatik N11 fatura iletimi:** `InvoiceDeliver` yeteneğini SUPPORTED yapan kod yok; otomatik kuyruk çalışmaz, elle yol çalışır. Canlıda doğrulanacak.
- **Panel dışı:** dashboard fatura sayaçları yalnız Trendyol bağlantısı olan kiracıda hesaplanıyor.

## Notlar
- Eski bilgisayarda `stash@{0}` ("Preserve existing local edits before Ravencia invoice and returns fixes", 8 Ekim) yalnız o bilgisayarda; kullanıcı bırakılmasını istedi.
- `docs/N11-ENTEGRASYON-PLANI.md` N11 planının tamamı; üstteki durum bölümü bu nota göre güncel tutulmalı.
