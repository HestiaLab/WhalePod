#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/pg-connection.sh
source "$SCRIPT_DIR/lib/pg-connection.sh"

BACKUP_FILE=""
DATABASE=""
CONFIRM_DATABASE=""
YES="false"
ALLOW_EXISTING="false"
DRY_RUN="false"
CONNECTION="${WHALEPOD_DB_CONNECTION:-}"
PG_BIN="${PG_BIN:-}"

usage() {
  echo "Usage: $0 --backup <file> --database <name> --confirm-database <name> --yes [--allow-existing] [--dry-run]" >&2
  echo "  --allow-existing  Allow restoring into a non-empty database; uses --clean --single-transaction." >&2
  echo "  --dry-run         Validate arguments, checksum and connection settings without restoring." >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backup) BACKUP_FILE="${2:-}"; shift 2 ;;
    --database) DATABASE="${2:-}"; shift 2 ;;
    --confirm-database) CONFIRM_DATABASE="${2:-}"; shift 2 ;;
    --yes) YES="true"; shift ;;
    --allow-existing) ALLOW_EXISTING="true"; shift ;;
    --dry-run) DRY_RUN="true"; shift ;;
    *) usage ;;
  esac
done

if [[ "$YES" != "true" && "$DRY_RUN" != "true" ]]; then
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
if [[ ! "$DATABASE" =~ ^whalepod(_[a-z0-9_]+)?$ ]]; then
  echo "Database name must be whalepod or whalepod_<lowercase suffix>." >&2
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

CHECKSUM_FILE="$BACKUP_FILE.sha256"
if [[ ! -f "$CHECKSUM_FILE" ]]; then
  echo "Checksum file is required: $CHECKSUM_FILE" >&2
  exit 2
fi
if command -v sha256sum >/dev/null 2>&1; then
  EXPECTED="$(awk '{print $1}' "$CHECKSUM_FILE")"
  ACTUAL="$(sha256sum "$BACKUP_FILE" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  EXPECTED="$(awk '{print $1}' "$CHECKSUM_FILE")"
  ACTUAL="$(shasum -a 256 "$BACKUP_FILE" | awk '{print $1}')"
else
  echo "Neither sha256sum nor shasum is available; cannot verify the backup." >&2
  exit 2
fi
if [[ -z "$EXPECTED" || "$EXPECTED" != "$ACTUAL" ]]; then
  echo "Backup SHA-256 verification failed; restore aborted." >&2
  exit 1
fi

if ! pg_connection_parse "$CONNECTION"; then
  exit 2
fi
pg_connection_export
trap 'pg_connection_clear_secrets' EXIT

if [[ "$DRY_RUN" == "true" ]]; then
  echo "dryRun=true"
  echo "host=$PG_CONN_HOST"
  echo "port=$PG_CONN_PORT"
  echo "database=$DATABASE"
  echo "user=$PG_CONN_USER"
  echo "sslmode=${PG_CONN_SSLMODE:-}"
  echo "sslrootcert=${PG_CONN_SSLROOTCERT:-}"
  echo "sslcert=${PG_CONN_SSLCERT:-}"
  echo "sslkey=${PG_CONN_SSLKEY:-}"
  echo "allowExisting=$ALLOW_EXISTING"
  echo "singleTransaction=true"
  exit 0
fi

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

EXISTS="$("$PSQL" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DATABASE'")"
USE_CLEAN="false"
if [[ "$EXISTS" == "1" ]]; then
  HAS_TABLES="$("$PSQL" -d "$DATABASE" -tAc "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' LIMIT 1")"
  if [[ "$HAS_TABLES" == "1" ]]; then
    if [[ "$ALLOW_EXISTING" != "true" ]]; then
      echo "Target database '$DATABASE' already contains tables. Re-run with --allow-existing to replace it atomically." >&2
      exit 2
    fi
    USE_CLEAN="true"
  fi
else
  "$PSQL" -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"$DATABASE\""
fi

RESTORE_ARGS=(--single-transaction --no-owner --no-privileges --exit-on-error)
if [[ "$USE_CLEAN" == "true" ]]; then
  RESTORE_ARGS+=(--clean --if-exists)
fi
RESTORE_ARGS+=(--dbname "$DATABASE" "$BACKUP_FILE")

"$PG_RESTORE" "${RESTORE_ARGS[@]}"

echo "restored=$DATABASE"
echo "backup=$BACKUP_FILE"
