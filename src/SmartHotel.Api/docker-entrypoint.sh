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
#                            Kept as they are when already set and non-empty;
#                            the matching file is then not read.
#   "$@"                     Arguments passed on to the API.
#
# Outputs
#   DB_PASSWORD and Jwt__SigningKey, exported from the files when they were
#   unset or empty. Then `exec dotnet SmartHotel.Api.dll "$@"` from the
#   image's working directory, /app: exec makes the API the container's main
#   process, so `docker stop` signals reach it.
#
# A missing or unreadable file is reported on stderr by its path, and the
# variable is left as it was. The script does not stop there:
# ConnectionStrings__SmartHotel may legitimately replace DB_PASSWORD, and the
# API's own startup validation reports a missing signing key.
#
# Never enable shell tracing or print a variable's value here: either would
# expose the secrets in the container log.

set -eu

log() {
    printf 'docker-entrypoint: %s\n' "$*" >&2
}

if [ -n "${SMARTHOTEL_SECRETS_DIR:-}" ]; then
    db_password_file="$SMARTHOTEL_SECRETS_DIR/db_password"
    jwt_signing_key_file="$SMARTHOTEL_SECRETS_DIR/jwt_signing_key"

    # Each value is assigned and exported separately so a failing cat still
    # stops the script under set -e. Command substitution strips only
    # trailing newlines, which the secret files never contain, so each value
    # equals its file exactly.
    if [ -z "${DB_PASSWORD:-}" ]; then
        if [ -r "$db_password_file" ]; then
            DB_PASSWORD="$(cat "$db_password_file")"
            export DB_PASSWORD
        else
            log "$db_password_file not found or not readable; leaving DB_PASSWORD unset"
        fi
    fi

    if [ -z "${Jwt__SigningKey:-}" ]; then
        if [ -r "$jwt_signing_key_file" ]; then
            Jwt__SigningKey="$(cat "$jwt_signing_key_file")"
            export Jwt__SigningKey
        else
            log "$jwt_signing_key_file not found or not readable; leaving Jwt__SigningKey unset"
        fi
    fi
fi

exec dotnet SmartHotel.Api.dll "$@"
