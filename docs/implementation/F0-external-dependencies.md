# F0 Dış Bağımlılıklar

## Bağlayıcı platform sırası

`Trendyol -> E-Faturam -> Shopify -> Hepsiburada -> N11 -> Pazarama`

Bu sıra değiştirilmez ve bir platformun belge/test erişimi diğerini üretim koduyla öne aldırmaz.

| Bağımlılık | Gerekli kapı | Mevcut durum | Güvenli fallback | Yerel geliştirme blocker? |
| --- | --- | --- | --- | --- |
| Trendyol resmî dokümanı | Capability inceleme | Erişilebilir; test hesabı yok | `UNKNOWN`, write off, fake adapter | Hayır |
| E-Faturam resmî dokümanı | Fatura capability inceleme | Erişilebilir; test firması/credential yok | `UNKNOWN`, submit off | Hayır |
| Shopify resmî dokümanı ve development store | F5 capability/SIT | 2026-07 pinli yerel adapter çekirdeği hazır; app/auth, test store, granted scope, Location ve public HTTPS yok | `UNKNOWN`, bütün write off, anonim contract fixture | Hayır, yerel çekirdek için; evet, F5 tam çıkışı için |
| Hepsiburada partner/SIT hesabı | F6A auth/capability/SIT | Sipariş SIT merchant credential sağlandı; resmî salt-okunur endpoint AWS’den HTTP 200 ve boş anonim zarf döndürdü | STAGE şifreli credential + bağlantı testi; sipariş mapping ve bütün write kapalı | Hayır, bağlantı testi için; evet, dolu fixture/mapping/tam çıkış için |
| N11 resmî dokümanı | Capability inceleme | Sayfa erişilebilir; test hesabı yok | `UNKNOWN`, write off | Hayır |
| Pazarama portalı | Capability inceleme | Login gerekli | `UNKNOWN`, endpoint uydurma yok | Hayır |
| Hedef AWS Ubuntu Server 26.04 LTS | F0 runtime/volume/backup çıkışı | Kullanıcı mevcut x86_64, 2 vCPU, 8 GB sınıfı RAM ve 80 GB NVMe hostunu v3.4 hedefi olarak onayladı; SSH, kapasite, Docker Engine/systemd, exact Compose v2.40.2 ve reboot/named-volume checksum geçti | Restore, x5 kapasite, DNS/TLS ve production image kanıtı `ubuntu-server-runtime-validation.md` ile tamamlanır | Hayır, yerel geliştirme için; evet, hedef production/F0 çıkışı için |
| Gerçek iş hacmi | Kapasite/x5 | `1.000` ürün ve `15.000` sipariş/yıl kullanıcı tarafından sağlandı; x5 profil `5.000` ürün ve `75.000` sipariş/yıl | Bilinmeyen varyant/sipariş satırı/seasonality metrikleri izlenir; limit uydurulmaz | Hayır; F0 baz girdisi tamamlandı |
| Hedef volume, restore süresi ve RTO | DR kanıtı | Yerel PostgreSQL 18.4 sentetik dump/temiz volume restore geçti (`0,147 sn`); hedef Ubuntu Server restore çalıştırılmadı | Yerel süre hedef RTO ilan edilmez; `RISK-DR-001` hedef için açık; off-host yalnız resilient profilde zorunlu | Hayır, yerel geliştirme için; evet, hedef F0 çıkışı için |
| F1 production manifest/lock/image | F1 build/release | F0 non-production lock/index digest kanıtı kullanıcı onayıyla tamamlandı | F1 root lock ve application image üretiminde yeniden karşılaştır; uyumsuzluk fail-closed | Hayır; F0 version blocker kapandı |
| Stitch arayüz dosyası | İlgili UI fazı | 2026-07-31 tarihinde sağlandı; ZIP SHA-256 `3B51EBF78D7653933451E2B41D627A5281E14298844F7B7AFFAFC0B8198CE0A9`; F3 planında faz filtresiyle incelendi | Yalnız ilgili faz ekranları görsel referans alınır; demo değerler ve ileri faz menüleri uygulanmaz | Hayır |

Platform doküman URL'leri capability matrisinde tutulur. Test hesabı olmadan `SUPPORTED` kullanılmaz.

## F4 güncellemesi

E-Faturam entegrasyon dokümanı ve sign-in kaynağı doğrulandı; test firma/credential, API kullanıcısı-pazaryeri entegratörü hesabı, legal entity girdisi ve mali policy kararları henüz sağlanmadı. Trendyol invoice link/file kaynakları doğrulandı ancak Stage package/delivery kanıtı yok. Bu girdiler F4 yerel çekirdeğini durdurmaz; taxpayer→submit→status→document ve marketplace delivery SIT/production kabulünü durdurur. Ubuntu sunucu/domain yokluğu private localhost panelini durdurmaz, public document link ve production smoke'u durdurur.

## F5 güncellemesi

Shopify Admin GraphQL `2026-07` yerel adapter çekirdeği, canonical shop-domain kontrolü, şifreli token/client-secret, raw-body HMAC, Inbox dedupe ve streaming JSONL contract kanıtları tamamlandı. Development store, gerçek auth/token edinim kanıtı, granted scopes, Location GID, public HTTPS ve ürün/fulfillment otoritesi bulunmadığından bütün Shopify write işlemleri kapalı, capability’ler `UNKNOWN` kalır. Bu girdiler F5 yerel çekirdeğini durdurmaz; development-store ve production kabulünü durdurur.

## F6A güncellemesi

Hepsiburada generic-port adapterı, merchant connection/capability kaydı ve integrations UI tamamlandı. 2026-08-02 tarihinde AWS sunucusundan resmî Sipariş SIT GET çağrısı Basic Auth + User-Agent ile HTTP 200 döndürdü. Secret kaynakta/kanıtta tutulmaz; uygulamada Data Protection ile şifrelenir. Yanıt boş olduğu için DTO/status/mapping üretilmedi; sipariş okuma ve bütün dış yazmalar fail-closed’dur. F5 production reconciliation/rollback tamamlanmadan F6A production çıkışı ayrıca faz kapısında kalır.
