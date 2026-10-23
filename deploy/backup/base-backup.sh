#!/usr/bin/env bash
# Полная физическая копия (WAL-G) + удаление копий старше срока хранения.
# WAL (журнал предзаписи) архивируется непрерывно командой archive_command самого PostgreSQL.
source "$(dirname "$0")/common.sh"

log "WAL-G: создаю полную копию"
wal-g backup-push "$PGDATA"
log "WAL-G: удаляю копии сверх ${BACKUP_RETAIN_FULL} полных"
wal-g delete retain FULL "$BACKUP_RETAIN_FULL" --confirm
wal-g backup-list || true
log "готово"
