# Otomatik Deploy (GitHub Actions → AWS Ubuntu)

`main` dalına push → `Validate` workflow'u geçer → `Deploy` workflow'u sunucuda
`deploy/scripts/fast-deploy.sh` çalıştırır (test, yedek, migration, health doğrulaması).

## Akış

1. `.github/workflows/ci.yml` (`Validate`) derleme, test, format ve güvenlik denetimlerini çalıştırır.
2. Yalnızca `main` üzerindeki başarılı bir push sonrasında `.github/workflows/deploy.yml` tetiklenir.
3. Deploy işi sunucuya SSH ile bağlanır ve doğrulanmış 40 karakterlik commit SHA'sını gönderir.
4. Sunucuda `/usr/local/bin/ravencia-ci-deploy` (`deploy/scripts/ci-entrypoint.sh`) çalışır:
   kilit alır, repoyu o commit'e fast-forward eder, `fast-deploy.sh` komutunu SSH bağlantısından
   bağımsız çalıştırır ve logu `~/deploy-logs/` altına yazar.
5. İş sonunda `https://panel.ravencia.com/health/ready` kontrol edilir.

Eşzamanlı çalışmalar `production-deploy` concurrency grubu ve sunucudaki `flock` ile sıraya girer.
Manuel yeniden deploy: Actions → Deploy → Run workflow (yalnızca `main`).

## GitHub Secrets (Settings → Secrets and variables → Actions)

| Secret | Değer |
| --- | --- |
| `DEPLOY_HOST` | Sunucu IP/DNS adresi |
| `DEPLOY_USER` | `ubuntu` |
| `DEPLOY_SSH_KEY` | Deploy özel anahtarı (ed25519, tüm içerik) |
| `DEPLOY_KNOWN_HOSTS` | `ssh-keyscan` / sunucu host key satırı (`<host> ssh-ed25519 AAAA...`) |

## Güvenlik modeli

- Deploy anahtarı `authorized_keys` içinde `restrict,command="/usr/local/bin/ravencia-ci-deploy"` ile
  kısıtlıdır: shell, port yönlendirme ve başka komut çalıştıramaz. Script root'a aittir; repo pull'u
  bu anahtarın yetkisini değiştiremez.
- Sunucu host key'i `StrictHostKeyChecking=yes` ile sabitlenir.
- Deploy işi repo kodunu çalıştırmaz; yalnızca SSH ile SHA gönderir.

## Anahtarı yenileme / kurulum

```bash
ssh-keygen -t ed25519 -N "" -C github-actions-ravencia-deploy -f deploy_key
# sunucuda, repo kökünden:
bash deploy/scripts/install-ci-deploy.sh "$(cat deploy_key.pub)"
```

Eski anahtarı `~/.ssh/authorized_keys` içinden silin ve `DEPLOY_SSH_KEY` secret'ını güncelleyin.

## Notlar

- Sunucu IP'si AWS'de Elastic IP değilse durdur/başlat sonrası değişir; `DEPLOY_HOST` ve
  `DEPLOY_KNOWN_HOSTS` güncellenmelidir.
- Security group 22. portu GitHub runner'larına açık olmalıdır (IP aralığı değişkendir).
- `fast-deploy.sh` doğrulama veritabanının anonim volume'ünü artık `--volumes` ile siler;
  eskiden her deploy ~570 MB sızdırıyordu.
