#!/bin/sh
set -eu
umask 077
test -r "${POSTGRES_PASSWORD_FILE:?missing POSTGRES_PASSWORD_FILE}"
export PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="/backup/$stamp"
mkdir "$target"
pg_dump --format=custom --file="$target/database.dump"
tar -C /source -czf "$target/private-volumes.tar.gz" files dp-keys
sha256sum "$target/database.dump" "$target/private-volumes.tar.gz" > "$target/SHA256SUMS"
printf '{"createdAt":"%s","postgresMajor":18,"filesIncluded":true,"dataProtectionKeysIncluded":true}\n' "$stamp" > "$target/manifest.json"
echo "Backup set created at $target; transfer it to the approved encrypted off-host target."
