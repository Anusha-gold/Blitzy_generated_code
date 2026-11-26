#!/bin/sh
# Entrypoint wrapper for the SmartHotel `db` service (postgres image).
#
# Purpose
#   On the first start, generate the local secrets, or accept them from the
#   host, and store them in the shared `secrets` volume. Then export the
#   database password as POSTGRES_PASSWORD and hand over to the postgres
#   image's own entrypoint. No secret lives in source or committed
#   configuration, and `docker compose up` needs no manual step.
#
# Inputs
#   SMARTHOTEL_DB_PASSWORD      Optional database password, 16-128 characters.
#   SMARTHOTEL_JWT_SIGNING_KEY  Optional API signing key, 32-256 characters.
#       Both are single-line values made only of A-Z a-z 0-9 . _ ~ + / = -
#       and an empty value means "no override". They are validated on every
#       start but applied on the first start only.
#   /run/smarthotel-secrets     The `secrets` volume: read-write here,
#                               read-only in the `api` service.
#   "$@"                        The postgres command, normally `postgres`.
#
# Outputs
#   db_password and jwt_signing_key in the secrets directory, written only
#   when absent: mode 0444, exactly the secret, no trailing newline. The
#   directory is mode 0755 so the API's non-root user can read them.
#   POSTGRES_PASSWORD, exported from db_password for the image entrypoint.
#
# Exit behaviour
#   Exits 1 before writing anything, with one stderr line naming each invalid
#   override but never its value. Also exits 1 when a secret cannot be
#   generated or written, or when an existing secret file is empty.
#   Otherwise execs the image entrypoint.
#
# Reset
#   PostgreSQL applies POSTGRES_PASSWORD only when it initializes an empty
#   data directory, so a restart never changes the role's password. The
#   secrets and `db-data` must therefore be reset together, with
#   "docker compose down -v".
#
# The file is bind-mounted and run as is: keep LF line endings. Never enable
# shell tracing or print the environment, which would expose the secrets.

set -eu

SECRETS_DIR=/run/smarthotel-secrets
CHARSET_RULE='characters from A-Z a-z 0-9 . _ ~ + / = -'
RESET_HINT='run "docker compose down -v" to reset the secrets and the database together'

log() {
    printf 'db-entrypoint: %s\n' "$*" >&2
}

# is_valid_override VALUE MIN MAX
# Succeeds when VALUE uses only the allowed characters and its length is
# within MIN..MAX. The character check runs first: once it passes the value
# is ASCII, so ${#} counts bytes even in a multibyte locale.
is_valid_override() {
    _candidate=$1
    _min=$2
    _max=$3
    case "$_candidate" in
        *[!A-Za-z0-9._~+/=-]*)
            _candidate=
            return 1
            ;;
    esac
    _length=${#_candidate}
    _candidate=
    [ "$_length" -ge "$_min" ] && [ "$_length" -le "$_max" ]
}

# generate_secret
# Prints 32 bytes from /dev/urandom as 64 lowercase hex characters. `-v`
# stops od from collapsing a repeated line into `*`.
generate_secret() {
    od -An -v -tx1 -N32 /dev/urandom | tr -d ' \n'
}

# ensure_secret NAME OVERRIDE VARIABLE
# Writes $SECRETS_DIR/NAME when it is absent: OVERRIDE when it is non-empty,
# a generated value otherwise. An existing file is never rewritten, chmodded
# or truncated, which is what makes the overrides first-start-only.
ensure_secret() {
    _name=$1
    _override=$2
    _variable=$3
    _file="$SECRETS_DIR/$_name"
    _tmp="$SECRETS_DIR/.$_name.tmp"

    if [ -e "$_file" ]; then
        _override_given=$_override
        _override=
        # Atomic writes make an empty file unreachable in normal operation;
        # refusing it keeps an empty POSTGRES_PASSWORD from being handed over.
        if [ ! -s "$_file" ]; then
            log "$_file is empty; $RESET_HINT"
            exit 1
        fi
        if [ -n "$_override_given" ]; then
            _override_given=
            log "$_variable ignored: $_name already exists and overrides" \
                "apply on the first start only; $RESET_HINT"
        fi
        return 0
    fi

    if [ -n "$_override" ]; then
        _secret=$_override
        _origin="from $_variable"
    else
        # Without pipefail a failing od only shows up as short output, so the
        # result is checked rather than the pipeline status.
        _secret=$(generate_secret) || _secret=
        case "$_secret" in
            *[!0-9a-f]*) _secret= ;;
        esac
        if [ "${#_secret}" -ne 64 ]; then
            _secret=
            log "failed to generate $_name"
            exit 1
        fi
        _origin=generated
    fi
    _override=

    # Write beside the target, then rename: the API never sees a partial file.
    # printf is a shell builtin, so the value never appears in an argv.
    # A temp file left by an interrupted start is removed first.
    # The steps are chained because set -e does not apply inside an if test.
    if ! (
        umask 077 &&
        rm -f "$_tmp" &&
        printf '%s' "$_secret" > "$_tmp" &&
        chmod 0444 "$_tmp" &&
        mv -f "$_tmp" "$_file"
    ); then
        _secret=
        log "failed to write $_file"
        exit 1
    fi
    _secret=
    log "$_name written ($_origin)"
}

# Validate every override before anything is written, on every start, so a
# bad host value always fails loudly. Both are checked before exiting so
# every problem is reported at once.
db_password_override=${SMARTHOTEL_DB_PASSWORD:-}
jwt_signing_key_override=${SMARTHOTEL_JWT_SIGNING_KEY:-}
invalid=0
if [ -n "$db_password_override" ] &&
    ! is_valid_override "$db_password_override" 16 128; then
    log "SMARTHOTEL_DB_PASSWORD is invalid (16-128 $CHARSET_RULE)"
    invalid=1
fi
if [ -n "$jwt_signing_key_override" ] &&
    ! is_valid_override "$jwt_signing_key_override" 32 256; then
    log "SMARTHOTEL_JWT_SIGNING_KEY is invalid (32-256 $CHARSET_RULE)"
    invalid=1
fi
if [ "$invalid" -ne 0 ]; then
    exit 1
fi

# Docker may create a new volume with a restrictive mode; the API's non-root
# user must be able to traverse it.
mkdir -p "$SECRETS_DIR"
chmod 0755 "$SECRETS_DIR"

ensure_secret db_password "$db_password_override" SMARTHOTEL_DB_PASSWORD
ensure_secret jwt_signing_key "$jwt_signing_key_override" SMARTHOTEL_JWT_SIGNING_KEY
unset db_password_override jwt_signing_key_override

# Assigned and exported separately so a failing cat still stops the script
# under set -e. Command substitution strips only trailing newlines, which no
# secret written here contains, so the value equals the file exactly.
POSTGRES_PASSWORD=$(cat "$SECRETS_DIR/db_password")
export POSTGRES_PASSWORD

# Keep the overrides, the signing key in particular, out of the server's
# environment.
unset SMARTHOTEL_DB_PASSWORD SMARTHOTEL_JWT_SIGNING_KEY

# The image entrypoint initializes db-data with POSTGRES_PASSWORD only when
# the data directory is empty, then drops to the postgres user.
exec /usr/local/bin/docker-entrypoint.sh "$@"
