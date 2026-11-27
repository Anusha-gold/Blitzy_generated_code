#!/bin/sh
# Entrypoint for the SmartHotel API container.
#
# Purpose
#   Hand the local secrets to the API as process environment variables, the
#   only place the API reads secrets from, then replace this shell with the
#   API. The `db` service's wrapper (deploy/db/db-entrypoint.sh) creates the
#   secrets on the first start in the shared `secrets` volume, which Compose
#   mounts here read-only.
#
# Inputs
#   SMARTHOTEL_SECRETS_DIR   Directory holding the secret files; Compose sets
#                            /run/smarthotel-secrets. Unset or empty skips
#                            the file lookup: outside local development the
#                            platform sets the variables below directly.
#   $SMARTHOTEL_SECRETS_DIR/db_password       The database password.
#   $SMARTHOTEL_SECRETS_DIR/jwt_signing_key   The JWT signing key.
#       Each file holds exactly the secret, with no trailing newline, made
#       only of the characters A-Z a-z 0-9 . _ ~ + / = -
#   DB_PASSWORD, Jwt__SigningKey
#                            Kept exactly as they are whenever set, even to an
#                            empty value; the matching file is then not read.
#                            The API's startup validation rejects an empty
#                            Jwt__SigningKey, and an empty DB_PASSWORD
#                            whenever the connection string is assembled from
#                            the DB_* parts.
#   "$@"                     Arguments passed on to the API.
#
# Outputs
#   DB_PASSWORD and Jwt__SigningKey, exported from the files when they were
#   unset. Then `exec dotnet SmartHotel.Api.dll "$@"` from the
#   image's working directory, /app: exec makes the API the container's main
#   process, so `docker stop` signals reach it.
#
# When a file is missing, is not a readable regular file, or holds an empty
# value or any byte outside that set, a trailing newline or a NUL byte
# included, its variable is left unset and one stderr line names the file
# and the variable, never the directory or the content. The script does
# not stop there: ConnectionStrings__SmartHotel may legitimately replace
# DB_PASSWORD, and the API's own startup validation reports a missing
# signing key.
#
# Never enable shell tracing or print a variable's value here: either would
# expose the secrets in the container log.

set -eu

log() {
    printf 'docker-entrypoint: %s\n' "$*" >&2
}

# export_secret_file NAME VARIABLE
# Exports VARIABLE from $SMARTHOTEL_SECRETS_DIR/NAME when that is a readable
# regular file holding exactly a non-empty value made only of the allowed
# characters. Otherwise VARIABLE is unset and one line naming only NAME and
# VARIABLE is logged. The directory is deployment input and is never
# printed, so the messages of cat, wc and the shell, which would print it,
# are discarded. Callers pass fixed names only. Command substitution drops
# trailing newlines, and BusyBox ash drops NUL bytes too, so the value read
# can be shorter than the file. Once the character check has passed the
# value is ASCII and ${#} counts its bytes, so the value equals the file
# only when the file's size, which wc pads with blanks on some systems,
# equals that count; any other file, a failing wc included, is malformed.
export_secret_file() {
    _name=$1
    _variable=$2
    _file="$SMARTHOTEL_SECRETS_DIR/$_name"
    if [ ! -f "$_file" ] || [ ! -r "$_file" ] ||
        ! _value=$(cat "$_file" 2>/dev/null); then
        _value=
        unset "$_variable"
        log "$_name in SMARTHOTEL_SECRETS_DIR is missing or unreadable;" \
            "leaving $_variable unset"
        return 0
    fi
    _valid=1
    case $_value in
        '' | *[!A-Za-z0-9._~+/=-]*) _valid=0 ;;
    esac
    if [ "$_valid" -eq 1 ] &&
        [ "$({ wc -c < "$_file"; } 2>/dev/null | tr -d ' \t')" != \
            "${#_value}" ]; then
        _valid=0
    fi
    if [ "$_valid" -eq 0 ]; then
        _value=
        unset "$_variable"
        log "$_name in SMARTHOTEL_SECRETS_DIR is empty or malformed;" \
            "leaving $_variable unset"
        return 0
    fi
    export "$_variable=$_value"
    _value=
}

if [ -n "${SMARTHOTEL_SECRETS_DIR:-}" ]; then
    if [ -z "${DB_PASSWORD+set}" ]; then
        export_secret_file db_password DB_PASSWORD
    fi
    if [ -z "${Jwt__SigningKey+set}" ]; then
        export_secret_file jwt_signing_key Jwt__SigningKey
    fi
fi

exec dotnet SmartHotel.Api.dll "$@"
