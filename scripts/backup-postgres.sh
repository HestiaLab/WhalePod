#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/pg-connection.sh
source "$SCRIPT_DIR/lib/pg-connection.sh"

CONNECTION="${WHALEPOD_DB_CONNECTION:-}"
OUTPUT_DIR="${WHALEPOD_BACKUP_DIR:-$(cd "$(dirname "$0")/.." && pwd)/artifacts/backups}"
RETENTION_DAYS="${WHALEPOD_BACKUP_RETENTION_DAYS:-30}"
STATUS_FILE="${WHALEPOD_BACKUP_STATUS_FILE:-}"
PG_BIN="${PG_BIN:-}"

if [[ -z "$CONNECTION" ]]; then
  echo "WHALEPOD_DB_CONNECTION is required." >&2
  exit 2
fi

if ! pg_connection_parse "$CONNECTION"; then
  exit 2
fi
DATABASE="$PG_CONN_DATABASE"
if [[ -z "$DATABASE" ]]; then
  echo "Connection string must include Database." >&2
  exit 2
fi
case "$DATABASE" in
  postgres|template0|template1)
    echo "Refusing to back up a system database." >&2
    exit 2 ;;
esac

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

PG_DUMP="$(find_tool pg_dump)"
mkdir -p "$OUTPUT_DIR"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
FILE_NAME="whalepod-${DATABASE}-${TIMESTAMP}.dump"
BACKUP_PATH="$OUTPUT_DIR/$FILE_NAME"

pg_connection_export
export PGDATABASE="$DATABASE"
trap 'pg_connection_clear_secrets' EXIT

"$PG_DUMP" --format=custom --compress=9 --no-owner --no-privileges --file "$BACKUP_PATH"

if command -v sha256sum >/dev/null 2>&1; then
  HASH="$(sha256sum "$BACKUP_PATH" | awk '{print $1}')"
else
  HASH="$(shasum -a 256 "$BACKUP_PATH" | awk '{print $1}')"
fi
echo "$HASH  $FILE_NAME" > "$BACKUP_PATH.sha256"

BYTES="$(wc -c < "$BACKUP_PATH" | tr -d ' ')"
if [[ -n "$STATUS_FILE" ]]; then
  mkdir -p "$(dirname "$STATUS_FILE")"
  printf '{"formatVersion":1,"lastSuccessUtc":"%s","database":"%s","fileName":"%s","bytes":%s,"sha256":"%s"}\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$DATABASE" "$FILE_NAME" "$BYTES" "$HASH" > "$STATUS_FILE"
fi

if [[ "$RETENTION_DAYS" -gt 0 ]]; then
  find "$OUTPUT_DIR" -maxdepth 1 -type f -name 'whalepod-*.dump' -mtime "+$RETENTION_DAYS" -print0 |
    while IFS= read -r -d '' old; do
      rm -f -- "$old" "$old.sha256"
    done
fi

echo "backup=$BACKUP_PATH"
echo "bytes=$BYTES"
echo "sha256=$HASH"
