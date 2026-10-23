#!/usr/bin/env bash
# Планировщик: каждые сутки в BACKUP_HOUR_UTC (по умолчанию 03:00 UTC) – полная копия и логическая копия.
# При первом запуске (копий ещё нет) полная копия создаётся сразу после готовности БД.
source "$(dirname "$0")/common.sh"
HOUR="${BACKUP_HOUR_UTC:-3}"

until pg_isready -q; do log "жду PostgreSQL"; sleep 3; done

if ! wal-g backup-list 2>/dev/null | grep -q base_; then
  log "копий нет – создаю первую"
  /scripts/base-backup.sh || log "ОШИБКА первой полной копии"
  /scripts/logical-dump.sh || log "ОШИБКА первой логической копии"
fi

while true; do
  now=$(date -u +%s)
  next=$(date -u -d "today ${HOUR}:00" +%s)
  (( next <= now )) && next=$(date -u -d "tomorrow ${HOUR}:00" +%s)
  log "следующая копия через $(( next - now )) с"
  sleep $(( next - now ))
  /scripts/base-backup.sh   || log "ОШИБКА полной копии"
  /scripts/logical-dump.sh  || log "ОШИБКА логической копии"
done
