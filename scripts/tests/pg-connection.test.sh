#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/pg-connection.sh
source "$SCRIPT_DIR/../lib/pg-connection.sh"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

pg_connection_parse 'Host=127.0.0.1;Port=55438;Database=whalepod_test_parser;Username=review;Password=plain'
[[ "$PG_CONN_HOST" == "127.0.0.1" ]] || fail "host"
[[ "$PG_CONN_PORT" == "55438" ]] || fail "port"
[[ "$PG_CONN_DATABASE" == "whalepod_test_parser" ]] || fail "database"
[[ "$PG_CONN_USER" == "review" ]] || fail "user"
[[ "$PG_CONN_PASSWORD" == "plain" ]] || fail "password"

pg_connection_parse 'Host=db.internal;Port=5432;Database=whalepod;Username=app;Password="p;a ss""word";SSL Mode=VerifyFull;Root Certificate="/tmp/ca cert.pem";Client Certificate=/tmp/client.pem;Client Key=/tmp/client.key;Ssl Password="k;ey"'
[[ "$PG_CONN_HOST" == "db.internal" ]] || fail "host with ssl"
[[ "$PG_CONN_PASSWORD" == 'p;a ss"word' ]] || fail "quoted password with semicolon/quote/space"
[[ "$PG_CONN_SSLMODE" == "verify-full" ]] || fail "sslmode"
[[ "$PG_CONN_SSLROOTCERT" == "/tmp/ca cert.pem" ]] || fail "root cert"
[[ "$PG_CONN_SSLCERT" == "/tmp/client.pem" ]] || fail "client cert"
[[ "$PG_CONN_SSLKEY" == "/tmp/client.key" ]] || fail "client key"
[[ "$PG_CONN_SSLPASSWORD" == "k;ey" ]] || fail "ssl password"

pg_connection_parse "Host=127.0.0.1;Database=whalepod;Username=app;Password='single;quoted value'"
[[ "$PG_CONN_PASSWORD" == "single;quoted value" ]] || fail "single-quoted password"

if pg_connection_parse 'Host=127.0.0.1;Password="unterminated' 2>/dev/null; then
  fail "unterminated quote must be rejected"
fi

if pg_connection_parse 'Host=127.0.0.1;Port=not-a-port' 2>/dev/null; then
  fail "invalid port must be rejected"
fi

pg_connection_export
[[ "${PGSSLMODE:-}" == "" ]] || fail "sslmode should be cleared by a new parse"
pg_connection_clear_secrets
[[ "${PGPASSWORD:-}" == "" ]] || fail "password should be cleared"

echo "pg-connection shell tests passed"
