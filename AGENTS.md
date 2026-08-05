# Ravencia MarketplaceHub - Codex Çalışma Talimatları

Bu dosya repository kökünün tamamı için geçerlidir.

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
- Uygulanmış migration dosyalarını silme veya yeniden adlandırma.
- Dış yazmaları kanıt ve açık operasyon onayı olmadan etkinleştirme.
- Adapter metodunun bulunmasını “tam çalışır” veya “production-ready” kabul etme.

## Durum yükseltme kuralı

Bir iş yalnız kod bulunduğu için tamamlanmış sayılmaz. Durumu yükseltmeden önce kabul kriterini, hedefli testleri, gerekiyorsa Stage kanıtını, audit/operasyon görünürlüğünü ve ilgili evidence kaydını doğrula. Çalıştırılamayan testi `NOT_RUN` veya `BLOCKED_*` olarak yaz; başarılı gösterme.

## Token tasarruflu test yöntemi

Geliştirme döngüsünde değişiklik etkisine uygun en küçük güvenilir test kümesini kullan:

1. Syntax/format ve ilgili proje build'i.
2. Değişen modülün hedefli unit/application testleri.
3. Gerekli integration/contract/web testleri.
4. Faz, commit çıkış kapısı, tag veya release öncesinde tam solution ve web testleri.

Tam test çıktısını konuşma bağlamına kopyalama. Logu evidence dosyasına veya geçici artefakta yaz; kullanıcıya exit code, test sayısı, başarısız test adı ve evidence yolunu özetle. Token tasarrufu test atlamak için kullanılamaz.

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
