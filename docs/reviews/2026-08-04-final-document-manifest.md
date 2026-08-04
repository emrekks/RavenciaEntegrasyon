# 2026-08-04 Ana Proje Planı v5.0 Manifestosu

## Amaç

Proje belgesini yalnız mevcut teknik durum raporu olmaktan çıkarıp proje öncesi planlama, karar geçmişi, kullanıcı paneli işleyişi, yapım/test yöntemi, dokümantasyon transaction düzeni ve Git geçmişi politikasını kapsayan yaşayan ana plana dönüştürmek.

## Eklenen/güncellenen kaynaklar

- `docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md` - v5.0 ana plan
- `docs/implementation/PROJECT-STATUS.yaml` - makine durum kaydı
- `docs/implementation/CURRENT-PHASE.md` - faz ve çalışma kuralı
- `docs/CHANGELOG.md` - kronolojik değişiklik özeti
- `docs/DOCUMENTATION-MAP.md` - dosya dağıtımı ve güncelleme düzeni
- `scripts/verify-documentation-transaction.py` - tutarlılık kontrolü
- `AGENTS.md` - Codex test, belge ve Git kuralları
- `README.md` - hızlı yönlendirme
- `.github/workflows/publish-release-images.yml` - belge durum kontrolü

## Git kararı

Orijinal `main` geçmişi, tagler ve remote bilgisi geliştirme repository'sine geri bağlanmıştır. Temiz release paketi `.git` içermez; geliştirme paketi geçmişi korur.
