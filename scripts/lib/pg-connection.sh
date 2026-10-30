#!/usr/bin/env bash
# Shared Npgsql-style connection-string parsing for libpq tools.
# Values may be quoted with single or double quotes; quotes are removed and
# doubled quotes are treated as literal quotes. Password values are never echoed.

PG_CONN_HOST="127.0.0.1"
PG_CONN_PORT="5432"
PG_CONN_DATABASE=""
PG_CONN_USER=""
PG_CONN_PASSWORD=""
PG_CONN_SSLMODE=""
PG_CONN_SSLROOTCERT=""
PG_CONN_SSLCERT=""
PG_CONN_SSLKEY=""
PG_CONN_SSLPASSWORD=""

pg_connection_trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

pg_connection_normalize_sslmode() {
  case "${1,,}" in
    verifyca|verify-ca) printf 'verify-ca' ;;
    verifyfull|verify-full) printf 'verify-full' ;;
    disable|allow|prefer|require) printf '%s' "${1,,}" ;;
    *) printf '%s' "$1" ;;
  esac
}

pg_connection_store() {
  local part="$1"
  local quoted="$2"
  local trimmed
  trimmed="$(pg_connection_trim "$part")"
  [[ -z "$trimmed" ]] && return 0
  [[ "$part" != *=* ]] && return 0

  local key="${part%%=*}"
  local value="${part#*=}"
  key="$(printf '%s' "$key" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
  if [[ "$quoted" != "1" ]]; then
    value="$(pg_connection_trim "$value")"
  fi

  case "$key" in
    host) PG_CONN_HOST="$value" ;;
    port) PG_CONN_PORT="$value" ;;
    database) PG_CONN_DATABASE="$value" ;;
    username) PG_CONN_USER="$value" ;;
    password) PG_CONN_PASSWORD="$value" ;;
    sslmode) PG_CONN_SSLMODE="$(pg_connection_normalize_sslmode "$value")" ;;
    rootcertificate|sslrootcert) PG_CONN_SSLROOTCERT="$value" ;;
    sslcertificate|sslcert|clientcertificate) PG_CONN_SSLCERT="$value" ;;
    sslkey|clientkey|clientcertificatekey) PG_CONN_SSLKEY="$value" ;;
    sslpassword) PG_CONN_SSLPASSWORD="$value" ;;
    *) ;;
  esac
}

pg_connection_parse() {
  local input="$1"
  local current=""
  local quote=""
  local quoted=0
  local char
  local i=0
  local length=${#input}

  PG_CONN_HOST="127.0.0.1"
  PG_CONN_PORT="5432"
  PG_CONN_DATABASE=""
  PG_CONN_USER=""
  PG_CONN_PASSWORD=""
  PG_CONN_SSLMODE=""
  PG_CONN_SSLROOTCERT=""
  PG_CONN_SSLCERT=""
  PG_CONN_SSLKEY=""
  PG_CONN_SSLPASSWORD=""

  while (( i < length )); do
    char="${input:i:1}"
    if [[ -n "$quote" ]]; then
      if [[ "$char" == "$quote" ]]; then
        if (( i + 1 < length )) && [[ "${input:i+1:1}" == "$quote" ]]; then
          current+="$quote"
          ((i += 2))
          continue
        fi
        quote=""
      else
        current+="$char"
      fi
      ((i += 1))
      continue
    fi

    case "$char" in
      "'"|'"')
        quote="$char"
        quoted=1
        ;;
      \;)
        pg_connection_store "$current" "$quoted"
        current=""
        quoted=0
        ;;
      *) current+="$char" ;;
    esac
    ((i += 1))
  done

  if [[ -n "$quote" ]]; then
    echo "Connection string contains an unterminated quote." >&2
    return 1
  fi

  pg_connection_store "$current" "$quoted"
  if [[ ! "$PG_CONN_PORT" =~ ^[0-9]+$ ]]; then
    echo "Connection string contains an invalid port." >&2
    return 1
  fi
}

pg_connection_export() {
  export PGHOST="${PG_CONN_HOST:-127.0.0.1}"
  export PGPORT="${PG_CONN_PORT:-5432}"
  if [[ -n "$PG_CONN_USER" ]]; then export PGUSER="$PG_CONN_USER"; fi
  if [[ -n "$PG_CONN_PASSWORD" ]]; then export PGPASSWORD="$PG_CONN_PASSWORD"; fi
  if [[ -n "$PG_CONN_SSLMODE" ]]; then export PGSSLMODE="$PG_CONN_SSLMODE"; fi
  if [[ -n "$PG_CONN_SSLROOTCERT" ]]; then export PGSSLROOTCERT="$PG_CONN_SSLROOTCERT"; fi
  if [[ -n "$PG_CONN_SSLCERT" ]]; then export PGSSLCERT="$PG_CONN_SSLCERT"; fi
  if [[ -n "$PG_CONN_SSLKEY" ]]; then export PGSSLKEY="$PG_CONN_SSLKEY"; fi
  if [[ -n "$PG_CONN_SSLPASSWORD" ]]; then export PGSSLPASSWORD="$PG_CONN_SSLPASSWORD"; fi
}

pg_connection_clear_secrets() {
  unset PGPASSWORD PGSSLPASSWORD || true
}
