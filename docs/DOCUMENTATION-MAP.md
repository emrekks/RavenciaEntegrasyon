# Dokümantasyon Haritası ve Güncelleme Düzeni

## Kaynak katmanları

1. `specification/RAVENCIA-NIHAI-PROJE-BELGESI.md`: ana proje planı, hedef işleyiş ve bağlayıcı kapsam.
2. `implementation/PROJECT-STATUS.yaml`: makinece okunabilir güncel durum.
3. `implementation/CURRENT-PHASE.md`: aktif faz ve sıradaki çalışma.
4. `platform-rules/capability-matrix.md`: dış API kanıtları.
5. `adr/`: kararlar ve supersede zinciri.
6. `implementation/F*-plan.md`: faz görevleri.
7. `implementation/F*-evidence-log.md`: test ve uygulama kanıtları.
8. `implementation/traceability-matrix.md`: gereksinim-kod-test ilişkisi.
9. `CHANGELOG.md`: kronolojik insan özeti.
10. Kökteki `README.md` ve `AGENTS.md`: hızlı başlangıç ve Codex kuralları.

## Tek işlemde güncelleme

Durum veya capability değiştiren kod değişikliği aynı commit içinde şunları günceller:

- `PROJECT-STATUS.yaml`
- `CURRENT-PHASE.md`
- İlgili faz evidence logu
- `CHANGELOG.md`
- Gerekliyse capability ve traceability matrisleri
- Kullanıcı görünür davranış veya kapsam değiştiyse ana plan, README ve AGENTS

## Paket türleri

- **Geliştirme repository'si:** `.git` geçmişini korur.
- **Release kaynak paketi:** `.git`, secret, runtime veri, cache ve build çıktısı içermez.
- **Production teslimi:** tercihen CI tarafından üretilmiş immutable image digestleriyle yapılır.
