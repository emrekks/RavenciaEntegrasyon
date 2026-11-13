# Devir prompt'u — Tarama düzeltmeleri (13 Kasım 2026)

Aşağıdaki metni yeni bir Claude oturumuna olduğu gibi yapıştırın.

---

Ravencia Marketplace Hub deposunda (`emrekks/RavenciaEntegrasyon`) 13 Kasım 2026'da yapılan tarama düzeltmelerinin devamını sen alıyorsun. Önce `CLAUDE.md`'yi ve `docs/TARAMA-DUZELTME-RAPORU-2026-11-13.md` dosyasını baştan sona oku: hangi bulgunun nasıl düzeltildiği, commit'leri ve testleri orada. Kullanıcıya Türkçe cevap ver.

## Durum
- `TARAMA-DOGRULAMA.html`'deki 25 bulgunun 24'ü düzeltildi. B-4'ü (yönetici iki adımlı doğrulama zorunluluğu) sahip kapattı, yapılmayacak.
- Sahibin kapattığı diğer konular, yeniden açma:
  - C-11: özellik başına tek eşleme şablonu.
  - C-6: 24 saat kuralının yalnız Pazarama'ya daraltılması.
- Ek olarak, sahibin isteğiyle panel KDV listesine %8 eklendi (0, 1, 8, 10, 20). Pazarama, PTT AVM ve n11'in kendi oran listelerine dokunulmadı.
- Sahibin kuralı (13 Kasım): **toplu değer eşitlemeleri pazaryerine anında hiçbir şey yazmaz**, yalnız paneldeki ürünü düzeltir.
  - Ürün bilgisi pazaryerine "Ürün Bilgisi Güncelle" ile gider.
  - Stok ve fiyat periyodik stok uzlaştırmasıyla (`ReconcileStock`) gider.
  - Toplu güvenlik stoğu (A-3) bu yüzden yalnız gönderim işaretini temizliyor; iş kuyruklamıyor.
- Kod `main`'de: `7ec16b5a0`. Validate, `codex/tarama-duzeltmeleri` dalında yeşil geçti ([run 5046](https://github.com/emrekks/RavenciaEntegrasyon/actions/runs/43444775005)). `main`'e push'lanan commit aynı.
- Önceki oturumun kendi dalı `claude/inspiring-noether-cbwvkm`. Dosya ağacı aynı; commit mesajlarında atıf satırları var. `main`'e giden kopyada bu satırlar temizlendi, o yüzden sha'lar farklı.

## İlk yapacakların
1. **Canlı doğrulama (önceki oturum yapamadı, SSH anahtarı yoktu):**
   - `/prod-logs status` ile `marketplacehub-api-1`, `-worker-1` ve `-caddy-1` imajlarının `7ec16b5a0`'a ulaştığını gör.
   - `/prod-logs errors` ile deploy sonrası hata var mı bak.
   - Deploy olmadıysa `gh workflow run deploy.yml --ref main`.
2. **Tarayıcıda kontrol edilmemiş ekranlar.** Kendi sekmende, giriş bilgisi yazmadan bak:
   - İşlem Takibi'nin telefon filtre paneli (G-1).
   - Bağlantı penceresindeki akış tablosunun durum sütunu, ~800 px (G-6).
   - Faturalar onay penceresindeki yeni "sipariş numarasıyla aradım" onay kutusu (D-2): yuvarlak, mor, beyaz tik.
   - CLAUDE.md UI kuralları geçerli.
3. **Canlı veritabanı 12 Kasım'da fabrika ayarlarına sıfırlandı** (0 ürün, 0 sipariş, bağlantılar kapalı). Bağlantılar açılıp veri gelince, `TARAMA-DOGRULAMA.html`'nin "Canlıda tekrar ölçmek için" bölümündeki salt okunur SQL'leri `/prod-sql` ile çalıştır:
   - A-1/A-2: takılı kartlar.
   - A-3: işareti sıfırlanmamış güvenlik stoklu teklifler.
   - A-8: eşleşmemiş sipariş satırları.
   - C-11: kazananı belirsiz şablonlar.

## Açık kalanlar (karar sahibin, sormadan yapma)
- **Drive yedeği (taramanın 1. adımı):** ekipte. Sunucu salt okunur; sen kurma. Bitince `systemctl list-timers 'ravencia-offsite-*'` ve Drive'daki ilk `.tar.age` ile doğrulanır. B-3 kod tarafında hazır: geçici ad, SHA-256 kontrolü, `.verified` kaydı. İlk çalıştırmada kaydı olmayan en yeni kopya yeniden yüklenecek.
- **F-10:** açıklamadaki renk, boyut ve hizalama panelde çalışıyor; Shopify'a giden açıklamada backend (`ImportedHtmlSanitizer`) stilleri siliyor.
- **F-9:** kayıttan sonraki 10 saniyelik yeniden doldurma penceresi (`allowReseedUntil`) duruyor.
- **C-5:** yalnız Shopify `products/delete` bayat bildirimi ele alındı; panel `products/update`'e abone değil.
- **D-2:** E-Faturam API'sinde sipariş numarasıyla arama yok. Kimlik uyuşmazlığı görülen bağlantıda yeniden gönderim, operatörün "portalda sipariş numarasıyla aradım" beyanına bağlı (409 `INVOICE_ORDER_NUMBER_SEARCH_REQUIRED`). Belgede bir arama uç noktası bulunursa (çevrimdışı belgeler `/marketplace-docs`'ta) otomatik aramaya çevrilebilir.

## Bilinmesi gerekenler (ortam ve test)
- **Bulut konteyneri:**
  - SDK 10.0.115 apt'tan kurulur (`builds.dotnet.microsoft.com` engelli). `global.json` 10.0.302 istediği için yerelde `"version": "10.0.100", "rollForward": "latestMajor"` yapıp `git update-index --skip-worktree global.json` uygula. **global.json'u asla commit'leme.**
  - PostgreSQL 16 kurulu, CI'da 18.6 var. Test veritabanına (ve `template1`'e) `pg_catalog.uuidv7()` ile `pg_catalog.uuid_extract_timestamp(uuid)` eşdeğerlerini ekle. Bağlantı dizesi: `MARKETPLACEHUB_TEST_CONNECTION="Host=localhost;Port=5432;Database=marketplacehub_test;Username=marketplacehub;Password=development-only"`.
  - Testleri her `*_FILE` ortam değişkenini kaldırarak çalıştır: `env $(env | grep -o '^[A-Z_]*_FILE' | sed 's/^/-u /') dotnet test ...`. Yoksa API testleri `SSL_CERT_FILE` / `PIP_CONFIG_FILE` yüzünden düşer.
  - Bellek dar; konteyner bir kez OOM ile yeniden başladı. Aynı anda tek ağır komut çalıştır.
- **Paylaşılan test DB'si:**
  - Yarım kalan koşular kilitli iş bırakır ve `JobLease_*` testleri düşer. Çözüm: DB'yi `DROP ... WITH (FORCE)` + `CREATE` ile sıfırla.
  - Konteyner yeniden başladıktan sonra `ANALYZE` çalıştır.
  - `PostgreSqlDashboardEquivalenceTests`'in iki uzun dönemi, tam koşudan sonra ortak DB'de zaman aşımıyla düşebiliyor. Değişmemiş `main` `6321b83`'te de aynı durum oluyor, yani ortam kaynaklı. Tek başına koşunca geçiyor.
- **Son yerel durum:**
  - Tam backend paketi 6.403/6.405 (iki düşen yukarıdaki zaman aşımı).
  - Web tarafında tip kontrolü, lint, build, `check:bundle` ve ilgili vitest dizinleri temiz.
- **Ship:** `ship.sh` `gh run` ve sunucuya SSH istiyor; bulut oturumunda çalışmıyor. Önceki oturum betiğin kilidini kullanmadan elle ship etti:
  - `codex/<topic>` dalına push.
  - Validate'i GitHub MCP ile izleme.
  - `git merge-base --is-ancestor origin/main <sha>` doğrulaması.
  - Aynı sha'yı `main`'e push.

  Mümkünse ship'i betiğin çalıştığı ortamdan yap. Commit mesajları İngilizce olmalı ve CLAUDE.md'ye göre atıf satırı içermemeli.
