# Ravencia MarketplaceHub - Codex Çalışma Talimatları

Bu dosya repository kökünün tamamı için geçerlidir.

## Güncel kullanıcı arayüzü notu

- Eşleştirme merkezi yalnız aktif `TRENDYOL` kapsamını gösterir; kategori ve marka iki ana sekmedir.
- Panel kategorisi eşleştirme ekranından oluşturulabilir. Bu yerel katalog işlemidir; Trendyol dış yazma kapılarını açmaz.
- Sipariş detay route'u kullanıcı arayüzünde yoktur; eski `/orders/:id` adresi listeye döner. Fatura ön izlemesi yalnız yerel taslak oluşturabilir; provider submit için parola ve açık onay kapısı korunur.
- Güvenlik ekranı mevcut MFA/session endpointlerine bağlıdır; canlı kabulte MFA etkinleştirme veya oturum iptali kullanıcı onayı olmadan tetiklenmez. Yeni ürün ekranında desi doğrudan veya ölçülerden hesaplanır ve nullable varyant alanında saklanır.
- Sonlandırılmış oturum kayıtları kullanıcı kapsamlı tekil/toplu silinebilir; aktif veya mevcut oturum silinemez. Genel faturalama ayar sayfası kullanıcı menüsünde yoktur.

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

1. Görsel, metin, hizalama veya CSS değişikliğinde otomatik test ya da build çalıştırma; yalnız değişen ekranı hızlıca önizle.
2. Olağan işlevsel değişiklikte varsayılan kontrol kısa manuel smoke kontroldür. Build/test ancak değişiklik derleme riski taşıyorsa veya somut bir sorun görülürse yalnız ilgili kapsamda çalıştırılır.
3. Kimlik doğrulama, yetkilendirme, migration, para/fatura, dosya yükleme, veri kaybı riski veya dış API yazması içeren değişiklikte en küçük ilgili hedefli kontrol zorunludur; başarısız sonuç gizlenmez.
4. Tam solution/web/entegrasyon doğrulaması günlük geliştirme sırasında çalıştırılmaz. Yalnız kullanıcı açıkça istediğinde, release/tag veya production deploy öncesinde çalıştırılır.

Çalıştırılmayan ayrıntılı testler `NOT_RUN` olarak kaydedilir; başarılı gösterilmez. Tam test çıktısını konuşma bağlamına kopyalama. Kullanıcıya yalnız çalıştırılan kontrolün özeti ve varsa bilinen riski bildir.

Tam doğrulama komutlarını günlük iş akışına ekleme; mevcut CI/release hattı gerektiğinde bunları merkezi olarak yürütür.

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
