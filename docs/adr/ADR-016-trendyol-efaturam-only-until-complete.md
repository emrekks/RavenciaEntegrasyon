# ADR-016 — Trendyol ve Trendyol E-Faturam Tamamlanana Kadar Tek Entegrasyon Kapsamı

- Durum: Accepted
- Tarih: 2026-08-04

## Bağlam

Birden fazla pazaryerinin aynı anda geliştirilmesi adapter, test, capability, dokümantasyon ve operasyon yüzeyini büyüttü. Buna karşın Trendyol ürün yayınlama, birleşik stok-fiyat yazma ve E-Faturam durum/iptal akışları henüz uçtan uca tamamlanmış değildir.

## Karar

Aktif kod ve ürün yüzeyi yalnız `TRENDYOL` ve `TRENDYOL_EFATURAM` bağlantılarını kabul eder. Başka platformlara ait adapter, DI kaydı, Worker dispatch, UI seçeneği ve aktif faz dokümanı kaynak ağacında tutulmaz.

Yeni platform yalnız şu koşullarla açılır:

1. `docs/specification/current-scope.md` tamamlanma tanımı karşılanır.
2. Trendyol ve E-Faturam için Stage E2E, reconciliation, backup/restore ve rollback kanıtı vardır.
3. Yeni platform için ayrı ADR ve capability matrisi güncellemesi kabul edilir.

## Sonuçlar

- Daha küçük saldırı ve bakım yüzeyi.
- Daha az sahte “hazır” capability.
- Worker ve UI’da daha net davranış.
- Gelecekte yeni adapter eklemek için generic portlar korunur.
- Uygulanmış migration geçmişi yeniden adlandırılmaz; tarihsel faz adı aktif kapsam anlamına gelmez.
