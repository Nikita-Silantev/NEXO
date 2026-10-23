#!/usr/bin/env bash
# ПРОВЕРКА ВОССТАНОВЛЕНИЯ: берёт последнюю физическую копию, поднимает ВРЕМЕННЫЙ экземпляр PostgreSQL
# (порт 5433, отдельный каталог) с воспроизведением WAL и проверяет число таблиц схемы.
# Запускать не реже раза в месяц и после изменения схемы бэкапов. Рабочую БД не затрагивает.
source "$(dirname "$0")/common.sh"
PGBIN="${PGBIN:-$(dirname "$(command -v postgres 2>/dev/null || echo /usr/lib/postgresql/16/bin/postgres)")}"
EXPECT_TABLES="${EXPECT_TABLES:-40}"
DIR="$(mktemp -d /tmp/verify-restore.XXXXXX)"
trap '"$PGBIN/pg_ctl" -D "$DIR" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$DIR"' EXIT

log "WAL-G: восстанавливаю LATEST в $DIR"
wal-g backup-fetch "$DIR" LATEST
chmod 700 "$DIR"
touch "$DIR/recovery.signal"
{
  echo "restore_command = 'wal-g wal-fetch %f %p'"
  echo "recovery_target_timeline = 'latest'"
  echo "port = 5433"
  echo "unix_socket_directories = '$DIR'"
  echo "archive_mode = off"
  echo "listen_addresses = ''"
} >> "$DIR/postgresql.auto.conf"

"$PGBIN/pg_ctl" -D "$DIR" -w -t 600 start >/dev/null
for i in $(seq 1 120); do
  [[ "$(psql -h "$DIR" -p 5433 -U "${PGUSER:-postgres}" -d "${PGDATABASE:-postgres}" -Atc 'select pg_is_in_recovery()' 2>/dev/null)" == "f" ]] && break
  sleep 1
done
n=$(psql -h "$DIR" -p 5433 -U "${PGUSER:-postgres}" -d "${PGDATABASE:-postgres}" -Atc \
  "select count(*) from information_schema.tables where table_schema='nexo' and table_type='BASE TABLE'")
log "таблиц в восстановленной копии: $n (ожидалось не менее $EXPECT_TABLES)"
(( n >= EXPECT_TABLES )) || { log "ПРОВЕРКА НЕ ПРОЙДЕНА"; exit 1; }
log "ПРОВЕРКА ПРОЙДЕНА"
