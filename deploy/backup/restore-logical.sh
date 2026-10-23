#!/usr/bin/env bash
# Восстановление из логической копии в НОВУЮ базу (по умолчанию nexo_restored).
# Использование: restore-logical.sh <файл.dump.age> <файл-закрытого-ключа-age> [имя_базы]
set -euo pipefail
file="${1:?файл .dump.age}"; key="${2:?файл закрытого ключа age}"; db="${3:-nexo_restored}"
sha="${file}.sha256"
[[ -f "$sha" ]] && (cd "$(dirname "$file")" && sha256sum -c "$(basename "$sha")")
createdb "$db"
age -d -i "$key" "$file" | pg_restore --no-owner --dbname="$db"
echo "Восстановлено в базу $db"
