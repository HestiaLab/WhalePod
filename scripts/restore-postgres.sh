#!/usr/bin/env bash
set -euo pipefail

BACKUP_FILE=""
DATABASE=""
CONFIRM_DATABASE=""
YES="false"
CONNECTION="${WHALEPOD_DB_CONNECTION:-}"
PG_BIN="${PG_BIN:-}"

usage() {
  echo "Usage: $0 --backup <file> --database <name> --confirm-database <name> --yes" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backup) BACKUP_FILE="${2:-}"; shift 2 ;;
    --database) DATABASE="${2:-}"; shift 2 ;;
    --confirm-database) CONFIRM_DATABASE="${2:-}"; shift 2 ;;
    --yes) YES="true"; shift ;;
    *) usage ;;
  esac
done

if [[ "$YES" != "true" ]]; then
  echo "Restore requires explicit --yes." >&2
  exit 2
fi
if [[ -z "$BACKUP_FILE" || -z "$DATABASE" || -z "$CONFIRM_DATABASE" ]]; then
  usage
fi
if [[ "$DATABASE" != "$CONFIRM_DATABASE" ]]; then
  echo "--confirm-database must match --database." >&2
  exit 2
fi
if [[ ! "$DATABASE" =~ ^[a-z0-9_]+$ ]]; then
  echo "Database name may only contain lowercase letters, digits and underscores." >&2
  exit 2
fi
case "$DATABASE" in
  postgres|template0|template1)
    echo "Refusing to restore into a system database." >&2
    exit 2 ;;
esac
if [[ ! -f "$BACKUP_FILE" ]]; then
  echo "Backup file not found: $BACKUP_FILE" >&2
  exit 2
fi
if [[ -z "$CONNECTION" ]]; then
  echo "WHALEPOD_DB_CONNECTION is required." >&2
  exit 2
fi

DB_HOST="127.0.0.1"
DB_PORT="5432"
DB_USER=""
DB_PASSWORD=""
IFS=';' read -ra PARTS <<< "$CONNECTION"
for part in "${PARTS[@]}"; do
  [[ -z "$part" ]] && continue
  key="${part%%=*}"
  value="${part#*=}"
  key="$(echo "$key" | tr '[:upper:]' '[:lower:]' | xargs)"
  value="$(echo "$value" | xargs)"
  case "$key" in
    host) DB_HOST="$value" ;;
    port) DB_PORT="$value" ;;
    username) DB_USER="$value" ;;
    password) DB_PASSWORD="$value" ;;
  esac
done

find_tool() {
  local name="$1"
  if [[ -n "$PG_BIN" && -x "$PG_BIN/$name" ]]; then
    echo "$PG_BIN/$name"
    return
  fi
  if command -v "$name" >/dev/null 2>&1; then
    command -v "$name"
    return
  fi
  echo "Cannot find $name. Set PG_BIN to the PostgreSQL bin directory." >&2
  exit 2
}

PSQL="$(find_tool psql)"
PG_RESTORE="$(find_tool pg_restore)"

export PGHOST="$DB_HOST"
export PGPORT="$DB_PORT"
if [[ -n "$DB_USER" ]]; then export PGUSER="$DB_USER"; fi
if [[ -n "$DB_PASSWORD" ]]; then export PGPASSWORD="$DB_PASSWORD"; fi
trap 'unset PGPASSWORD' EXIT

CHECKSUM_FILE="$BACKUP_FILE.sha256"
if [[ -f "$CHECKSUM_FILE" ]]; then
  if command -v sha256sum >/dev/null 2>&1; then
    EXPECTED="$(awk '{print $1}' "$CHECKSUM_FILE")"
    ACTUAL="$(sha256sum "$BACKUP_FILE" | awk '{print $1}')"
  else
    EXPECTED="$(awk '{print $1}' "$CHECKSUM_FILE")"
    ACTUAL="$(shasum -a 256 "$BACKUP_FILE" | awk '{print $1}')"
  fi
  if [[ "$EXPECTED" != "$ACTUAL" ]]; then
    echo "Backup SHA-256 verification failed; restore aborted." >&2
    exit 1
  fi
fi

EXISTS="$("$PSQL" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DATABASE'")"
if [[ "$EXISTS" != "1" ]]; then
  "$PSQL" -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"$DATABASE\""
fi

"$PG_RESTORE" --clean --if-exists --no-owner --no-privileges --exit-on-error --dbname "$DATABASE" "$BACKUP_FILE"

echo "restored=$DATABASE"
echo "backup=$BACKUP_FILE"
