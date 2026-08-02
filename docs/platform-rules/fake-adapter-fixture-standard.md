# Fake Adapter ve Anonim Fixture Standardı

## Amaç

F1 yerel geliştirmesinin gerçek platform secret'ı veya canlı dış çağrı olmadan deterministik ilerlemesini sağlar. Bu belge production adapter kodu veya platform sözleşmesi tanımlamaz.

## Fake adapter kuralları

- Aynı giriş ve kayıtlı senaryo aynı sonucu üretir; sistem saati, rastgelelik ve sıra numarası test tarafından kontrol edilir.
- Read ve write capability'leri ayrı simüle edilir. Varsayılan tüm capability'ler `UNKNOWN`, write sonuçları fail-closed'dur.
- Senaryolar en az başarı, boş sonuç, kısmi sonuç, geçici hata, kalıcı hata, timeout, rate-limit sınıfı, duplicate/replay ve belirsiz sonuç türlerini ifade edebilir; resmî değeri bilinmeyen status code/enum/limit uydurulmaz.
- Idempotency aynı anahtarla aynı dış etkiyi ikinci kez üretmez; inbox duplicate senaryosu görünürdür.
- Retry/backoff zamanı sanal/test saatiyle gözlenebilir; gerçek ağa çıkış yasaktır.

## Fixture kuralları

- UTF-8, stabil alan sırası ve deterministik dosya adı kullanılır.
- Secret, access token, refresh token, API key, cookie, imza, gerçek e-posta/telefon/adres/vergi/kimlik bilgisi ve kişisel ad içermez.
- Tanımlayıcılar sentetik ve açıkça test niteliğindedir; ham üretim payload'ı commit edilmez.
- Her fixture için kaynak platform, capability code, anonimleştirme yöntemi, capture tarihi, kaynak sürümü, beklenen sonuç ve SHA-256 checksum metadata'sı tutulur.
- Platformun resmî sözleşmesinde doğrulanmayan alan fixture'a eklenmez. Bilinmeyen kısım metadata'da `UNKNOWN` olarak işaretlenir.

## Kabul kanıtı

İlgili fazda secret/PII pattern taraması, schema/mapping testi, checksum doğrulaması, çevrimdışı çalışma ve ağ çıkışının engellendiği test kanıtı üretilir. F0'da gerçek fixture oluşturulmamıştır.

## Uygulama durumu

Release-candidate E2E hazırlığı için test-only `DeterministicFakeAdapter`, `tests/MarketplaceHub.EndToEnd.Tests` içinde generic connection/reference/product/inventory-price/order/return portlarının tamamını uygular. Production DI veya platform registry’sine eklenmez. Success, empty, partial, authentication, rate-limit, 5xx, timeout, validation ve contract-violation senaryoları deterministiktir; clock enjekte edilir, yazmalar varsayılan kapalıdır ve aynı effect idempotency anahtarının replay’i ikinci etki üretmez. Kaynak guard’ı ağ/auth/secret bağımlılığı bulunmadığını doğrular. PostgreSQL E2E testi job lease, işlem, worker-kill/reaper, retry ve completion zincirinde Order/OrderLine/cursor tekrar üretmediğini kanıtlar.

Bu test harness gerçek platform fixture’ı veya sandbox/SIT kanıtı değildir. Yerel release-candidate kanıtında gerçek Chromium oturumu → API → PostgreSQL job → gerçek Worker → Fake adapter → sipariş listesi/detay UI zinciri geçmiştir. Gerçek platform sandbox/SIT bölümü dış test hesabı hazır olduğunda ayrıca çalıştırılır.
