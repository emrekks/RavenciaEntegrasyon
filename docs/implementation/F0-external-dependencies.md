# F0 Dış Bağımlılıklar

## Bağlayıcı platform sırası

`Trendyol -> E-Faturam -> Shopify -> Hepsiburada -> N11 -> Pazarama`

Bu sıra değiştirilmez ve bir platformun belge/test erişimi diğerini üretim koduyla öne aldırmaz.

| Bağımlılık | Gerekli kapı | Mevcut durum | Güvenli fallback | F1 yerel blocker? |
| --- | --- | --- | --- | --- |
| Trendyol resmî dokümanı | Capability inceleme | Erişilebilir; test hesabı yok | `UNKNOWN`, write off, fake adapter | Hayır |
| E-Faturam resmî dokümanı | Fatura capability inceleme | Erişilebilir; test firması/credential yok | `UNKNOWN`, submit off | Hayır |
| Shopify resmî dokümanı | Capability inceleme | Erişilebilir; test store yok | `UNKNOWN`, write off | Hayır |
| Hepsiburada portalı | Capability inceleme | Login/erişim doğrulaması eksik | `UNKNOWN`, endpoint uydurma yok | Hayır |
| N11 resmî dokümanı | Capability inceleme | Sayfa erişilebilir; test hesabı yok | `UNKNOWN`, write off | Hayır |
| Pazarama portalı | Capability inceleme | Login gerekli | `UNKNOWN`, endpoint uydurma yok | Hayır |
| Hedef Windows VPS | F0 runtime/volume/backup çıkışı | Kullanıcı yerel bilgisayarda başlayıp VPS'i daha sonra kiralama kararını onayladı; hedef erişim ve özellikler henüz yok | Yerelde ön doğrulama; kiralama sonrası aynı runbook hedefte yeniden çalıştırılır; öncesinde production onayı yok | Hayır, yerel geliştirme için; evet, hedef production/F0 çıkışı için |
| Gerçek iş hacmi | Kapasite/x5 | `1.000` ürün ve `15.000` sipariş/yıl kullanıcı tarafından sağlandı; x5 profil `5.000` ürün ve `75.000` sipariş/yıl | Bilinmeyen varyant/sipariş satırı/seasonality metrikleri izlenir; limit uydurulmaz | Hayır; F0 baz girdisi tamamlandı |
| Hedef volume, restore süresi ve RTO | DR kanıtı | Yerel PostgreSQL 18.4 sentetik dump/temiz volume restore geçti (`0,147 sn`); hedef VPS restore çalıştırılmadı | Yerel süre hedef RTO ilan edilmez; `RISK-DR-001` hedef için açık; off-host yalnız resilient profilde zorunlu | Hayır, yerel geliştirme için; evet, hedef F0 çıkışı için |
| F1 production manifest/lock/image | F1 build/release | F0 non-production lock/index digest kanıtı kullanıcı onayıyla tamamlandı | F1 root lock ve application image üretiminde yeniden karşılaştır; uyumsuzluk fail-closed | Hayır; F0 version blocker kapandı |
| Stitch arayüz dosyası | İlgili UI fazı | Sağlanmadı | Markalı fidelity ertelenir | Hayır |

Platform doküman URL'leri capability matrisinde tutulur. Test hesabı olmadan `SUPPORTED` kullanılmaz.
