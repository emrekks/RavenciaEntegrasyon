# F4 — Trendyol E-Faturam Tamamlama Planı

## Hedef

Trendyol siparişi/paketi için doğru mali belgeyi oluşturmak, uzak durumunu takip etmek, PDF’yi private saklamak ve Trendyol’a güvenli link iletmek.

## Uygulama sırası

1. Test firma, company/user scope ve entegrasyon modelini doğrula.
2. Taxpayer query endpoint ve mapper’ı uygula.
3. Mali policy: e-Fatura/e-Arşiv seçimi, rounding, due, adjustment ve iptal kurallarını onayla.
4. Submit payload fixture’larını gerçek ancak anonim örneklerle genişlet.
5. Invoice status polling ve terminal durum eşlemesini uygula.
6. Permanent PDF URL alımı, download checksum ve private immutable storage testini tamamla.
7. Cancellation endpoint, izin, süre ve durum guardlarını uygula.
8. Trendyol link teslimini `SUBMITTED → CONFIRMED` şeklinde uzlaştır.
9. Duplicate submit/delivery ve timeout-after-remote-success senaryolarını test et.

## Çıkış kapısı

- Aynı sipariş/paket için duplicate mali belge oluşmaz.
- Uzak durum bilinmeden yerel terminal başarı yazılmaz.
- PDF public storage’a veya loga düşmez.
- İptal ve adjustment iş kuralları yazılı mali onaya dayanır.
- Stage E2E ve production read-only smoke kanıtı vardır.
