#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
RESTORE="$REPO_ROOT/scripts/restore-postgres.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/bin"
FAKE_LOG="$TMP/psql.log"
FAKE_ENV_LOG="$TMP/env.log"
export FAKE_LOG FAKE_ENV_LOG

cat > "$TMP/bin/psql" <<'EOF'
#!/usr/bin/env bash
printf 'psql %s\n' "$*" >> "$FAKE_LOG"
if [[ "$*" == *"pg_database"* ]]; then
  [[ "${FAKE_DB_EXISTS:-0}" == "1" ]] && echo 1
  exit 0
fi
if [[ "$*" == *"information_schema.tables"* ]]; then
  [[ "${FAKE_HAS_TABLES:-0}" == "1" ]] && echo 1
  exit 0
fi
exit 0
EOF

cat > "$TMP/bin/pg_restore" <<'EOF'
#!/usr/bin/env bash
printf 'pg_restore %s\n' "$*" >> "$FAKE_LOG"
printf 'PGSSLMODE=%s\n' "${PGSSLMODE:-}" >> "$FAKE_ENV_LOG"
printf 'PGSSLROOTCERT=%s\n' "${PGSSLROOTCERT:-}" >> "$FAKE_ENV_LOG"
printf 'PGSSLCERT=%s\n' "${PGSSLCERT:-}" >> "$FAKE_ENV_LOG"
printf 'PGPASSWORD=%s\n' "${PGPASSWORD:-}" >> "$FAKE_ENV_LOG"
exit "${FAKE_RESTORE_EXIT:-0}"
EOF
chmod +x "$TMP/bin/psql" "$TMP/bin/pg_restore"

DUMP="$TMP/backup.dump"
printf 'not-a-real-dump' > "$DUMP"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

run_restore() {
  WHALEPOD_DB_CONNECTION="${CONNECTION}" PG_BIN="$TMP/bin" bash "$RESTORE" \
    --backup "$DUMP" --database whalepod_test_restore \
    --confirm-database whalepod_test_restore "$@"
}

CONNECTION='Host=127.0.0.1;Port=55438;Database=whalepod;Username=review'

rm -f "$DUMP.sha256"
if run_restore --yes >/dev/null 2>&1; then
  fail "missing checksum must abort"
fi
if grep -q '^pg_restore' "$FAKE_LOG" 2>/dev/null; then
  fail "restore must not run without checksum"
fi

printf 'deadbeef  backup.dump\n' > "$DUMP.sha256"
if run_restore --yes >/dev/null 2>&1; then
  fail "bad checksum must abort"
fi
if grep -q '^pg_restore' "$FAKE_LOG" 2>/dev/null; then
  fail "restore must not run with bad checksum"
fi

sha256sum "$DUMP" > "$DUMP.sha256"

export FAKE_DB_EXISTS=1 FAKE_HAS_TABLES=1
: > "$FAKE_LOG"
if run_restore --yes >/dev/null 2>&1; then
  fail "non-empty target without --allow-existing must abort"
fi
if grep -q '^pg_restore' "$FAKE_LOG" 2>/dev/null; then
  fail "restore must not run against non-empty target"
fi

: > "$FAKE_LOG"
run_restore --yes --allow-existing >/dev/null
grep -q -- '--single-transaction' "$FAKE_LOG" || fail "allow-existing restore must use single transaction"
grep -q -- '--clean' "$FAKE_LOG" || fail "allow-existing restore must clean"
grep -q -- '--if-exists' "$FAKE_LOG" || fail "allow-existing restore must use --if-exists"

export FAKE_DB_EXISTS=0 FAKE_HAS_TABLES=0
: > "$FAKE_LOG"
run_restore --yes >/dev/null
grep -q 'CREATE DATABASE' "$FAKE_LOG" || fail "new database must be created"
grep -q -- '--single-transaction' "$FAKE_LOG" || fail "new database restore must use single transaction"
if grep -q -- '--clean' "$FAKE_LOG"; then
  fail "new database restore must not clean"
fi

export FAKE_DB_EXISTS=1 FAKE_HAS_TABLES=0
: > "$FAKE_LOG"
run_restore --yes >/dev/null
grep -q -- '--single-transaction' "$FAKE_LOG" || fail "empty existing database restore must use single transaction"
if grep -q -- '--clean' "$FAKE_LOG"; then
  fail "empty existing database restore must not clean"
fi

export FAKE_DB_EXISTS=1 FAKE_HAS_TABLES=1
: > "$FAKE_LOG"
: > "$FAKE_ENV_LOG"
CONNECTION='Host=db.internal;Port=5432;Database=whalepod;Username=app;Password="p;a ss""word";SSL Mode=VerifyFull;Root Certificate="/tmp/ca cert.pem";Client Certificate=/tmp/client.pem;Client Key=/tmp/client.key'
run_restore --yes --allow-existing >/dev/null
grep -qx 'PGSSLMODE=verify-full' "$FAKE_ENV_LOG" || fail "sslmode must be passed to pg_restore"
grep -qx 'PGSSLROOTCERT=/tmp/ca cert.pem' "$FAKE_ENV_LOG" || fail "root cert must be passed"
grep -qx 'PGSSLCERT=/tmp/client.pem' "$FAKE_ENV_LOG" || fail "client cert must be passed"
grep -qx 'PGPASSWORD=p;a ss"word' "$FAKE_ENV_LOG" || fail "quoted password must be parsed exactly"

: > "$FAKE_LOG"
export FAKE_RESTORE_EXIT=1
if run_restore --yes --allow-existing >/dev/null 2>&1; then
  fail "pg_restore failure must fail the script"
fi
grep -q -- '--single-transaction' "$FAKE_LOG" || fail "failing restore must still use single transaction"
unset FAKE_RESTORE_EXIT

export FAKE_DB_EXISTS=1 FAKE_HAS_TABLES=1
DRY_CONNECTION='Host=127.0.0.1;Database=whalepod;Username=app;Password="secret;value";SSL Mode=VerifyFull;Root Certificate=/tmp/ca.pem'
DRY_OUTPUT="$(WHALEPOD_DB_CONNECTION="$DRY_CONNECTION" PG_BIN="" bash "$RESTORE" \
  --backup "$DUMP" --database whalepod_test_restore \
  --confirm-database whalepod_test_restore --dry-run 2>&1)"
grep -q 'sslmode=verify-full' <<<"$DRY_OUTPUT" || fail "dry-run must show sslmode"
grep -q 'singleTransaction=true' <<<"$DRY_OUTPUT" || fail "dry-run must show transaction mode"
if grep -q 'secret;value' <<<"$DRY_OUTPUT"; then
  fail "dry-run must not print the password"
fi

echo "restore-postgres shell tests passed"
