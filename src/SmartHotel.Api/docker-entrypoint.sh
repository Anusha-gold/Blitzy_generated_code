#!/bin/sh
# Entrypoint for the SmartHotel API container.
#
# Checks the host ports Compose publishes (SMARTHOTEL_API_PORT and
# SMARTHOTEL_WEB_PORT, each when set) and exports the local secrets
# db_password and jwt_signing_key from $SMARTHOTEL_SECRETS_DIR as
# DB_PASSWORD and Jwt__SigningKey. Compose mounts the `secrets` volume
# there read-only; deploy/db/db-entrypoint.sh writes it. Then
# `exec dotnet SmartHotel.Api.dll "$@"`, from /app, makes the API the
# container's main process.
#
# An invalid published port exits 1 before any secret file is read. An
# explicit DB_PASSWORD or Jwt__SigningKey, even an empty one, takes
# precedence and its file is not read. A missing, unreadable or malformed
# file leaves its variable unset for the API's startup validation, since
# ConnectionStrings__SmartHotel may replace DB_PASSWORD. Never enable shell
# tracing or print a value: messages name only variables and files.

set -eu

log() {
    printf 'docker-entrypoint: %s\n' "$*" >&2
}

# require_port VARIABLE VALUE
# Exits 1 after one stderr line naming VARIABLE, never VALUE, unless VALUE
# is one port number from 1 to 65535, written in decimal without leading
# zeros: the only form that publishes one fixed host port and reads the
# same in a URL. Compose itself also accepts 0 (a random port), ranges
# such as 14200-14201 and leading zeros such as 04200. The digits are
# listed rather than written as a range, which some locales widen, and a
# value of six or more characters is rejected before the numeric test.
require_port() {
    case $2 in
        '' | 0* | *[!0123456789]* | ??????*) _port_valid=0 ;;
        *) _port_valid=1 ;;
    esac
    if [ "$_port_valid" -eq 1 ] && [ "$2" -gt 65535 ]; then
        _port_valid=0
    fi
    if [ "$_port_valid" -eq 0 ]; then
        log "$1 must be one port number from 1 to 65535, in decimal" \
            "without leading zeros"
        exit 1
    fi
}

# Compose passes both host ports in and writes the CORS origin as
# http://localhost:$SMARTHOTEL_WEB_PORT. Outside Compose neither port is
# set and nothing is checked. The checks run before any secret file is
# read.
if [ -n "${SMARTHOTEL_API_PORT+set}" ]; then
    require_port SMARTHOTEL_API_PORT "$SMARTHOTEL_API_PORT"
fi
if [ -n "${SMARTHOTEL_WEB_PORT+set}" ]; then
    require_port SMARTHOTEL_WEB_PORT "$SMARTHOTEL_WEB_PORT"
    # Browsers omit the scheme's default port from the Origin header, and
    # the API accepts the origin only in that form, but Compose can only
    # append the port. Any other origin is left to the API's validation.
    if [ "$SMARTHOTEL_WEB_PORT" = 80 ] &&
        [ "${Cors__AllowedOrigin-}" = http://localhost:80 ]; then
        export Cors__AllowedOrigin=http://localhost
    fi
fi

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
