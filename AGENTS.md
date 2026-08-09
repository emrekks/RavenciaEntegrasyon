# Ravencia MarketplaceHub - Codex Çalışma Talimatları

Bu dosya repository kökünün tamamı için geçerlidir.

## Güncel kullanıcı arayüzü notu

- Eşleştirme merkezi yalnız aktif `TRENDYOL` kapsamını gösterir; kategori ve marka iki ana sekmedir.
- Panel kategorisi eşleştirme ekranından oluşturulabilir. Bu yerel katalog işlemidir; Trendyol dış yazma kapılarını açmaz.
- Sipariş detay route'u kullanıcı arayüzünde yoktur; eski `/orders/:id` adresi listeye döner. Fatura ön izlemesi yalnız yerel taslak oluşturabilir; provider submit için parola ve açık onay kapısı korunur.
- Güvenlik ekranı mevcut MFA/session endpointlerine bağlıdır; canlı kabulte MFA etkinleştirme veya oturum iptali kullanıcı onayı olmadan tetiklenmez. Yeni ürün ekranında desi doğrudan veya ölçülerden hesaplanır ve nullable varyant alanında saklanır.

## Başlamadan önce zorunlu okuma sırası

1. `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md`
2. `docs/implementation/PROJECT-STATUS.yaml`
3. `docs/implementation/CURRENT-PHASE.md`
4. `docs/specification/current-scope.md`
5. `docs/platform-rules/capability-matrix.md`
6. `docs/CHANGELOG.md`
7. Yapılacak işe karşılık gelen `docs/implementation/F*-plan.md` ve `F*-evidence-log.md`

## Aktif kapsam

- Yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` aktiftir.
- F3/F4 kapanışı, F5 pilot, F6 stabilizasyon ve F7 adapter-registry çıkış kapıları geçmeden yeni pazaryeri adapterı, route, menü, job türü veya capability satırı ekleme.
- F3 ve F4 kod kapanışlarını geri açma; sıradaki iş exact runtime/Stage kabulü ve ardından kontrollü F5 production pilotudur.
- Sipariş UI’sinde menüler görünür alana göre yönlenir; mikro ihracat türetiminde sipariş numarası sabitlenmez, resmî alanlar ve dar belgelenmiş partner geri uyumluluğu kullanılır. Kısa “Mikro ihracat” rozeti yalnız fatura sütununda gösterilir. Uzak termin alanı eksikse tarih türetilmez veya uydurulmaz; veri eksikliği açıkça gösterilir. Masaüstü menü daraltma tercihi kalıcıdır.
- Uygulanmış migration dosyalarını silme veya yeniden adlandırma.
- Dış yazmaları kanıt ve açık operasyon onayı olmadan etkinleştirme.
- Adapter metodunun bulunmasını “tam çalışır” veya “production-ready” kabul etme.

## Durum yükseltme kuralı

Bir iş yalnız kod bulunduğu için tamamlanmış sayılmaz. Durumu yükseltmeden önce kabul kriterini, hedefli testleri, gerekiyorsa Stage kanıtını, audit/operasyon görünürlüğünü ve ilgili evidence kaydını doğrula. Çalıştırılamayan testi `NOT_RUN` veya `BLOCKED_*` olarak yaz; başarılı gösterme.

## Hızlı geliştirme ve test yöntemi

Günlük geliştirme akışı hızlı tutulur. Her küçük UI/metin/CSS değişikliğinde tam solution, tüm backend testleri veya tüm web testleri otomatik çalıştırılmaz.

1. Görsel veya metin değişikliğinde: değişen ekranı yerelde/canlı önizlemede kontrol et; derleme veya test yalnız hata riski varsa çalıştırılır.
2. İşlevsel kod değişikliğinde: yalnız etkilenen proje build'i veya en küçük hedefli test çalıştırılır.
3. Kimlik doğrulama, yetkilendirme, migration, para/fatura, veri kaybı riski veya dış API yazması içeren değişiklikte: ilgili hedefli build/test zorunludur; başarısız sonuç gizlenmez.
4. Tam solution/web/entegrasyon doğrulaması yalnız kullanıcı açıkça istediğinde, faz kapanışında, release/tag öncesinde veya production deploy öncesinde çalıştırılır.

Çalıştırılmayan ayrıntılı testler `NOT_RUN` olarak kaydedilir; başarılı gösterilmez. Tam test çıktısını konuşma bağlamına kopyalama. Kullanıcıya yalnız çalıştırılan kontrolün özeti ve varsa bilinen riski bildir.

Tam doğrulama komutları:

```bash
python3 scripts/verify-repository-cleanliness.py
python3 scripts/verify-documentation-transaction.py
dotnet restore MarketplaceHub.sln --locked-mode
dotnet build MarketplaceHub.sln --no-restore
dotnet test MarketplaceHub.sln --no-build --no-restore
dotnet format MarketplaceHub.sln --verify-no-changes --no-restore
cd src/MarketplaceHub.Web
npm ci --ignore-scripts
npm run typecheck
npm test -- --run
npm run build
```

## Dokümantasyon transaction kuralı

Durum veya capability etkileyen kod değişikliğinde aynı commit içinde en az şunları güncelle:

- `docs/implementation/PROJECT-STATUS.yaml`
- `docs/implementation/CURRENT-PHASE.md`
- ilgili faz evidence logu
- `docs/CHANGELOG.md`
- gerekiyorsa capability ve traceability matrisleri
- kullanıcı görünür davranış veya kapsam değiştiyse ana plan, `README.md` ve bu dosya

`python3 scripts/verify-documentation-transaction.py --base <base-ref>` kontrolü kod değişikliğinin belge işlemi olmadan ilerlemesini engeller.

## Git ve paket politikası

Ana geliştirme repository'sinde `.git` geçmişini, branchleri, tagleri ve commitleri koru. Temiz release/deployment paketinde `.git`, build çıktıları, cache, secret ve runtime veri bulunmaz. Release paketinden `.git` çıkarılması ana repository geçmişini silme gerekçesi değildir.
