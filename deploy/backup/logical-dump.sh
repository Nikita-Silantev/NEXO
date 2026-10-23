#!/usr/bin/env bash
# Независимая логическая копия: pg_dump (custom) -> age (шифрование открытым ключом) -> том и внешнее хранилище.
# Закрытый ключ age хранится у владельца ВНЕ сервера. Без него копию не расшифровать.
source "$(dirname "$0")/common.sh"

: "${AGE_RECIPIENT:?нужно задать AGE_RECIPIENT (открытый ключ age)}"
mkdir -p "$LOGICAL_DIR"
name="nexo-$(date -u +%Y%m%dT%H%M%SZ).dump.age"
tmp="$LOGICAL_DIR/.$name.part"

log "pg_dump -> $name"
pg_dump --format=custom --no-owner --dbname="${PGDATABASE:?}" | age -r "$AGE_RECIPIENT" -o "$tmp"
mv "$tmp" "$LOGICAL_DIR/$name"
sha256sum "$LOGICAL_DIR/$name" > "$LOGICAL_DIR/$name.sha256"

if [[ "${OFFSITE_ENABLED:-false}" == "true" ]]; then
  log "выгрузка во внешнее хранилище (rclone, remote OFFSITE)"
  rclone copy "$LOGICAL_DIR/$name" "OFFSITE:${OFFSITE_PATH:-nexo-offsite/logical}"
  rclone copy "$LOGICAL_DIR/$name.sha256" "OFFSITE:${OFFSITE_PATH:-nexo-offsite/logical}"
  rclone delete --min-age "${LOGICAL_RETAIN_DAYS}d" "OFFSITE:${OFFSITE_PATH:-nexo-offsite/logical}" || true
fi

find "$LOGICAL_DIR" -type f -name 'nexo-*' -mtime "+${LOGICAL_RETAIN_DAYS}" -delete
log "готово"
