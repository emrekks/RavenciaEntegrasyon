# F0 Dış Bağımlılıklar

## Bağlayıcı platform sırası

`Trendyol -> Hepsiburada -> Trendyol E-Faturam`

ADR-015 uyarınca aktif geliştirme ve doğrulama yalnız bu üç platformla sınırlıdır. Shopify, N11 ve Pazarama `DEFERRED` durumundadır; mevcut kanıtları korunur fakat aktif teslim, ön koşul veya faz blocker'ı sayılmaz. Aktif üç platform tamamlanmadan başka platform açılmaz.

| Bağımlılık | Gerekli kapı | Mevcut durum | Güvenli fallback | Yerel geliştirme blocker? |
| --- | --- | --- | --- | --- |
| Trendyol resmî dokümanı | Capability inceleme | Erişilebilir; test hesabı yok | `UNKNOWN`, write off, fake adapter | Hayır |
| E-Faturam resmî dokümanı | Fatura capability inceleme | Erişilebilir; test firması/credential yok | `UNKNOWN`, submit off | Hayır |
| Shopify resmî dokümanı ve development store | Ertelenen F5 capability/SIT | Yerel adapter çekirdeği korunuyor; ADR-015 ile aktif kapsam dışında | `DEFERRED`; `UNKNOWN`, bütün write off, yeni çalışma yok | Hayır; yeni işletme sahibi kararına kadar kapsam dışı |
| Hepsiburada partner/SIT hesabı | F6A auth/capability/SIT | Sipariş SIT merchant credential ve dolu test siparişi sağlandı; AWS üretim dağıtımından salt-okunur endpoint `items=2`, `ConnectionTest` ve `ORDER_READ` `SUPPORTED` | STAGE şifreli credential + salt-okunur sipariş eşitleme; diğer capability'ler ve bütün write kapalı | Hayır, order read için; evet, diğer ürün aileleri/safe-write/tam çıkış için |
| N11 resmî dokümanı | Ertelenen capability inceleme | ADR-015 ile aktif kapsam dışında | `DEFERRED`; `UNKNOWN`, write off | Hayır |
| Pazarama portalı | Ertelenen capability inceleme | ADR-015 ile aktif kapsam dışında | `DEFERRED`; `UNKNOWN`, endpoint uydurma yok | Hayır |
| Hedef AWS Ubuntu Server 26.04 LTS | F0 runtime/volume/backup çıkışı | Kullanıcı mevcut x86_64, 2 vCPU, 8 GB sınıfı RAM ve 80 GB NVMe hostunu v3.4 hedefi olarak onayladı; SSH, Docker Engine/systemd, exact Compose v2.40.2, immutable production image, reboot/named-volume, DNS/TLS ve hedef restore geçti | x5 kapasite ölçümü ve şifreli off-host backup hedefi tamamlanır | Hayır, yerel geliştirme için; evet, tam production/F0 çıkışı için |
| Gerçek iş hacmi | Kapasite/x5 | `1.000` ürün ve `15.000` sipariş/yıl kullanıcı tarafından sağlandı; x5 profil `5.000` ürün ve `75.000` sipariş/yıl | Bilinmeyen varyant/sipariş satırı/seasonality metrikleri izlenir; limit uydurulmaz | Hayır; F0 baz girdisi tamamlandı |
| Hedef volume, restore süresi ve RTO | DR kanıtı | Yerel PostgreSQL 18.4 sentetik dump/temiz volume restore geçti (`0,147 sn`); hedef Ubuntu Server restore çalıştırılmadı | Yerel süre hedef RTO ilan edilmez; `RISK-DR-001` hedef için açık; off-host yalnız resilient profilde zorunlu | Hayır, yerel geliştirme için; evet, hedef F0 çıkışı için |
| F1 production manifest/lock/image | F1 build/release | F0 non-production lock/index digest kanıtı kullanıcı onayıyla tamamlandı | F1 root lock ve application image üretiminde yeniden karşılaştır; uyumsuzluk fail-closed | Hayır; F0 version blocker kapandı |
| Stitch arayüz dosyası | İlgili UI fazı | 2026-07-31 tarihinde sağlandı; ZIP SHA-256 `3B51EBF78D7653933451E2B41D627A5281E14298844F7B7AFFAFC0B8198CE0A9`; F3 planında faz filtresiyle incelendi | Yalnız ilgili faz ekranları görsel referans alınır; demo değerler ve ileri faz menüleri uygulanmaz | Hayır |

Platform doküman URL'leri capability matrisinde tutulur. Test hesabı olmadan `SUPPORTED` kullanılmaz.

## F4 güncellemesi

E-Faturam entegrasyon dokümanı ve sign-in kaynağı doğrulandı; test firma/credential, API kullanıcısı-pazaryeri entegratörü hesabı, legal entity girdisi ve mali policy kararları henüz sağlanmadı. Trendyol invoice link/file kaynakları doğrulandı ancak Stage package/delivery kanıtı yok. Bu girdiler F4 yerel çekirdeğini durdurmaz; taxpayer→submit→status→document ve marketplace delivery SIT/production kabulünü durdurur. Ubuntu sunucu/domain yokluğu private localhost panelini durdurmaz, public document link ve production smoke'u durdurur.

## F5 güncellemesi

Shopify Admin GraphQL `2026-07` yerel adapter çekirdeği ve tarihsel yerel kanıtları korunur. ADR-015 ile Shopify aktif kapsamdan çıkarılmıştır; development store, auth/scope ve production reconciliation eksikleri tamamlanmış sayılmaz, fakat Hepsiburada'nın ilerlemesini bloke etmez. Yeni Shopify çalışması yapılmaz; capability’ler `UNKNOWN`, bütün write işlemleri kapalı kalır.

## F6A güncellemesi

Hepsiburada generic-port adapterı, merchant connection/capability kaydı ve integrations UI tamamlandı. 2026-08-03 tarihinde AWS üretim dağıtımından resmî Sipariş SIT GET çağrısı Basic Auth + User-Agent ile iki dolu sipariş döndürdü; canlı `merchantSKU` alan yazımı contract testine ve mapper'a alındı. `ConnectionTest` ve `ORDER_READ` `SUPPORTED`, iki SIT siparişi yerel generic sipariş modeline işlendi. Secret kaynakta/kanıtta tutulmaz; uygulamada Data Protection ile şifrelenir. Diğer read capability'leri ve bütün dış yazmalar fail-closed'dur. ADR-015 uyarınca Shopify ön koşulu kaldırılmıştır; Hepsiburada'nın kendi safe-write, target reconciliation, rollback ve production smoke kanıtları tamamlanmadan F6A tam çalışma ilan edilmez.
