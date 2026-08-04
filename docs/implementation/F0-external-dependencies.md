# Dış Bağımlılıklar

| Bağımlılık | Gerekli olduğu alan | Mevcut durum | Çıkış kanıtı |
| --- | --- | --- | --- |
| Trendyol Stage/Production seller credential | Bütün Trendyol read/write kabul testleri | Kaynak pakette secret bulunmaz; kullanıcı tarafından secret store’a verilmeli | Connection, reference, product, order, return ve invoice-link test kayıtları |
| Trendyol E-Faturam test firma hesabı | Fatura oluşturma, durum, PDF, iptal | Gerekli | Firma/user scope, entegrasyon modeli ve tarihli E2E |
| Mali kararlar | Belge türü, rounding, due, iptal ve adjustment | Onay bekliyor | Yazılı iş/mali onay ve test senaryosu |
| Public DNS + TLS | Gerçek webhook ve fatura link erişimi | Production değişkenleri zorunlu hale getirildi | Dış ağdan TLS, webhook ve belge link smoke testi |
| Şifreli off-host backup hedefi | Felaket kurtarma | Aynı host dışına kopya kanıtı gerekli | Temiz host/volume restore + uygulama smoke testi |
| Exact SDK/runtime sürümleri | CI ve immutable image | Repository’de pinli | Locked restore, build, test, image digest |

Başka platform hesabı veya dokümanı bu teslimin dış bağımlılığı değildir.
