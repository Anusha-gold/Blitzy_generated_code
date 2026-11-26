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
#       and an empty value means "no override". They are read only when a
#       secret file is missing, and then both are validated before anything
#       is written. When both files exist they are ignored, with a notice.
#   /run/smarthotel-secrets     The `secrets` volume: read-write here,
#                               read-only in the `api` service.
#   "$@"                        The postgres command, normally `postgres`.
#
# Outputs
#   db_password and jwt_signing_key in the secrets directory, written only
#   when absent: mode 0444, exactly the secret, no trailing newline. The
#   directory is mode 0755 so the API's non-root user can read them.
#   .lock, an empty file in the same directory, created by any start that
#   has a secret to write: it makes concurrent first starts on one volume
#   take turns.
#   POSTGRES_PASSWORD, exported from db_password for the image entrypoint.
#   For a server start, value-free server log settings placed before the
#   caller's postgres options.
#
# Exit behaviour
#   Exits 2 with a usage line on stderr, before anything is read or written,
#   when no command is given or the command is empty.
#   When a secret file is missing, exits 1 before writing anything, with one
#   stderr line naming each invalid override but never its value. Also
#   exits 1 when an existing secret file is not a readable regular file
#   holding a valid secret, which is left in place, or when a secret cannot
#   be locked, generated or written.
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
LOCK_FILE="$SECRETS_DIR/.lock"
CHARSET_RULE='characters from A-Z a-z 0-9 . _ ~ + / = -'
RESET_HINT='run "docker compose down -v" to reset the secrets and the database together'

log() {
    printf 'db-entrypoint: %s\n' "$*" >&2
}

# is_valid_override VALUE MIN MAX
# Succeeds when VALUE uses only the allowed characters and its length is
# within MIN..MAX. It checks the overrides and the content of existing secret
# files. The character check runs first: once it passes the value
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

# starts_server ARG...
# Succeeds when ARG... starts the server the way the image entrypoint
# recognises it: `postgres`, or a first argument starting with "-", which
# the image entrypoint prefixes with `postgres`. Fails when an argument asks
# for help or the version, or the first postgres argument selects the
# single-user, bootstrap or check mode: postgres honours those only as its
# first argument, so nothing may be placed in front of them.
starts_server() {
    case ${1:-} in
        postgres) shift ;;
        -*) ;;
        *) return 1 ;;
    esac
    case ${1:-} in
        --single | --boot | --check) return 1 ;;
    esac
    for _arg in "$@"; do
        case $_arg in
            -'?' | --help | --describe-config | -V | --version)
                _arg=
                return 1
                ;;
        esac
    done
    _arg=
    return 0
}

# secret_present NAME MIN MAX
# Succeeds when $SECRETS_DIR/NAME is a readable regular file holding exactly
# a valid secret of MIN..MAX characters, and fails when nothing exists under
# that name. Anything else, such as an empty file, a valid secret followed
# by a newline, a directory or a dangling link, is refused with exit 1 and
# left in place: an existing entry is never replaced. The diagnostic names
# the file, never its content, and the messages of cat, wc and the shell are
# discarded. Command substitution drops trailing newlines, and BusyBox ash
# drops NUL bytes too, so the value read can be shorter than the file. Once
# the character check has passed the value is ASCII and ${#} counts its
# bytes, so the stored bytes are the value only when the file's size, which
# wc pads with blanks on some systems, equals that count. A failing wc
# prints nothing and fails the comparison.
secret_present() {
    _file="$SECRETS_DIR/$1"
    if [ ! -e "$_file" ] && [ ! -L "$_file" ]; then
        return 1
    fi
    if [ -f "$_file" ] && [ -r "$_file" ] &&
        _content=$(cat "$_file" 2>/dev/null) &&
        is_valid_override "$_content" "$2" "$3" &&
        [ "$({ wc -c < "$_file"; } 2>/dev/null | tr -d ' \t')" = \
            "${#_content}" ]; then
        _content=
        return 0
    fi
    _content=
    log "$1 exists but is not a readable regular file holding a valid" \
        "secret; $RESET_HINT"
    exit 1
}

# check_secrets
# Sets db_password_missing and jwt_signing_key_missing to 1 for a file that
# does not exist and to 0 for one that holds a valid secret. Both files are
# checked every time, so a malformed one stops the start before anything is
# written.
check_secrets() {
    db_password_missing=0
    secret_present db_password 16 128 || db_password_missing=1
    jwt_signing_key_missing=0
    secret_present jwt_signing_key 32 256 || jwt_signing_key_missing=1
}

# report_ignored NAME VARIABLE
# Reports that the override VARIABLE is ignored because NAME already exists.
# The caller only tests that VARIABLE is set and never reads its value.
report_ignored() {
    log "$2 ignored: $1 already exists and overrides apply on the first" \
        "start only; $RESET_HINT"
}

# write_secret NAME OVERRIDE VARIABLE
# Writes $SECRETS_DIR/NAME: OVERRIDE, the value of VARIABLE, when it is
# non-empty, a generated value otherwise. The caller holds the lock, has
# just found the file absent and has validated OVERRIDE. An existing file is
# never passed here, so it is never rewritten, chmodded or truncated, which
# is what makes the overrides first-start-only.
write_secret() {
    _name=$1
    _override=$2
    _variable=$3
    _file="$SECRETS_DIR/$_name"
    _tmp="$SECRETS_DIR/.$_name.tmp"

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
    # A temp file left by an interrupted start is removed first; the lock
    # keeps every other start away from the temp file and the target.
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

# Refuse a missing or empty command before anything is read or written:
# without one the image entrypoint skips initialization and exits 0 without
# starting a database.
if [ "$#" -eq 0 ] || [ -z "$1" ]; then
    log "usage: db-entrypoint.sh postgres [option...]"
    exit 2
fi

# The override values are expanded only where a secret file is missing, to
# validate them and to write the missing file; "${VARIABLE:+set}" tests
# whether an override is set without expanding its value. When both files
# hold valid secrets nothing is validated or written: the overrides apply on
# the first start only, so a changed one cannot stop a restart and is only
# reported as ignored.
check_secrets
if [ "$db_password_missing" -eq 1 ] ||
    [ "$jwt_signing_key_missing" -eq 1 ]; then
    # Validate every override before anything is written, so a bad host
    # value fails loudly. Both are checked before exiting so every problem
    # is reported at once.
    invalid=0
    if [ -n "${SMARTHOTEL_DB_PASSWORD:+set}" ] &&
        ! is_valid_override "$SMARTHOTEL_DB_PASSWORD" 16 128; then
        log "SMARTHOTEL_DB_PASSWORD is invalid (16-128 $CHARSET_RULE)"
        invalid=1
    fi
    if [ -n "${SMARTHOTEL_JWT_SIGNING_KEY:+set}" ] &&
        ! is_valid_override "$SMARTHOTEL_JWT_SIGNING_KEY" 32 256; then
        log "SMARTHOTEL_JWT_SIGNING_KEY is invalid (32-256 $CHARSET_RULE)"
        invalid=1
    fi
    if [ "$invalid" -ne 0 ]; then
        exit 1
    fi

    # Docker may create a new volume with a restrictive mode; the API's
    # non-root user must be able to traverse it.
    mkdir -p "$SECRETS_DIR"
    chmod 0755 "$SECRETS_DIR"

    # Concurrent first starts on one volume take turns from here. Each one
    # checks the files again under the lock and writes only what is still
    # missing, so none removes another's temp file or replaces a secret
    # another has published. The lock belongs to descriptor 9 and is
    # released when the descriptor is closed below or this shell dies.
    if ! { command exec 9>>"$LOCK_FILE"; } 2>/dev/null || ! flock -x 9; then
        log "failed to lock $LOCK_FILE"
        exit 1
    fi
    check_secrets
fi

# Write each file still missing. One that exists is kept, and its override,
# when set, is only reported as ignored.
if [ "$db_password_missing" -eq 1 ]; then
    write_secret db_password "${SMARTHOTEL_DB_PASSWORD:-}" \
        SMARTHOTEL_DB_PASSWORD
elif [ -n "${SMARTHOTEL_DB_PASSWORD:+set}" ]; then
    report_ignored db_password SMARTHOTEL_DB_PASSWORD
fi
if [ "$jwt_signing_key_missing" -eq 1 ]; then
    write_secret jwt_signing_key "${SMARTHOTEL_JWT_SIGNING_KEY:-}" \
        SMARTHOTEL_JWT_SIGNING_KEY
elif [ -n "${SMARTHOTEL_JWT_SIGNING_KEY:+set}" ]; then
    report_ignored jwt_signing_key SMARTHOTEL_JWT_SIGNING_KEY
fi

# Release the lock, when it was taken, before the handoff: the server must
# not inherit it.
exec 9>&-

# Read from the published file, so a start that waited for the lock exports
# the password the first start wrote. Assigned and exported separately so a
# failing cat still stops the script under set -e. The file was either
# written by this start or found by secret_present to hold exactly its
# value, so command substitution drops nothing and the value is the secret.
POSTGRES_PASSWORD=$(cat "$SECRETS_DIR/db_password")
export POSTGRES_PASSWORD

# Keep the overrides, the signing key in particular, out of the server's
# environment.
unset SMARTHOTEL_DB_PASSWORD SMARTHOTEL_JWT_SIGNING_KEY

# Value-free server log. At its default settings an error record in the
# server log carries DETAIL, where a unique or exclusion violation prints the
# conflicting key (a guest's email, for one), and the failed STATEMENT with
# its bind parameters. TERSE drops DETAIL, HINT, QUERY and CONTEXT; PANIC
# stops failed statements from being logged; 0 stops bind parameters from
# being logged with errors and with logged statements. These settings shape
# the server log only: the server still sends clients the SQLSTATE, the
# constraint name and DETAIL, and the API's 23505 and 23P01 translation
# needs only the SQLSTATE and the constraint name.
# They are added only to a server start, and the image entrypoint passes the
# same arguments to its initialization server and to `postgres -C`, so the
# first start is covered as well. Later -c options in "$@" override them.
# Settings in postgresql.conf or ALTER SYSTEM do not, because command-line
# options take precedence over the configuration files. Any other command
# is forwarded unchanged.
if starts_server "$@"; then
    if [ "$1" = postgres ]; then
        shift
    fi
    set -- postgres \
        -c log_error_verbosity=terse \
        -c log_min_error_statement=panic \
        -c log_parameter_max_length_on_error=0 \
        -c log_parameter_max_length=0 \
        "$@"
fi

# The image entrypoint, running as root, creates the data directory and
# hands it to the postgres user with mode 0700, then re-executes itself as
# that user through gosu. Only then, and only when the data directory is
# empty, does it initialize db-data with POSTGRES_PASSWORD; an existing
# cluster keeps its role password.
exec /usr/local/bin/docker-entrypoint.sh "$@"
