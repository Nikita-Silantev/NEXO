#!/usr/bin/env bash
# Общие настройки скриптов резервного копирования NEXO.
set -euo pipefail

log() { echo "[$(date -u +%FT%TZ)] $*"; }

# Каталог данных PostgreSQL (в контейнере – том pgdata).
export PGDATA="${PGDATA:-/var/lib/postgresql/data}"
# Сколько полных копий хранить (ежесуточные => 30 суток, см. Политику хранения).
export BACKUP_RETAIN_FULL="${BACKUP_RETAIN_FULL:-30}"
export LOGICAL_RETAIN_DAYS="${LOGICAL_RETAIN_DAYS:-30}"
export LOGICAL_DIR="${LOGICAL_DIR:-/backups/logical}"
