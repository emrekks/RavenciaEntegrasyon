# Güncel Uygulama Kapsamı

> Ayrıntılı ve yetkili ürün/teknik çerçeve: `RAVENCIA-NIHAI-PROJE-BELGESI.md`. Bu dosya günlük kapsam özetidir.

**Karar tarihi:** 2026-08-04  
**Karar:** Aktif entegrasyon geliştirmesi Trendyol Türkiye `CORE` storefront ve Trendyol E-Faturam ile sınırlıdır.

## 1. Aktif entegrasyonlar

| Platform kodu | Sorumluluk |
| --- | --- |
| `TRENDYOL` | Türkiye `CORE`: bağlantı/capability, referans katalog, Product V2 create/update/archive/read-back, stok-fiyat, Order V2/stream, paket, takip numarası, etiket, iade, webhook ve fatura link teslimi |
| `TRENDYOL_EFATURAM` | Mükellef, mali belge oluşturma, durum, PDF, iptal ve mali belge sağlayıcı işlemleri |

UI bağlantı seçicisi, API servisleri, DI kayıtları ve Worker yönlendirmesi bu iki kodla sınırlandırılır.

## 2. Aktif ürün hedefi

Sistem aşağıdaki uçtan uca akışı güvenilir biçimde tamamlamalıdır:

1. Trendyol bağlantısını doğrula ve capability kanıtlarını kaydet.
2. Kategori ağacı, marka, kategori özellikleri ve özellik değerlerini eşitle.
3. Trendyol’daki ürünleri yerel kataloğa çek ve kimlik eşlemesini koru.
4. Yerel ürün yayınlama isteğini Trendyol Product Integration V2 payload’ına dönüştür.
5. Ürün oluşturma/güncelleme batch sonucunu izleyip satır hatalarını kaydet.
6. Stok ve fiyatı tek bir uzak `price-and-inventory` komutu altında atomik iş emri olarak gönder.
7. Sipariş/paket/iade verisini idempotent biçimde içeri al.
8. E-Faturam üzerinden doğru belge türünü üret, durumunu takip et ve PDF belgesini private storage’a al.
9. Kalıcı HTTPS fatura bağlantısını doğru Trendyol paketine ilet ve sonucu uzlaştır.
10. Reconciliation, retry, dead-letter, audit, backup/restore ve rollback kanıtlarını tamamla.

## 3. Kapsam dışı

Trendyol `LUXE`, uluslararası storefront ve Türkiye dışı channel/storefront kodları ayrı ADR ve kabul kanıtı olmadan etkin değildir. E-Faturam mali sağlayıcı kapanışı F4 olarak ayrıca izlenir.


Başka pazaryeri adapterı, credential türü, menü, route, job türü, webhook doğrulayıcısı veya capability satırı oluşturulmaz. Yeni platform için önce ayrı karar belgesi, resmi doküman, test hesabı, fixture ve tamamlanmış Trendyol/E-Faturam çıkış raporu gerekir.

## 4. Tamamlanma tanımı

Bir entegrasyon ancak aşağıdaki koşulların tamamı sağlandığında “tam çalışır” sayılır:

- Resmî ve güncel endpoint/sürüm doğrulandı.
- Stage/test hesabında read ve izin verilen write senaryoları geçti.
- Başarılı, validation hatalı, auth hatalı, rate-limit, timeout, duplicate ve partial batch senaryoları test edildi.
- Retry yalnız güvenli ve idempotent işlemlerde çalışıyor.
- Reconciliation uzak sistemle farkı buluyor ve operatöre gösteriyor.
- Secret/PII loglara veya kanıt dosyalarına girmiyor.
- Backup temiz volume’a restore edildi ve uygulama smoke testi geçti.
- Dış yazma açma işlemi açık operasyon onayı ve rollback adımı içeriyor.

## 5. Veri ve migration politikası

- Tarihsel migration dosyaları silinmez veya yeniden adlandırılmaz.
- Eski platformlara ait mevcut veritabanı kayıtları otomatik hard-delete edilmez; servis katmanı bunları aktif kapsamdan gizler.
- Veri temizliği ancak backup sonrasında, ayrı SQL planı ve kayıt sayımıyla yapılır.
- Kaynak pakette runtime PostgreSQL data dizini bulunmaz.
