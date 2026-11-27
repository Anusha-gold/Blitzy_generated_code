#!/bin/sh
# Entrypoint for the SmartHotel API container.
#
# Purpose
#   Hand the local secrets to the API as process environment variables, the
#   only place the API reads secrets from, then replace this shell with the
#   API. The `db` service's wrapper (deploy/db/db-entrypoint.sh) creates the
#   secrets on the first start in the shared `secrets` volume, which Compose
#   mounts here read-only.
#   Before handing over the secrets, check the host ports that Compose
#   publishes and derives the CORS origin and the SPA's API base URL from,
#   and write the origin for web port 80 the way browsers send it.
#
# Inputs
#   SMARTHOTEL_API_PORT, SMARTHOTEL_WEB_PORT
#                            The host ports Compose publishes the API and the
#                            web app on. Each one that is set must be one
#                            port number from 1 to 65535, in decimal without
#                            leading zeros. Outside Compose both are unset
#                            and nothing is checked.
#   Cors__AllowedOrigin      The web app's origin. Compose writes it as
#                            http://localhost:$SMARTHOTEL_WEB_PORT.
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
#   Cors__AllowedOrigin, exported as http://localhost when
#   SMARTHOTEL_WEB_PORT is 80 and the origin is exactly http://localhost:80.
#   Any other origin is passed on unchanged.
#   DB_PASSWORD and Jwt__SigningKey, exported from the files when they were
#   unset. Then `exec dotnet SmartHotel.Api.dll "$@"` from the
#   image's working directory, /app: exec makes the API the container's main
#   process, so `docker stop` signals reach it.
#
# A port variable that is set to any other value, 0, a range or a leading
# zero included, stops the script before any secret file is read: one
# stderr line names the variable, never its value, and the script exits 1,
# so the API never starts and `docker compose up --wait` fails.
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
