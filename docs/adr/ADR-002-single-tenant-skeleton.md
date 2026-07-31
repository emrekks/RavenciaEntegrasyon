# ADR-002: Tek Tenant İskeleti

- Durum: Accepted — yetkili şartname kararı
- Tarih: 2026-07-31
- Faz: F0

## Bağlam

Başlangıç kapsamı tek işletme, tek Owner ve tek aktif tenant'tır; ileri çok kullanıcı/çok tenant fazları aktif değildir.

## Karar

Veri ve capability scope'u tenant kimliği taşıyabilecek iskeleti korur, ancak yalnız tek aktif tenant vardır. Tenant CRUD, tenant switcher, abonelik/kota, tenant scheduler, impersonation, aktif rol matrisi ve PostgreSQL RLS oluşturulmaz.

## Sonuçlar

İleri fazlara veri izolasyonu için geçiş yolu korunur; F7B/F8 işlevleri erken uygulanmaz. Tüm connection/environment/store scope kayıtları aktif tenant ile ilişkilidir.

## Değişiklik kapısı

Çok kullanıcı yalnız F7B, ikinci tenant ve aktif multi-tenant yalnız F8 onayıyla ele alınabilir.
