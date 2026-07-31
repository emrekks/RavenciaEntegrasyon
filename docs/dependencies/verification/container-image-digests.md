# F0 Container Image Digest Kanıtı

Doğrulama tarihi: 2026-07-31. Aşağıdaki SHA-256 değerleri resmî registry manifest endpoint'lerinden `Docker-Content-Digest` başlığıyla okunmuş multi-platform image index digest'leridir. Tag değişse bile index digest immutable pin sağlar. Hedef VPS kiralandığında platforma özgü child manifest digest'i ayrıca kaydedilecektir.

| Rol | Exact tag | Multi-platform index digest | Resmî registry kaynağı | Durum |
| --- | --- | --- | --- | --- |
| PostgreSQL | `postgres:18.4` | `sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a` | `registry-1.docker.io/v2/library/postgres/manifests/18.4` | VERIFIED_INDEX |
| Caddy | `caddy:2.11.3` | `sha256:ec18ee54aab3315c22e25f3b2babda73ff8007d39b13b3bd1bfffa2f0444c7d9` | `registry-1.docker.io/v2/library/caddy/manifests/2.11.3` | VERIFIED_INDEX |
| Node build runtime | `node:24.18.1` | `sha256:19cd848a0e073d34bd8cd5545a1b6b4d28489b3e3b607366621ced442bd5f6b4` | `registry-1.docker.io/v2/library/node/manifests/24.18.1` | VERIFIED_INDEX |
| .NET SDK build image | `mcr.microsoft.com/dotnet/sdk:10.0.302` | `sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664` | `mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.302` | VERIFIED_INDEX |
| API/Worker runtime base | `mcr.microsoft.com/dotnet/aspnet:10.0.10` | `sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7` | `mcr.microsoft.com/v2/dotnet/aspnet/manifests/10.0.10` | VERIFIED_INDEX |

## Compose CLI release checksum'ları

Kaynak: <https://github.com/docker/compose/releases/download/v2.40.2/checksums.txt>

| Artefakt | SHA-256 |
| --- | --- |
| `docker-compose-windows-x86_64.exe` | `1f7f20b91e0564147dc58b3a58a22a8f64a787e060ce3c25789f408beacc0c4d` |
| `docker-compose-windows-aarch64.exe` | `4acf95d3276cfbaea0e4c348f92f92ac792adc93566b166b5a5acef812a81439` |
| `docker-compose-linux-x86_64` | `6c964d9655cd629ef43c5dc75d9612c2da319237debee54a7aef217e9f362b88` |
| `docker-compose-linux-aarch64` | `20e30dda8d0133895b7991bcfec1eb2c02f9d38c8de9e73669daf9fb83df49e6` |

Hedef mimari bilinmediği için bir binary seçilmemiştir. Runbook, kiralanan VPS'te kullanılan artefaktı bu resmî checksum'lardan biriyle eşleştirecektir.

## Sınırlar

- Bu kayıt image pull veya Windows VPS runtime testi değildir.
- API/Worker application image digest'i production image build edilmeden oluşamaz ve F1 release kanıtıdır; F0'da base image index'i pinlenmiştir.
- Backup service `PILOT_LOCAL` profilde PostgreSQL araçları ve uygulama backup akışıyla tanımlanacaktır; ayrı, doğrulanmamış restic image seçilmemiştir.
