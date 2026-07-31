# ADR-006: İş Otoriteleri ve Güvenli Varsayılanlar

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Birden fazla platformun stok, fiyat, iade ve fatura davranışı merkezi iş gerçeğini belirsizleştirebilir; başlangıç otoriteleri güvenli ve tek anlamlı olmalıdır.

## Karar

- Stok otoritesi merkezi `StockLedger`dır.
- Merkezi fiyat esastır; kanal fiyatı yalnız açık override ile farklılaşır.
- Başlangıç lokasyonu `MAIN`, safety stock `0`dır.
- İade yalnız kalite sonucu `PASS` ise restock edilir.
- Otomatik fatura kapalıdır.

## Sonuçlar

Platform bakiyesi merkezi gerçeği örtük biçimde değiştiremez; `UNKNOWN` kalite sonucu stok artırmaz; fatura submit ayrı capability ve onay gerektirir.

## Değişiklik kapısı

İş sahibi onayı, ölçülebilir kabul kriteri ve bu ADR güncellemesi olmadan varsayılanlar değişmez.
