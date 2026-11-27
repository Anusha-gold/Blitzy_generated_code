/**
 * Playwright global setup for the web end-to-end suite: builds the E-STACK
 * acceptance stack before any spec runs or, in reuse mode, confirms that an
 * already running stack answers.
 *
 * `playwright.config.ts` names this file as `globalSetup`, so it runs once per
 * `ng test` and once per `npx playwright test`, before the first spec. The
 * stack contract (project, ports, Compose selection and target URLs) lives in
 * `./support/stack-target`, which `playwright.config.ts` also reads, so the
 * stack built here is the one every spec targets.
 *
 * The acceptance project is one per Docker host, so a fresh run owns it by
 * listening on `127.0.0.1:18079` from before its first Compose call until the
 * suite's global teardown. The operating system frees that port when the run
 * ends, however it ends, and a concurrent fresh run finds it taken and fails
 * before any Compose call instead of removing the stack under test.
 *
 * Every Compose command runs under a deadline, 5 minutes for a `down` and 30
 * minutes for the `up`. One that outlives it is stopped with its whole
 * process tree, the `docker` CLI, the Compose plugin the CLI runs and every
 * process they started, and nothing else: SIGTERM, then SIGKILL (on Windows,
 * `taskkill /T /F`). Setup fails and releases the lock only after a bounded
 * drain in which that tree has gone and its pipes have closed, so a stalled
 * Docker operation can neither hold the lock indefinitely nor, short of a
 * process that survives SIGKILL, keep running once the lock is released.
 *
 * Playwright transpiles this file as CommonJS and it is never type-checked
 * (no `@types/node`; `tsconfig.app.json` excludes `e2e/**`). It uses Node's
 * built-in modules and globals only.
 *
 * What it passes and prints:
 * - It passes Compose no secret of its own. The child environment drops every
 *   `SMARTHOTEL_*` first-start override and sets only the two acceptance
 *   ports; every other variable passes through unchanged, so the Docker CLI
 *   keeps working. The stack's secrets are generated inside the `db`
 *   container and are never supplied here.
 * - Its own messages never print a raw `E2E_*` setting or the text of a
 *   caught error. They hold only fixed text, the project name, the acceptance
 *   and lock ports, the target URLs in the canonical form
 *   `./support/stack-target` validated them into (scheme, host, port and
 *   path, never a user name, password, query or fragment), the web app's
 *   `/config.json` URL built from that canonical origin, HTTP statuses,
 *   fixed-text reasons why `/config.json` does not name the target API (never
 *   the value it serves), validated error codes such as `ECONNREFUSED`, the
 *   Compose deadlines, the manual removal command with this checkout's
 *   paths, and the process id, start time and checkout path the run owning
 *   the lock reports, with control characters removed.
 * - Only the last 1 MiB of characters of a Compose command's output is kept,
 *   from the first line that starts within it; a line that limit cuts is
 *   dropped whole, the part of it written later included, so no line's end
 *   is kept without its start. When a Compose command fails or times out,
 *   that output is printed filtered, limited to the last 200 lines of at most
 *   500 characters, after a marker when earlier output was dropped, and with
 *   every line prefixed `  compose | ` so it cannot pass for a message of
 *   this setup.
 *   The filter removes control characters and escape sequences and redacts
 *   what its patterns recognize: credential headers and key-value pairs,
 *   connection strings, bearer and basic credentials, JWTs, URL user
 *   information, queries and fragments, email addresses, long mixed-case
 *   tokens and the values of the child's credential-named environment
 *   variables. Because it is pattern-based, that output can still contain
 *   sensitive text it does not recognize.
 */
import { type ChildProcess, spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';

import {
  ACCEPTANCE_API_PORT,
  ACCEPTANCE_LOCK_PORT,
  ACCEPTANCE_PROJECT,
  ACCEPTANCE_WEB_PORT,
  type StackTarget,
  composeArgs,
  composeEnvironment,
  findRepositoryRoot,
  isReuseMode,
  resolveStackTarget,
} from './support/stack-target';

/** The outcome of one Compose command. */
interface ComposeResult {
  /** The exit code; a child that ended on a signal, or never exited, counts as `1`. */
  code: number;
  /**
   * What the command wrote to stdout and stderr, in arrival order: all of it,
   * or, when {@link outputDropped} is set, only its last whole lines within
   * {@link COMPOSE_OUTPUT_MAX_CHARACTERS}.
   */
  output: string;
  /** Whether earlier output was dropped to keep {@link output} within its cap. */
  outputDropped: boolean;
  /** Whether the command outlived its deadline and was stopped. */
  timedOut: boolean;
}

/** Removes the acceptance project with its containers, networks and volumes. */
const DOWN_ARGS = ['down', '-v', '--remove-orphans'];

/**
 * Builds current images and starts db, api and web from fresh volumes.
 * `--wait` (Compose v2.1.1 or later) returns only once every service's
 * healthcheck passes, and the `api` healthcheck passes only after migration
 * and seeding, so success means a migrated and seeded stack. It runs under
 * {@link COMPOSE_UP_TIMEOUT_MS}, which leaves room for a first image build
 * on a clean machine, a matter of several minutes.
 */
const UP_ARGS = ['up', '-d', '--build', '--force-recreate', '--wait'];

/**
 * Deadline for each {@link DOWN_ARGS} command, the removal before the build
 * and the removal after a failed `up`: 5 minutes. A removal stops the
 * project's containers, each within Compose's default 10-second stop
 * timeout, and removes its networks and volumes, so one still running then
 * is stalled.
 */
const COMPOSE_DOWN_TIMEOUT_MS = 5 * 60_000;

/**
 * Deadline for the {@link UP_ARGS} command: 30 minutes. A first build on a
 * clean machine pulls the base images, restores the NuGet and npm packages
 * and builds both images within it, so only a stalled build or start reaches
 * it.
 */
const COMPOSE_UP_TIMEOUT_MS = 30 * 60_000;

/**
 * How long a Compose command that outlived its deadline, the `docker` CLI and
 * every process it started, gets to exit after SIGTERM before what is left of
 * it is sent SIGKILL.
 */
const COMPOSE_KILL_GRACE_MS = 10_000;

/**
 * How long a Compose command gets, once it has exited or its process tree has
 * been sent SIGKILL, for its output pipes to close and, after a timeout, for
 * every process of its tree to be gone. A process it started, such as the
 * Compose plugin, can hold the pipes open after it; they are then closed from
 * this side.
 */
const COMPOSE_DRAIN_TIMEOUT_MS = 10_000;

/**
 * How often the process tree of a timed-out Compose command is read again
 * while it is stopped and drained, so a process it starts meanwhile is
 * signalled too and the wait ends soon after the whole tree is gone.
 */
const COMPOSE_STOP_POLL_INTERVAL_MS = 250;

/**
 * Upper bound for one run of `ps` or `taskkill` while a timed-out Compose
 * command is stopped. Both run synchronously, so a hung one must not stall
 * the stop.
 */
const PROCESS_TOOL_TIMEOUT_MS = 2_000;

/** Upper bound, in bytes, for the process list `ps` prints. */
const PROCESS_LIST_MAX_BYTES = 16 * 1_048_576;

/**
 * The most times the process tree of a timed-out Compose command is read
 * again while it is frozen before SIGKILL. A frozen process starts no other,
 * so each read finds only processes started just before their parent was
 * frozen, and a read or two normally finds no new one.
 */
const PROCESS_TREE_FREEZE_MAX_PASSES = 10;

/**
 * The most characters (UTF-16 code units) of a Compose command's output that
 * are kept, its last ones: 1 MiB. While the command runs, at most twice that
 * is held before the oldest text is dropped, at a line boundary; a line that
 * cut falls in is dropped whole, the part of it written later included.
 */
const COMPOSE_OUTPUT_MAX_CHARACTERS = 1_048_576;

/** HTTP probe attempts, which absorb a listener that has only just started. */
const PROBE_ATTEMPTS = 10;

/** Pause between HTTP probe attempts. */
const PROBE_INTERVAL_MS = 1_000;

/** Upper bound for one HTTP probe request, body included. */
const PROBE_REQUEST_TIMEOUT_MS = 10_000;

/** Upper bound for one TCP connection attempt of the port check. */
const PORT_CHECK_TIMEOUT_MS = 1_000;

/** The most lines of a failed command's output that are printed: its last ones. */
const COMPOSE_OUTPUT_MAX_LINES = 200;

/** The most characters of one output line that are printed. */
const COMPOSE_OUTPUT_MAX_LINE_LENGTH = 500;

/**
 * Prefix of every printed output line, so text from the child process cannot
 * pass for a message of this setup.
 */
const COMPOSE_OUTPUT_PREFIX = '  compose | ';

/** What a redacted piece of output is replaced with. */
const REDACTED = '<redacted>';

/**
 * Names of the Compose child's environment variables whose values are
 * redacted wherever they appear in printed output, outside the project's own
 * Compose names.
 */
const SENSITIVE_VARIABLE_NAME = /PASS|SECRET|TOKEN|KEY|CRED|AUTH/i;

/**
 * The shortest environment value redacted by its exact text; a shorter one
 * would also match ordinary words.
 */
const MIN_REDACTED_VALUE_LENGTH = 6;

/** Resolves after `ms` milliseconds. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Reads the `code` property of a Node.js system error, if it has one. */
function errorCode(err: unknown): unknown {
  return typeof err === 'object' && err !== null && 'code' in err
    ? (err as { code: unknown }).code
    : undefined;
}

/** Reads the `name` property of an error, such as `TimeoutError`, if it has one. */
function errorName(err: unknown): unknown {
  return typeof err === 'object' && err !== null && 'name' in err
    ? (err as { name: unknown }).name
    : undefined;
}

/** Reads the `cause` property of an error, if it has one. */
function errorCause(err: unknown): unknown {
  return typeof err === 'object' && err !== null && 'cause' in err
    ? (err as { cause: unknown }).cause
    : undefined;
}

/** The shape of a Node.js error code, such as `ECONNREFUSED` or `UND_ERR_SOCKET`. */
const ERROR_CODE_PATTERN = /^[A-Z][A-Z0-9_]{0,63}$/;

/**
 * Returns the `code` of a Node.js system error when it has the shape of an
 * error code, so a message can name it without repeating any foreign text.
 */
function validatedErrorCode(err: unknown): string | undefined {
  const code = errorCode(err);
  return typeof code === 'string' && ERROR_CODE_PATTERN.test(code) ? code : undefined;
}

/**
 * Formats the validated code of `err` for the end of a fixed message, as
 * ` (EADDRNOTAVAIL)`, or returns an empty string when it has none.
 */
function errorCodeNote(err: unknown): string {
  const code = validatedErrorCode(err);
  return code === undefined ? '' : ` (${code})`;
}

/** Plain-language labels for the network error codes a probe meets most often. */
const NETWORK_ERROR_LABELS = new Map<string, string>([
  ['ECONNREFUSED', 'connection refused'],
  ['ECONNRESET', 'connection reset'],
  ['ENOTFOUND', 'host not found'],
  ['EAI_AGAIN', 'host lookup failed'],
  ['ETIMEDOUT', 'connection timed out'],
  ['UND_ERR_CONNECT_TIMEOUT', 'connection timed out'],
  ['EHOSTUNREACH', 'host unreachable'],
  ['ENETUNREACH', 'network unreachable'],
]);

/**
 * Describes a failed probe request as a fixed category: a timeout, a network
 * error with its validated code, or a plain network error. The error's own
 * message and its cause's message are never used, because they can quote the
 * request, the resolved addresses or other foreign text.
 */
function describeProbeFailure(err: unknown): string {
  const cause = errorCause(err);
  if ([err, cause].some((e) => errorName(e) === 'TimeoutError' || errorName(e) === 'AbortError')) {
    return `no response within ${PROBE_REQUEST_TIMEOUT_MS / 1_000} s`;
  }
  // fetch reports every network failure as a TypeError and keeps the socket
  // error, such as ECONNREFUSED, in `cause`. When several addresses fail, the
  // cause can be an AggregateError whose own errors carry the code.
  const nested = typeof cause === 'object' && cause !== null && 'errors' in cause
    ? (cause as { errors: unknown }).errors
    : undefined;
  const candidates = [cause, ...(Array.isArray(nested) ? nested : []), err];
  for (const candidate of candidates) {
    const code = validatedErrorCode(candidate);
    if (code !== undefined) {
      return `${NETWORK_ERROR_LABELS.get(code) ?? 'network error'} (${code})`;
    }
  }
  return 'network error';
}

/** Tells whether a code point is a Unicode bidirectional control, which reorders the text shown. */
function isBidiControl(code: number): boolean {
  return (
    code === 0x061c ||
    code === 0x200e ||
    code === 0x200f ||
    (code >= 0x202a && code <= 0x202e) ||
    (code >= 0x2066 && code <= 0x2069)
  );
}

/**
 * Splits `text` into display lines with every terminal escape sequence and
 * control character removed.
 *
 * LF, CR, CR LF, U+2028 and U+2029 each end a line, so a carriage return
 * cannot overwrite a printed line. Tab is kept. ESC and CSI sequences are
 * dropped whole, and so are OSC, DCS, SOS, PM and APC strings up to their
 * terminator or the end of the line. Every other C0 or C1 control character,
 * DEL and the Unicode bidirectional controls are dropped.
 */
function splitDisplayLines(text: string): string[] {
  const lines: string[] = [];
  let line = '';
  let state: 'text' | 'escape' | 'csi' | 'string' | 'stringEscape' = 'text';
  let afterCarriageReturn = false;
  for (const ch of text) {
    const code = ch.codePointAt(0) ?? 0;
    // A line break ends the line in every state, so an unterminated sequence
    // cannot swallow the rest of the output.
    if (code === 0x0a || code === 0x0d || code === 0x2028 || code === 0x2029) {
      if (code !== 0x0a || !afterCarriageReturn) {
        lines.push(line);
        line = '';
      }
      afterCarriageReturn = code === 0x0d;
      state = 'text';
      continue;
    }
    afterCarriageReturn = false;

    if (state === 'stringEscape') {
      // ESC \ ends a string; ESC and any other character ends it and starts
      // a new escape sequence with that character.
      state = ch === '\\' ? 'text' : 'escape';
      if (state === 'text') {
        continue;
      }
    }
    if (state === 'string') {
      if (code === 0x1b) {
        state = 'stringEscape';
      } else if (code === 0x07 || code === 0x9c) {
        state = 'text';
      }
      continue;
    }
    if (state === 'escape') {
      if (ch === '[') {
        state = 'csi';
      } else if (ch === ']' || ch === 'P' || ch === 'X' || ch === '^' || ch === '_') {
        state = 'string';
      } else if (code !== 0x1b && (code < 0x20 || code > 0x2f)) {
        // A final character ends the sequence; intermediates keep it open.
        state = 'text';
      }
      continue;
    }
    if (state === 'csi') {
      if (code >= 0x20 && code <= 0x3f) {
        continue;
      }
      state = 'text';
      if (code >= 0x40 && code <= 0x7e) {
        continue;
      }
      // Any other character aborts the sequence and is read as text.
    }

    if (code === 0x1b) {
      state = 'escape';
    } else if (code === 0x9b) {
      state = 'csi';
    } else if (code === 0x90 || code === 0x98 || code === 0x9d || code === 0x9e || code === 0x9f) {
      state = 'string';
    } else if (
      code === 0x09 ||
      (code >= 0x20 && code !== 0x7f && (code < 0x80 || code > 0x9f) && !isBidiControl(code))
    ) {
      line += ch;
    }
  }
  lines.push(line);
  return lines;
}

/** Escapes `text` so a regular expression matches it literally. */
function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Pattern source for the acceptance project name and the names Compose
 * derives from it: containers `<project>-<service>-<n>`, images
 * `<project>-<service>`, volumes `<project>_<volume>` and networks such as
 * `<project>_default`. Compose writes them in lower case.
 */
const PROJECT_NAME_SOURCE = String.raw`${escapeRegExp(ACCEPTANCE_PROJECT)}(?:[-_][a-z0-9]+)*(?![A-Za-z0-9_-])`;

/**
 * The Compose names this setup itself determines: the project name and the
 * names derived from it ({@link PROJECT_NAME_SOURCE}), and the service names
 * db, api and web where they stand as words of their own, as in
 * `service "db"` or `[api build 3/9]`.
 */
const COMPOSE_IDENTIFIER_PATTERN = new RegExp(
  String.raw`(?<![A-Za-z0-9_.-])${PROJECT_NAME_SOURCE}|(?<=^|[\s"'[(])(?:db|api|web)(?=$|[\s"'\]):,])`,
  'g',
);

/**
 * Collects the exact values to redact from printed output: the values of the
 * credential-named variables ({@link SENSITIVE_VARIABLE_NAME}) in the
 * environment the Compose child receives, as {@link composeEnvironment}
 * builds it. A variable this process holds but the child never receives,
 * such as a dropped `SMARTHOTEL_*` first-start override, cannot appear in the
 * child's output and is not collected. Each value contributes every one of
 * its display lines, as {@link splitDisplayLines} prints them, trimmed; a
 * piece shorter than {@link MIN_REDACTED_VALUE_LENGTH} is left out. The
 * longest come first, so a value that contains another is replaced whole.
 */
function sensitiveEnvironmentValues(): string[] {
  const values = new Set<string>();
  for (const [name, value] of Object.entries(composeEnvironment())) {
    if (!SENSITIVE_VARIABLE_NAME.test(name)) {
      continue;
    }
    for (const piece of splitDisplayLines(value)) {
      const trimmed = piece.trim();
      if (trimmed.length >= MIN_REDACTED_VALUE_LENGTH) {
        values.add(trimmed);
      }
    }
  }
  return [...values].sort((a, b) => b.length - a.length);
}

/** Replaces every occurrence of each of `values` in `text`. */
function redactValues(text: string, values: string[]): string {
  let result = text;
  for (const value of values) {
    result = result.split(value).join(REDACTED);
  }
  return result;
}

/**
 * Replaces every occurrence of each of `values` in one display line, outside
 * the {@link COMPOSE_IDENTIFIER_PATTERN} matches: the line is split around
 * them and only the text between them is searched, so a value that equals
 * the project name, or part of a container, volume or network name, never
 * masks that name. A value anywhere else is still replaced.
 */
function redactValuesOutsideIdentifiers(line: string, values: string[]): string {
  if (values.length === 0) {
    return line;
  }
  let result = '';
  let segmentStart = 0;
  for (const match of line.matchAll(COMPOSE_IDENTIFIER_PATTERN)) {
    const start = match.index ?? segmentStart;
    result += `${redactValues(line.slice(segmentStart, start), values)}${match[0]}`;
    segmentStart = start + match[0].length;
  }
  return `${result}${redactValues(line.slice(segmentStart), values)}`;
}

/**
 * Removes the user information, query and fragment of one URL, keeping its
 * scheme, host, port and path.
 */
function redactUrl(url: string): string {
  const authorityStart = url.indexOf('://') + 3;
  let rest = url.slice(authorityStart);
  const authorityEnd = rest.search(/[/?#]/);
  const authority = authorityEnd === -1 ? rest : rest.slice(0, authorityEnd);
  const userInfoEnd = authority.lastIndexOf('@');
  if (userInfoEnd !== -1) {
    rest = `${REDACTED}${rest.slice(userInfoEnd)}`;
  }
  const queryStart = rest.search(/[?#]/);
  if (queryStart !== -1) {
    rest = `${rest.slice(0, queryStart + 1)}${REDACTED}`;
  }
  return `${url.slice(0, authorityStart)}${rest}`;
}

/**
 * Pattern source for a key that marks its value as a credential: it contains
 * `auth` (as in Authorization, Proxy-Authorization, X-Registry-Auth,
 * X-Auth-Token or WWW-Authenticate), `token`, `secret`, `passw`, `pwd`,
 * `credential`, `cookie` or `session`, or names an API, access, private or
 * signing key, with `-`, `_`, a space or nothing before `key`. One of the
 * project's own Compose names is never such a key, so the text after a volume
 * such as `smarthotel-acceptance_secrets:` stays readable.
 */
const CREDENTIAL_KEY_SOURCE = String.raw`(?!${PROJECT_NAME_SOURCE}["']?\s*[:=])(?:(?=[A-Za-z0-9_.-]*(?:auth|token|secret|passw|pwd|credential|cookie|session|(?:api|access|private|signing)[-_]?key))[A-Za-z0-9_.-]+|[A-Za-z0-9_.-]*(?:api|access|private|signing) key)`;

/**
 * Pattern source for the start of a `key: value` or `key=value` pair with a
 * credential key, which may be quoted (`"key": "value"`, `'key'='value'`).
 * It starts only where a token starts.
 */
const CREDENTIAL_LABEL_SOURCE = String.raw`(?<![A-Za-z0-9_.-])(["']?${CREDENTIAL_KEY_SOURCE}["']?\s*[:=]\s*)`;

/**
 * An `Authorization` header, or one whose name ends in it such as
 * `Proxy-Authorization`, with an unquoted value, which is redacted to the end
 * of the line.
 */
const AUTHORIZATION_HEADER = /(?<![A-Za-z0-9_.-])(["']?[A-Za-z0-9_.-]*authorization["']?\s*[:=]\s*)(?!["'\s])\S.*$/gi;

/**
 * A credential pair whose unquoted value is an authentication scheme followed
 * by a credential, such as `X-Auth: Bearer …` or `X-Registry-Auth: Token …`,
 * which is redacted to the end of the line.
 */
const SCHEME_CREDENTIAL = new RegExp(
  String.raw`${CREDENTIAL_LABEL_SOURCE}(?:basic|bearer|digest|negotiate|ntlm|kerberos|oauth|token|dpop|gnap|hoba|mutual|privatetoken|concealed|vapid|scram-sha-\d+|aws4-hmac-sha256)\s+\S.*$`,
  'gi',
);

/**
 * A credential pair and its value: a quoted value to its closing quote, or to
 * the end of the line when it has none, with backslash escapes; otherwise one
 * run of non-space characters. Groups: the label, the opening quote, the
 * quoted text and the closing quote.
 */
const CREDENTIAL_PAIR = new RegExp(
  String.raw`${CREDENTIAL_LABEL_SOURCE}(?:(["'])((?:(?!\2)[^\\]|\\.)*)(\2?)|\S+)`,
  'gi',
);

/**
 * A run of two or more `key=value` pairs separated by `;`, the shape of an
 * ADO.NET, Npgsql or ODBC connection string. A key after a `;` may hold
 * spaces; the first one may be `Data Source`, `User Id` or `Initial Catalog`
 * or a single word, so the words before a connection string stay out of it.
 * The first value is bounded and holds no `=` but trailing base64 padding,
 * so text with many `=` and no `;` is scanned in linear time.
 */
const KEY_VALUE_RUN = /(?<![A-Za-z0-9_.-])(?:data\s+source|user\s+id|initial\s+catalog|[A-Za-z][\w.-]{0,63})\s*=[^;=]{0,256}=*(?:;\s*[A-Za-z][\w\s.-]{0,63}=[^;]*)+;?/gi;

/**
 * The keys, lower-cased with single spaces, that make a {@link KEY_VALUE_RUN}
 * a connection string.
 */
const CONNECTION_STRING_KEYS = new Set([
  'host',
  'server',
  'data source',
  'address',
  'user id',
  'user',
  'username',
  'uid',
  'password',
  'pwd',
  'database',
  'initial catalog',
  'port',
]);

/** What a recognized connection string is replaced with, whole. */
const REDACTED_CONNECTION_STRING = '<redacted connection string>';

/**
 * A run of at least 24 characters of the base64 and base64url alphabets,
 * delimited by any other character, such as `/`, `.`, `:`, `@` or a space.
 */
const TOKEN_RUN = /(?<![A-Za-z0-9+=_-])[A-Za-z0-9+=_-]{24,}(?![A-Za-z0-9+=_-])/g;

/**
 * Replaces a {@link KEY_VALUE_RUN} whole when any of its keys is in
 * {@link CONNECTION_STRING_KEYS}, and leaves it unchanged otherwise.
 */
function redactConnectionString(run: string): string {
  const isConnectionString = run.split(';').some((pair) => {
    const key = pair.split('=', 1)[0].trim().replace(/\s+/g, ' ').toLowerCase();
    return CONNECTION_STRING_KEYS.has(key);
  });
  return isConnectionString ? REDACTED_CONNECTION_STRING : run;
}

/**
 * Replaces a {@link TOKEN_RUN} that mixes upper-case letters, lower-case
 * letters and digits, the shape of a generated key, token or encoded
 * credential. Lower-case hexadecimal, such as a container id or a `sha256`
 * digest, and ordinary words are kept.
 */
function redactTokenRun(run: string): string {
  return /[A-Z]/.test(run) && /[a-z]/.test(run) && /[0-9]/.test(run) ? REDACTED : run;
}

/**
 * Redacts the credential-shaped parts of one display line, in this order.
 * Every pattern starts only where a token starts, and a redacted value keeps
 * the key, scheme or URL part in front of it that shows what was removed:
 *
 * - an unquoted `Authorization` or `Proxy-Authorization` value, and an
 *   unquoted credential-key value that starts with an authentication scheme,
 *   to the end of the line;
 * - URL user information, query and fragment;
 * - connection strings, replaced whole;
 * - `Bearer` and `Basic` credentials;
 * - JWT-shaped strings;
 * - the value of every other `key: value` or `key=value` pair with a
 *   credential key, quoted or not ({@link CREDENTIAL_KEY_SOURCE});
 * - email addresses. An image reference such as `postgres:16@sha256:…` has no
 *   dotted domain after its `@` and is kept;
 * - runs of 24 or more base64 or base64url characters that mix upper-case
 *   letters, lower-case letters and digits.
 *
 * Every pattern is bounded to run in time linear in the line's length, so an
 * arbitrarily long line is filtered before it is cut. Text that matches none
 * of them is kept.
 */
function redactLine(line: string): string {
  return line
    .replace(AUTHORIZATION_HEADER, `$1${REDACTED}`)
    .replace(SCHEME_CREDENTIAL, `$1${REDACTED}`)
    .replace(/(?<![a-z0-9+.-])[a-z][a-z0-9+.-]*:\/\/\S*/gi, (url) => redactUrl(url))
    .replace(KEY_VALUE_RUN, (run) => redactConnectionString(run))
    .replace(/\b(bearer|basic)(\s+)[A-Za-z0-9._~+/-]+=*/gi, `$1$2${REDACTED}`)
    .replace(/\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*/g, REDACTED)
    .replace(
      CREDENTIAL_PAIR,
      (_pair: string, label: string, quote?: string, _text?: string, closing?: string) =>
        quote === undefined ? `${label}${REDACTED}` : `${label}${quote}${REDACTED}${closing ?? ''}`,
    )
    .replace(
      /(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b/g,
      REDACTED,
    )
    .replace(TOKEN_RUN, (run) => redactTokenRun(run));
}

/** Shortens a line to {@link COMPOSE_OUTPUT_MAX_LINE_LENGTH} characters, marking the cut. */
function truncateLine(line: string): string {
  const characters = Array.from(line);
  return characters.length > COMPOSE_OUTPUT_MAX_LINE_LENGTH
    ? `${characters.slice(0, COMPOSE_OUTPUT_MAX_LINE_LENGTH).join('')} [line truncated]`
    : line;
}

/**
 * Renders text another process supplied, such as the lock owner's checkout
 * path, as one printable line: control characters and escape sequences are
 * removed as {@link splitDisplayLines} removes them, its lines are joined with
 * a space, and the result is cut as {@link truncateLine} cuts it.
 */
function displayText(text: string): string {
  return truncateLine(splitDisplayLines(text).join(' '));
}

/**
 * Filters a Compose command's output for printing, line by line:
 * {@link splitDisplayLines} removes control characters and escape sequences;
 * {@link redactValuesOutsideIdentifiers} replaces the exact values
 * {@link sensitiveEnvironmentValues} collects from the child's environment,
 * everywhere but inside the project's own Compose names; {@link redactLine}
 * redacts credential-shaped text; and only the last
 * {@link COMPOSE_OUTPUT_MAX_LINES} lines are kept, each cut to
 * {@link COMPOSE_OUTPUT_MAX_LINE_LENGTH} characters after it is redacted.
 *
 * Every line is kept, redacted where a rule matches, because a failed command
 * is diagnosed from its output. The filter is pattern-based, so that output
 * can still hold sensitive text no rule recognizes.
 *
 * @returns The printable lines and the count of earlier lines left out.
 */
function filterComposeOutput(output: string): { lines: string[]; omitted: number } {
  const values = sensitiveEnvironmentValues();
  const all = splitDisplayLines(output);
  while (all.length > 0 && all[all.length - 1].trim() === '') {
    all.pop();
  }
  const kept = all.slice(-COMPOSE_OUTPUT_MAX_LINES);
  return {
    lines: kept.map((line) => truncateLine(redactLine(redactValuesOutsideIdentifiers(line, values)))),
    omitted: all.length - kept.length,
  };
}

/**
 * Describes a deadline for a message: a whole number of minutes as
 * `5 minutes`, any other length in seconds, as `2.5 s`.
 */
function describeDuration(ms: number): string {
  if (ms % 60_000 === 0) {
    const minutes = ms / 60_000;
    return `${minutes} minute${minutes === 1 ? '' : 's'}`;
  }
  return `${ms / 1_000} s`;
}

/**
 * Prints a failed Compose command: a header naming the operation, how it
 * failed (its exit code, or the deadline it outlived) and the project, then
 * the command's output as {@link filterComposeOutput} leaves it, every line
 * prefixed with {@link COMPOSE_OUTPUT_PREFIX}. Marker lines say when output
 * was dropped while it was collected ({@link ComposeResult.outputDropped})
 * and when earlier lines of what was kept are not printed.
 *
 * @param operation - The Compose subcommand and its arguments, as run.
 * @param result - The command's outcome.
 * @param deadlineMs - The deadline the command ran under, which the header
 *   names when the command outlived it.
 */
function printComposeFailure(operation: string, result: ComposeResult, deadlineMs: number): void {
  const { lines, omitted } = filterComposeOutput(result.output);
  const outcome = result.timedOut
    ? `for project ${ACCEPTANCE_PROJECT} timed out after ${describeDuration(deadlineMs)} and was stopped`
    : `exited with code ${result.code} for project ${ACCEPTANCE_PROJECT}`;
  const report = [
    `docker compose ${operation} ${outcome}; its output follows, filtered by pattern (recognized credentials, tokens, connection strings, URL queries and email addresses redacted; control characters removed):`,
  ];
  if (result.outputDropped) {
    report.push(
      `${COMPOSE_OUTPUT_PREFIX}[earlier output omitted: only its last ${COMPOSE_OUTPUT_MAX_CHARACTERS} characters were kept, from the first line that starts within them]`,
    );
  }
  if (omitted > 0) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}[${omitted} earlier line${omitted === 1 ? '' : 's'} omitted]`);
  }
  if (lines.length === 0 && !result.outputDropped) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}[no output]`);
  }
  for (const line of lines) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}${line}`);
  }
  console.error(report.join('\n'));
}

/**
 * Builds the error for a Compose command that could not be started. The
 * message is fixed text with at most the error's validated code, never the
 * system's own message, which can quote the arguments or the environment.
 * The validated code is kept as the error's `code`.
 */
function composeStartFailure(args: string[], err: unknown): Error {
  const code = validatedErrorCode(err);
  const message =
    code === 'ENOENT'
      ? 'The docker CLI was not found on PATH. The acceptance stack needs Docker Engine 28.0 or later with Docker Compose v2.1.1 or later.'
      : `Could not run "docker compose ${args.join(' ')}" for project ${ACCEPTANCE_PROJECT}${code === undefined ? '' : ` (${code})`}.`;
  return Object.assign(new Error(message), { code });
}

/** A line break as {@link splitDisplayLines} reads one: CR LF, LF, CR, U+2028 or U+2029. */
const LINE_BREAK = /\r\n|[\n\r\u2028\u2029]/;

/**
 * Keeps the end of a command's output within
 * {@link COMPOSE_OUTPUT_MAX_CHARACTERS}, from the first line that starts
 * within it. The line the cut falls in is dropped whole, so no fragment is
 * printed and no credential key is cut away from its value, which the
 * redaction patterns recognize only together. When no line break follows the
 * cut, that line is still open, and what the command writes next continues
 * it; {@link createOutputCollector} then drops that as well, up to the line's
 * end.
 *
 * @param text - Output longer than {@link COMPOSE_OUTPUT_MAX_CHARACTERS}.
 * @returns `tail`, the kept tail, empty when no line starts within it, and
 *   `insideLine`, set when the cut falls in a line that `text` does not end.
 */
function keepOutputTail(text: string): { tail: string; insideLine: boolean } {
  const start = text.length - COMPOSE_OUTPUT_MAX_CHARACTERS;
  const previous = text[start - 1];
  const startsLine =
    previous === '\n' ||
    previous === '\u2028' ||
    previous === '\u2029' ||
    // A cut between the CR and the LF of one line break starts no line.
    (previous === '\r' && text[start] !== '\n');
  const tail = text.slice(start);
  if (startsLine) {
    return { tail, insideLine: false };
  }
  const lineBreak = LINE_BREAK.exec(tail);
  return lineBreak === null
    ? { tail: '', insideLine: true }
    : { tail: tail.slice(lineBreak.index + lineBreak[0].length), insideLine: false };
}

/**
 * Collects a command's output, keeping at most its last
 * {@link COMPOSE_OUTPUT_MAX_CHARACTERS}. Chunks are appended as they arrive;
 * whenever twice that much has accumulated, the text is cut back by
 * {@link keepOutputTail}, so the memory held stays bounded however much the
 * command writes, and each character is copied a bounded number of times.
 *
 * A line the cut falls in is dropped whole, however long it grows: when the
 * cut leaves it open, every chunk that follows is discarded up to the next
 * line break as {@link splitDisplayLines} reads one (LF, CR, CR LF, U+2028 or
 * U+2029; the LF of a CR LF split across two chunks included), and none of it
 * is held, so the end of a line, such as a credential's value without the
 * label its redaction needs, is never kept without the line's start.
 * Collection resumes with the next line, and the drop stays reported.
 *
 * @returns `append`, the listener for decoded output chunks, and `read`,
 *   which returns the kept output, cut back to the cap, and whether any
 *   earlier output was dropped.
 */
function createOutputCollector(): {
  append: (chunk: string) => void;
  read: () => { output: string; outputDropped: boolean };
} {
  let chunks: string[] = [];
  let length = 0;
  let dropped = false;
  // What is discarded from the output that comes next: nothing, the rest of
  // a line whose start was dropped, or the LF of a CR LF whose CR ended the
  // dropped text.
  let discard: 'none' | 'line' | 'lineFeed' = 'none';
  const cutBack = () => {
    const text = chunks.join('');
    const { tail, insideLine } = keepOutputTail(text);
    chunks = [tail];
    length = tail.length;
    dropped = true;
    if (insideLine) {
      discard = 'line';
    } else if (tail === '' && text.endsWith('\r')) {
      discard = 'lineFeed';
    } else {
      discard = 'none';
    }
  };
  return {
    append: (chunk: string) => {
      let text = chunk;
      if (discard === 'lineFeed' && text !== '') {
        discard = 'none';
        if (text.startsWith('\n')) {
          text = text.slice(1);
        }
      }
      if (discard === 'line') {
        const lineBreak = LINE_BREAK.exec(text);
        if (lineBreak === null) {
          return;
        }
        text = text.slice(lineBreak.index + lineBreak[0].length);
        discard = lineBreak[0] === '\r' && text === '' ? 'lineFeed' : 'none';
      }
      if (text === '') {
        return;
      }
      chunks.push(text);
      length += text.length;
      if (length > 2 * COMPOSE_OUTPUT_MAX_CHARACTERS) {
        cutBack();
      }
    },
    read: () => {
      if (length > COMPOSE_OUTPUT_MAX_CHARACTERS) {
        cutBack();
      }
      return { output: chunks.join(''), outputDropped: dropped };
    },
  };
}

/** One entry of the host's process table, as {@link readProcessTable} reads it. */
interface ProcessEntry {
  /** The parent's process id. */
  ppid: number;
  /**
   * When the process started, as the table reports it: clock ticks since boot
   * on Linux, the `ps` start time elsewhere. With the process id it identifies
   * the process, so a process that reuses the id of one that ended is not
   * mistaken for it.
   */
  started: string;
  /** Whether the process has exited and only awaits reaping (a zombie). */
  exited: boolean;
}

/**
 * Reads the process table on Linux from `/proc/<pid>/stat`. The command name
 * in that line is in parentheses and can hold spaces and parentheses itself,
 * so fields are counted from the last `)`: the state is the first after it,
 * the parent's id the second and the start time the twentieth.
 *
 * @returns Every process that could be read, by process id.
 * @throws Error when `/proc` cannot be listed.
 */
function readProcTable(): Map<number, ProcessEntry> {
  const table = new Map<number, ProcessEntry>();
  for (const name of fs.readdirSync('/proc')) {
    if (!/^\d+$/.test(name)) {
      continue;
    }
    let stat: string;
    try {
      stat = fs.readFileSync(`/proc/${name}/stat`, 'utf8');
    } catch {
      // The process ended between the listing and the read.
      continue;
    }
    const fields = stat.slice(stat.lastIndexOf(')') + 1).trim().split(/\s+/);
    const ppid = Number(fields[1]);
    const started = fields[19];
    if (Number.isInteger(ppid) && started !== undefined) {
      table.set(Number(name), { ppid, started, exited: fields[0] === 'Z' || fields[0] === 'X' });
    }
  }
  return table;
}

/**
 * Reads the process table on POSIX systems other than Linux, such as macOS,
 * with `ps -A -o pid=,ppid=,stat=,lstart=`, within
 * {@link PROCESS_TOOL_TIMEOUT_MS}.
 *
 * @returns Every listed process by process id, or `undefined` when `ps`
 *   cannot be run, fails or lists nothing.
 */
function readPsTable(): Map<number, ProcessEntry> | undefined {
  const result = spawnSync('ps', ['-A', '-o', 'pid=,ppid=,stat=,lstart='], {
    encoding: 'utf8',
    maxBuffer: PROCESS_LIST_MAX_BYTES,
    stdio: ['ignore', 'pipe', 'ignore'],
    timeout: PROCESS_TOOL_TIMEOUT_MS,
  });
  if (result.error !== undefined || result.status !== 0 || typeof result.stdout !== 'string') {
    return undefined;
  }
  const table = new Map<number, ProcessEntry>();
  for (const line of result.stdout.split('\n')) {
    const match = /^\s*(\d+)\s+(\d+)\s+(\S+)\s+(\S.*?)\s*$/.exec(line);
    if (match !== null) {
      table.set(Number(match[1]), { ppid: Number(match[2]), started: match[4], exited: match[3].startsWith('Z') });
    }
  }
  return table.size > 0 ? table : undefined;
}

/**
 * Reads the host's process table: from `/proc` on Linux, with `ps` on other
 * POSIX systems.
 *
 * @returns The table by process id, or `undefined` when it cannot be read.
 *   It never throws.
 */
function readProcessTable(): Map<number, ProcessEntry> | undefined {
  try {
    return process.platform === 'linux' ? readProcTable() : readPsTable();
  } catch {
    return undefined;
  }
}

/**
 * Lists the descendants of `roots` in `table` by following parent ids: their
 * children, those children's children, and so on.
 *
 * @param table - The process table, as {@link readProcessTable} read it.
 * @param roots - The process ids to start from, which are not listed.
 * @returns The descendants' process ids, each once.
 */
function collectDescendants(table: Map<number, ProcessEntry>, roots: number[]): number[] {
  const children = new Map<number, number[]>();
  for (const [pid, entry] of table) {
    const siblings = children.get(entry.ppid);
    if (siblings === undefined) {
      children.set(entry.ppid, [pid]);
    } else {
      siblings.push(pid);
    }
  }
  const seen = new Set<number>(roots);
  const queue = [...roots];
  const descendants: number[] = [];
  for (let next = 0; next < queue.length; next += 1) {
    for (const pid of children.get(queue[next]) ?? []) {
      if (!seen.has(pid)) {
        seen.add(pid);
        descendants.push(pid);
        queue.push(pid);
      }
    }
  }
  return descendants;
}

/** A signal the process tree of a timed-out Compose command is sent: SIGTERM first, then SIGKILL. */
type StopSignal = 'SIGTERM' | 'SIGKILL';

/**
 * Prepares the stop of a timed-out Compose command's whole process tree on
 * POSIX: the `docker` CLI, the Compose plugin it runs as a child process, and
 * every process either of them started.
 *
 * Each call of the returned function reads the process table again and adds
 * to the tree the descendants of the CLI while it runs and of every member
 * still alive, so a process started during the stop is found while its
 * parent lives, and a member stays in the tree after its parent has died.
 * Members are recorded with their start time: a process holding a member's
 * id with another start time is a different process, is never signalled and
 * leaves that member counted as gone. The call then sends `signal` to the CLI
 * and to every live member not yet sent it or SIGKILL, so each process gets
 * each signal once. Before SIGKILL the tree is frozen with SIGSTOP and read
 * again, up to {@link PROCESS_TREE_FREEZE_MAX_PASSES} times, until every
 * member a read shows is frozen: a stopped process cannot start another, and
 * one started just before its parent stopped shows under that parent in the
 * next read, so no process the tree starts at the last moment escapes the
 * kill.
 *
 * Nothing else is signalled. The CLI is not detached, so it and its tree stay
 * in the process group a stopped `ng test` stops as a whole, and no group is
 * addressed here, only these process ids. When the table cannot be read,
 * only the CLI is signalled, through its handle.
 *
 * @param child - The `docker` CLI process of the command.
 * @returns The stop step: it signals as above and returns `true` once the
 *   tree is gone, that is the CLI has exited and no member is alive, an
 *   exited one awaiting reaping counting as gone; `false` while any of it
 *   runs or the table cannot be read to tell. It never throws.
 */
function createProcessTreeStop(child: ChildProcess): (signal: StopSignal) => boolean {
  const members = new Map<number, { started: string; frozen: boolean; sent?: StopSignal }>();
  let cliFrozen = false;
  let cliSent: StopSignal | undefined;
  const cliRunning = () => child.exitCode === null && child.signalCode === null;
  const needs = (sent: StopSignal | undefined, signal: StopSignal) =>
    sent === undefined || (sent === 'SIGTERM' && signal === 'SIGKILL');
  const isLive = (table: Map<number, ProcessEntry>, pid: number, started: string) => {
    const entry = table.get(pid);
    return entry !== undefined && entry.started === started && !entry.exited;
  };
  const signalCli = (signal: 'SIGSTOP' | StopSignal) => {
    try {
      child.kill(signal);
    } catch {
      // Deliberately ignored: SIGKILL after the grace, then the bounded
      // drain, still end the wait.
    }
  };
  const signalMember = (pid: number, signal: 'SIGSTOP' | StopSignal) => {
    try {
      process.kill(pid, signal);
    } catch {
      // Deliberately ignored: it ended since the read, or it cannot be
      // signalled; the bounded drain still ends the wait.
    }
  };
  // Reads the table and records the descendants of the CLI while it runs and
  // of every live member.
  const discover = (): Map<number, ProcessEntry> | undefined => {
    const table = readProcessTable();
    if (table !== undefined) {
      const roots = [...members].filter(([pid, member]) => isLive(table, pid, member.started)).map(([pid]) => pid);
      if (cliRunning() && child.pid !== undefined) {
        roots.push(child.pid);
      }
      for (const pid of collectDescendants(table, roots)) {
        const started = table.get(pid)?.started;
        if (started !== undefined && members.get(pid)?.started !== started) {
          members.set(pid, { started, frozen: false });
        }
      }
    }
    return table;
  };
  return (signal) => {
    try {
      let table = discover();
      const passes = signal === 'SIGKILL' ? PROCESS_TREE_FREEZE_MAX_PASSES : 0;
      for (let pass = 0; table !== undefined && pass < passes; pass += 1) {
        let froze = false;
        if (cliRunning() && !cliFrozen) {
          cliFrozen = true;
          froze = true;
          signalCli('SIGSTOP');
        }
        for (const [pid, member] of members) {
          if (!member.frozen && isLive(table, pid, member.started)) {
            member.frozen = true;
            froze = true;
            signalMember(pid, 'SIGSTOP');
          }
        }
        if (!froze) {
          break;
        }
        // A failed read keeps the last table, so every frozen member is
        // still killed below.
        table = discover() ?? table;
      }
      if (cliRunning() && needs(cliSent, signal)) {
        cliSent = signal;
        signalCli(signal);
      }
      let anyLive = false;
      if (table !== undefined) {
        for (const [pid, member] of members) {
          if (!isLive(table, pid, member.started)) {
            continue;
          }
          anyLive = true;
          if (needs(member.sent, signal)) {
            member.sent = signal;
            signalMember(pid, signal);
          }
        }
      }
      // Without a table the members' state is unknown; with none recorded
      // only the CLI is waited for, as only it was signalled.
      return !cliRunning() && !anyLive && (table !== undefined || members.size === 0);
    } catch {
      return false;
    }
  };
}

/**
 * Stops a timed-out Compose command's whole process tree on Windows, which
 * has neither POSIX signals nor process groups to address: runs
 * `taskkill /PID <pid> /T /F`, which ends the `docker` CLI and every process
 * it started, as forcefully as Node's own kill does there, within
 * {@link PROCESS_TOOL_TIMEOUT_MS}. When `taskkill` cannot be run or fails,
 * the CLI alone is ended through its handle. It never throws.
 *
 * @param child - The `docker` CLI process of the command.
 */
function stopWindowsProcessTree(child: ChildProcess): void {
  let ended = false;
  try {
    if (child.pid !== undefined) {
      const result = spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], {
        stdio: 'ignore',
        timeout: PROCESS_TOOL_TIMEOUT_MS,
        windowsHide: true,
      });
      ended = result.error === undefined && result.status === 0;
    }
  } catch {
    ended = false;
  }
  if (!ended) {
    try {
      child.kill('SIGKILL');
    } catch {
      // Deliberately ignored: the bounded drain still ends the wait.
    }
  }
}

/**
 * Runs one Compose command on the acceptance project, from the repository
 * root, with the neutralized Compose environment, within a deadline. Output
 * is collected, not streamed, so it is printed once, filtered by
 * {@link printComposeFailure}, and only when a command fails; at most its
 * last {@link COMPOSE_OUTPUT_MAX_CHARACTERS} are kept
 * ({@link createOutputCollector}).
 *
 * A command still running at its deadline is stopped, its whole process tree
 * and nothing else: the `docker` CLI, the Compose plugin it runs as a child
 * process and every process they started. On POSIX
 * ({@link createProcessTreeStop}) the tree is read from the process table at
 * the deadline, while the CLI still runs, and sent SIGTERM; what is left of
 * it after {@link COMPOSE_KILL_GRACE_MS}, processes started meanwhile
 * included, is frozen with SIGSTOP and sent SIGKILL. The table is read again
 * every {@link COMPOSE_STOP_POLL_INTERVAL_MS}, so a tree that is gone earlier
 * ends the grace early. The CLI is not detached, so the tree stays in the
 * process group a stopped `ng test` stops as a whole. On Windows the tree is
 * ended at the deadline with `taskkill /T /F` ({@link stopWindowsProcessTree}).
 *
 * The drain follows: {@link COMPOSE_DRAIN_TIMEOUT_MS} for the pipes to close
 * and, after a timeout on POSIX, for every process of the tree to be gone.
 * Pipes still held open then, by a process outside the tree or one that
 * survived SIGKILL, are closed from this side. The promise settles exactly
 * once: when the command has ended, its pipes have closed and, after a
 * timeout, its tree is gone; when the drain ends; or when `docker` cannot be
 * started. Every timer is cleared then. A caller holding the acceptance-run
 * lock therefore releases it only after the command's tree has been stopped
 * and drained, at most `deadlineMs` plus the grace and the drain after the
 * command started.
 *
 * @param root - The repository root.
 * @param args - The Compose subcommand and its arguments.
 * @param deadlineMs - How long the command may run before it is stopped.
 * @returns The exit code, whether the command timed out, the kept output and
 *   whether earlier output was dropped.
 * @throws Error when `docker` cannot be started at all. Its message is fixed
 *   text with at most a validated error code, such as `EACCES`, never the
 *   system's own message.
 */
function runCompose(root: string, args: string[], deadlineMs: number): Promise<ComposeResult> {
  return new Promise((resolve, reject) => {
    let settled = false;
    let deadlineTimer: ReturnType<typeof setTimeout> | undefined;
    let killTimer: ReturnType<typeof setTimeout> | undefined;
    let drainTimer: ReturnType<typeof setTimeout> | undefined;
    let pollTimer: ReturnType<typeof setInterval> | undefined;
    const settle = (finish: () => void) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(deadlineTimer);
      clearTimeout(killTimer);
      clearTimeout(drainTimer);
      clearInterval(pollTimer);
      finish();
    };
    try {
      const child = spawn('docker', composeArgs(root, ...args), {
        cwd: root,
        env: composeEnvironment(),
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const collector = createOutputCollector();
      let timedOut = false;
      let pipesClosed = false;
      // Set at the deadline on POSIX, where the command's tree is stopped
      // step by step; Windows ends it at once.
      let stopTree: ((signal: StopSignal) => boolean) | undefined;
      let stopSignal: StopSignal = 'SIGTERM';
      const finishWith = (code: number) => settle(() => resolve({ code, timedOut, ...collector.read() }));
      // Decoding on the stream keeps a multi-byte character split across two
      // chunks intact.
      child.stdout.setEncoding('utf8');
      child.stderr.setEncoding('utf8');
      child.stdout.on('data', collector.append);
      child.stderr.on('data', collector.append);

      const drain = () => {
        if (settled || drainTimer !== undefined) {
          return;
        }
        drainTimer = setTimeout(() => {
          child.stdout.destroy();
          child.stderr.destroy();
          // A process that survived SIGKILL must not keep this run alive.
          child.unref();
          finishWith(child.exitCode ?? 1);
        }, COMPOSE_DRAIN_TIMEOUT_MS);
      };
      // One step of stopping a timed-out command: signals what is left of its
      // tree, and settles once the tree is gone and the pipes have closed.
      const stopStep = () => {
        if (settled || stopTree === undefined || !stopTree(stopSignal)) {
          return;
        }
        // A tree gone within the grace needs no SIGKILL; its drain starts now.
        clearTimeout(killTimer);
        drain();
        if (pipesClosed) {
          finishWith(child.exitCode ?? 1);
        }
      };
      deadlineTimer = setTimeout(() => {
        timedOut = true;
        if (process.platform === 'win32') {
          stopWindowsProcessTree(child);
          drain();
          return;
        }
        stopTree = createProcessTreeStop(child);
        killTimer = setTimeout(() => {
          stopSignal = 'SIGKILL';
          drain();
          stopStep();
        }, COMPOSE_KILL_GRACE_MS);
        pollTimer = setInterval(stopStep, COMPOSE_STOP_POLL_INTERVAL_MS);
        stopStep();
      }, deadlineMs);

      child.on('error', (err: unknown) => {
        // Without a pid the command never started. A started one reports
        // only a signal it could not be sent, and the steps above still end
        // the wait.
        if (child.pid === undefined) {
          settle(() => reject(composeStartFailure(args, err)));
        }
      });
      child.on('exit', () => {
        // After a timeout the stop goes on until the whole tree is gone.
        if (!timedOut) {
          clearTimeout(deadlineTimer);
          drain();
        }
      });
      // The promise settles once, so a 'close' that follows a spawn 'error'
      // or the end of the drain changes nothing. After a timeout on POSIX,
      // closed pipes settle it only once the tree is gone as well.
      child.on('close', (code: number | null) => {
        pipesClosed = true;
        if (stopTree === undefined) {
          finishWith(code ?? 1);
        } else {
          stopStep();
        }
      });
    } catch (err) {
      // spawn throws synchronously on an invalid argument or environment
      // entry, with a message that can quote it.
      settle(() => reject(composeStartFailure(args, err)));
    }
  });
}

/**
 * The loopback addresses the port check connects to: `127.0.0.1`, where
 * Compose publishes the acceptance ports, and `::1`, which a `localhost` URL
 * can also reach.
 */
const LOOPBACK_ADDRESSES = ['127.0.0.1', '::1'];

/**
 * Tells whether one address accepts a TCP connection on `port`.
 *
 * @param host - The IP address to connect to.
 * @param port - The port to connect to.
 * @returns `true` when a connection succeeds; `false` when it is refused, fails
 *   otherwise (as an IPv6 address does on a host without IPv6) or does not
 *   complete within {@link PORT_CHECK_TIMEOUT_MS}.
 */
function acceptsConnection(host: string, port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = net.connect({ host, port });
    let settled = false;
    const settle = (inUse: boolean) => {
      if (settled) {
        return;
      }
      settled = true;
      socket.destroy();
      resolve(inUse);
    };
    socket.setTimeout(PORT_CHECK_TIMEOUT_MS);
    socket.on('connect', () => settle(true));
    socket.on('timeout', () => settle(false));
    // A persistent listener: a socket error with no listener would be thrown.
    socket.on('error', () => settle(false));
  });
}

/**
 * Tells whether something already accepts TCP connections on a loopback port.
 *
 * Compose publishes the acceptance ports on `127.0.0.1` only, but the fresh
 * target URLs name `localhost`, which Node's `fetch` and Chromium can resolve
 * to `::1` first. A process listening only on `::1` would then answer the
 * probes and specs in place of the acceptance stack, so the port counts as
 * taken when either `127.0.0.1` or `::1` accepts a connection. Both addresses
 * are tried at once. On a host without IPv6 the `::1` attempt fails, which
 * counts as free, so that host is checked on `127.0.0.1` alone.
 *
 * @param port - The host port to check.
 * @returns `true` when a connection to either loopback address succeeds;
 *   `false` when every attempt is refused, fails otherwise or does not
 *   complete within {@link PORT_CHECK_TIMEOUT_MS}.
 */
async function isPortInUse(port: number): Promise<boolean> {
  const accepted = await Promise.all(LOOPBACK_ADDRESSES.map((host) => acceptsConnection(host, port)));
  return accepted.includes(true);
}

/** The statuses fetch treats as redirects. */
const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);

/**
 * Requires `url` itself to answer `200`, and with `expectBody` as its trimmed
 * body when given, within {@link PROBE_ATTEMPTS} attempts. Redirects are not
 * followed: a `3xx` answer is a failed attempt, so only the named endpoint can
 * pass and no other destination is contacted.
 *
 * @param stack - The stack's name at the start of the failure message, for
 *   example `The reused stack (E2E_REUSE_STACK=1)`.
 * @param url - The absolute URL to request. It is built from a canonical
 *   {@link StackTarget}, so it holds only a scheme, host, port and path.
 * @param expectBody - The exact trimmed body required, if any.
 * @returns The body of the accepted `200` answer, as received.
 * @throws Error naming the stack, the URL and the last failure as a fixed
 *   category (an HTTP status, a redirect, a missing body, a timeout or a
 *   network error with its validated code) when every attempt fails.
 */
async function probe(stack: string, url: string, expectBody?: string): Promise<string> {
  let reason = '';
  for (let attempt = 1; attempt <= PROBE_ATTEMPTS; attempt += 1) {
    if (attempt > 1) {
      await delay(PROBE_INTERVAL_MS);
    }
    try {
      const response = await fetch(url, {
        redirect: 'manual',
        signal: AbortSignal.timeout(PROBE_REQUEST_TIMEOUT_MS),
      });
      // Reading the body on every path releases the connection.
      const body = await response.text();
      if (response.type === 'opaqueredirect' || response.status === 0) {
        reason = 'redirect, which the probe does not follow';
      } else if (REDIRECT_STATUSES.has(response.status)) {
        reason = `HTTP ${response.status} redirect, which the probe does not follow`;
      } else if (response.status !== 200) {
        reason = `HTTP ${response.status}`;
      } else if (expectBody !== undefined && body.trim() !== expectBody) {
        reason = `HTTP 200 without the expected "${expectBody}" body`;
      } else {
        return body;
      }
    } catch (err) {
      reason = describeProbeFailure(err);
    }
  }
  throw new Error(`${stack} is not reachable at ${url} (${reason}).`);
}

/** The only path `/config.json` may give the API base URL, as the SPA requires. */
const CONFIG_API_PATH = '/api';

/**
 * Reads the API base URL a `/config.json` body names, in the canonical form
 * the SPA's `parseRuntimeConfig` (`src/app/core/config/runtime-config.ts`)
 * derives before it calls the API: the body must be a JSON object whose
 * `apiBaseUrl` is a string that parses as an absolute URL with no user name,
 * password, query or fragment (a bare `?` or `#` counts), and whose path,
 * after one trailing `/` is trimmed, is exactly `/api`. The canonical form is
 * the parsed origin followed by `/api`, so it compares equal to a canonical
 * {@link StackTarget} `apiUrl` exactly when the SPA calls that API. The SPA's
 * transport rule is not repeated here: an `apiBaseUrl` it would refuse on
 * that ground alone still names one API, and its boot error fails the specs.
 *
 * @param body - The text of the `200` answer to `/config.json`.
 * @returns `{ apiUrl }` with the canonical `<origin>/api`, or `{ problem }`
 *   with a fixed-text reason the document names no usable API. A reason never
 *   holds any of the served text.
 */
function configuredApiUrl(body: string): { apiUrl: string } | { problem: string } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    // The parser's message can quote the body, so it is not passed on.
    return { problem: 'its body is not valid JSON' };
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { problem: 'its body is not a JSON object' };
  }
  const value = (parsed as Record<string, unknown>)['apiBaseUrl'];
  if (value === undefined) {
    return { problem: 'it has no apiBaseUrl' };
  }
  if (typeof value !== 'string') {
    return { problem: 'its apiBaseUrl is not a string' };
  }
  let url: URL;
  try {
    // No base URL, so a relative value such as `/api` is rejected.
    url = new URL(value);
  } catch {
    return { problem: 'its apiBaseUrl is not an absolute URL' };
  }
  if (url.username !== '' || url.password !== '') {
    return { problem: 'its apiBaseUrl has a user name or password' };
  }
  // As in the SPA: `search` and `hash` are empty for a bare `?` or `#` too, so
  // presence is read from the serialized URL, where the first `#` starts the
  // fragment and a `?` before it starts the query.
  const fragmentStart = url.href.indexOf('#');
  const beforeFragment = fragmentStart === -1 ? url.href : url.href.slice(0, fragmentStart);
  if (beforeFragment.includes('?')) {
    return { problem: 'its apiBaseUrl has a query' };
  }
  if (fragmentStart !== -1) {
    return { problem: 'its apiBaseUrl has a fragment' };
  }
  const path = url.pathname.endsWith('/') ? url.pathname.slice(0, -1) : url.pathname;
  if (path !== CONFIG_API_PATH) {
    return { problem: `its apiBaseUrl path is not ${CONFIG_API_PATH}` };
  }
  return { apiUrl: url.origin + CONFIG_API_PATH };
}

/**
 * Requires the web app at `target.baseUrl` to call the API at
 * `target.apiUrl`: its `/config.json`, fetched as {@link probe} fetches, must
 * name that API once {@link configuredApiUrl} canonicalizes it. Without this
 * check two separately healthy URLs could belong to different stacks, and the
 * browser specs would run against one database while the direct API requests
 * of the specs and the setup use another.
 *
 * @param stack - The stack's name at the start of the failure message.
 * @param target - The canonical target whose pair is checked.
 * @throws Error as {@link probe} throws it when `/config.json` does not
 *   answer `200`. Error naming the stack, the `/config.json` URL, the expected
 *   API and a fixed-text reason when the answer is not a valid configuration
 *   or names another API. The served value is never shown, because it is
 *   foreign text.
 */
async function requireConfiguredApi(stack: string, target: StackTarget): Promise<void> {
  const configUrl = `${target.baseUrl}/config.json`;
  const configured = configuredApiUrl(await probe(stack, configUrl));
  let problem: string | undefined;
  if ('problem' in configured) {
    problem = configured.problem;
  } else if (configured.apiUrl !== target.apiUrl) {
    problem = 'its apiBaseUrl names another API';
  }
  if (problem !== undefined) {
    throw new Error(
      `${stack}'s web app does not call the target API: ${configUrl} must name the apiBaseUrl ${target.apiUrl}, but ${problem}. The served value is not shown.`,
    );
  }
}

/**
 * Confirms the pair the specs target: the API readiness probe answers
 * `Healthy` and the web root answers `200`. In fresh mode it also requires
 * the web app's `/config.json` to name the target API
 * ({@link requireConfiguredApi}), so the browser and the specs' direct API
 * requests reach one stack. Reuse mode stops at the two probes, because its
 * results are never evidence. A failure names the reused stack in reuse mode
 * and the acceptance stack otherwise.
 *
 * @param target - The resolved target; `target.reuse` selects the mode.
 * @throws Error when a probe fails or, in fresh mode, `/config.json` does not
 *   name `target.apiUrl`; the message says which.
 */
async function probeTarget(target: StackTarget): Promise<void> {
  const stack = target.reuse ? 'The reused stack (E2E_REUSE_STACK=1)' : 'The acceptance stack';
  await probe(stack, `${target.apiUrl}/health`, 'Healthy');
  await probe(stack, `${target.baseUrl}/`);
  if (!target.reuse) {
    await requireConfiguredApi(stack, target);
  }
}

/**
 * Arguments made only of these characters are shown bare: none of them means
 * anything to POSIX shells, PowerShell or cmd. `\` is not among them, because
 * POSIX shells read it as an escape outside quotes.
 */
const BARE_ARGUMENT = /^[A-Za-z0-9_+=:./-]+$/;

/**
 * Characters that double quotes do not keep literal in all of POSIX shells,
 * PowerShell and cmd: the double quote and PowerShell's typographic double
 * quotes, which end the quoted text; `$`, the backtick, `%` and `!`, which
 * start an expansion or an escape in at least one of them; and control
 * characters.
 */
const UNQUOTABLE_CHARACTER = /["\u201C-\u201E$`%!\p{Cc}]/u;

/**
 * A backslash that ends the argument or precedes another backslash. Inside
 * double quotes POSIX shells fold `\\` into one, and Windows argument parsing
 * reads a backslash before the closing quote as escaping that quote.
 */
const UNQUOTABLE_BACKSLASH = /\\(\\|$)/;

/**
 * Formats `argv` as one command line that POSIX shells, PowerShell and cmd
 * all split back into exactly `argv`. An argument made only of
 * {@link BARE_ARGUMENT} characters is shown bare; any other is wrapped in
 * double quotes, which all three read alike for text holding no
 * {@link UNQUOTABLE_CHARACTER} and no {@link UNQUOTABLE_BACKSLASH}.
 *
 * @param argv - The program and its arguments.
 * @returns The command line, or `undefined` when an argument is empty (Windows
 *   PowerShell drops `""`) or cannot be quoted for all three.
 */
function portableCommandLine(argv: string[]): string | undefined {
  const words: string[] = [];
  for (const arg of argv) {
    if (BARE_ARGUMENT.test(arg)) {
      words.push(arg);
    } else if (arg !== '' && !UNQUOTABLE_CHARACTER.test(arg) && !UNQUOTABLE_BACKSLASH.test(arg)) {
      words.push(`"${arg}"`);
    } else {
      return undefined;
    }
  }
  return words.join(' ');
}

/**
 * Tells how to remove the acceptance project by hand once its automatic
 * removal has failed.
 *
 * The command shown is the one setup runs, built from {@link composeArgs} and
 * {@link DOWN_ARGS}: it names the project, the Compose file, the project
 * directory and the empty env file with absolute paths, so it works from any
 * directory and never reads a developer's `.env` or override file. A pasted
 * command cannot carry setup's neutralized environment, so the text names the
 * variables to unset; it never shows a variable's value. Running the tests
 * again in fresh mode is offered as well, and is the only route offered when
 * the repository path cannot be quoted for every shell.
 *
 * @param root - The repository root.
 * @returns One or more lines of instructions, without a trailing newline.
 */
function manualRemovalHint(root: string): string {
  const rerun =
    'running the tests again in fresh mode (E2E_REUSE_STACK unset), which starts with this same removal';
  const command = portableCommandLine(['docker', ...composeArgs(root, ...DOWN_ARGS)]);
  if (command === undefined) {
    return `Remove it by ${rerun}. No command is shown, because the repository path holds a character that POSIX shells, PowerShell and cmd do not all read the same way.`;
  }
  return [
    'Remove it from any directory, in a shell where COMPOSE_PROJECT_NAME, COMPOSE_FILE, COMPOSE_PROFILES, COMPOSE_ENV_FILES and every SMARTHOTEL_* variable are unset, with:',
    `  ${command}`,
    `or by ${rerun}.`,
  ].join('\n');
}

/**
 * Removes the acceptance project after a failed `up`, so the next run starts
 * clean. The removal runs under {@link COMPOSE_DOWN_TIMEOUT_MS}. Its own
 * failure is only reported, so the `up` failure stays the one the run fails
 * with. Whether the removal exits non-zero or outlives its deadline, after
 * its Compose output, or `docker` cannot be started, the report ends with the
 * same {@link manualRemovalHint}.
 *
 * @param root - The repository root.
 */
async function removeFailedStack(root: string): Promise<void> {
  try {
    const result = await runCompose(root, DOWN_ARGS, COMPOSE_DOWN_TIMEOUT_MS);
    if (result.timedOut || result.code !== 0) {
      printComposeFailure(DOWN_ARGS.join(' '), result, COMPOSE_DOWN_TIMEOUT_MS);
      const note = result.timedOut
        ? ` (it did not finish within ${describeDuration(COMPOSE_DOWN_TIMEOUT_MS)} and was stopped)`
        : '';
      console.error(`Removing the failed ${ACCEPTANCE_PROJECT} stack also failed${note}. ${manualRemovalHint(root)}`);
    }
  } catch (err) {
    // runCompose rejects with fixed text; only its validated code is repeated.
    const code = validatedErrorCode(err);
    console.error(
      `Removing the failed ${ACCEPTANCE_PROJECT} stack also failed (docker compose could not be started${code === undefined ? '' : `, ${code}`}). ${manualRemovalHint(root)}`,
    );
  }
}

/** The loopback address {@link ACCEPTANCE_LOCK_PORT} is listened on. */
const ACCEPTANCE_LOCK_HOST = '127.0.0.1';

/**
 * Upper bound for reading the reply of whatever holds a taken lock port. A
 * run holding the lock answers at once, so a slow or silent reply comes from
 * another program.
 */
const OWNER_REPLY_TIMEOUT_MS = 1_000;

/** Upper bound, in bytes, for the reply read from a taken lock port. */
const OWNER_REPLY_MAX_BYTES = 4_096;

/**
 * The record a fresh run sends, as one JSON line, to every client of the lock
 * port while it owns the acceptance project, so a run refused the lock can
 * name the owner.
 */
interface LockOwner {
  /** Always {@link ACCEPTANCE_PROJECT}, which tells this record from another program's reply. */
  lock: string;
  /** Process id of the Playwright runner that owns the stack. */
  pid: number;
  /** Repository root the owning run builds the stack from. */
  root: string;
  /** ISO 8601 instant at which the owner took the lock. */
  startedAt: string;
}

/**
 * Reads what the holder of the lock port sends a new client: at most
 * {@link OWNER_REPLY_MAX_BYTES} bytes, within {@link OWNER_REPLY_TIMEOUT_MS}.
 * A connection that is refused, fails, closes or times out ends the read with
 * whatever has arrived, so the call never hangs and never rejects.
 *
 * @returns The bytes received, decoded as UTF-8; empty when none arrived.
 */
function readLockReply(): Promise<string> {
  return new Promise((resolve) => {
    const socket = net.connect({ host: ACCEPTANCE_LOCK_HOST, port: ACCEPTANCE_LOCK_PORT });
    const chunks: Buffer[] = [];
    let received = 0;
    let settled = false;
    const settle = () => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      resolve(Buffer.concat(chunks).toString('utf8'));
    };
    const timer = setTimeout(settle, OWNER_REPLY_TIMEOUT_MS);
    socket.on('data', (chunk: Buffer) => {
      const kept = chunk.subarray(0, OWNER_REPLY_MAX_BYTES - received);
      chunks.push(kept);
      received += kept.length;
      if (received >= OWNER_REPLY_MAX_BYTES) {
        settle();
      }
    });
    socket.on('end', settle);
    socket.on('close', settle);
    // A persistent listener: a socket error with no listener would be thrown.
    socket.on('error', settle);
  });
}

/**
 * Parses the reply read from a taken lock port.
 *
 * @param reply - The text read by {@link readLockReply}.
 * @returns The owner record when the reply's first line is one, or
 *   `undefined` when it is anything else, as another program's reply is.
 */
function parseLockOwner(reply: string): LockOwner | undefined {
  let value: unknown;
  try {
    value = JSON.parse(reply.split('\n', 1)[0]);
  } catch {
    return undefined;
  }
  if (typeof value !== 'object' || value === null) {
    return undefined;
  }
  const { lock, pid, root, startedAt } = value as Record<string, unknown>;
  if (
    lock !== ACCEPTANCE_PROJECT ||
    typeof pid !== 'number' ||
    !Number.isInteger(pid) ||
    typeof root !== 'string' ||
    typeof startedAt !== 'string'
  ) {
    return undefined;
  }
  return { lock, pid, root, startedAt };
}

/**
 * Makes this run the single owner of the acceptance project on this host by
 * listening on `127.0.0.1` port {@link ACCEPTANCE_LOCK_PORT}. The operating
 * system lets only one socket listen there and frees the port when this
 * process ends, however it ends, a kill included, so no lock is ever left
 * behind and none is ever taken over. Every client that connects is sent this
 * run's {@link LockOwner} record as one JSON line, so a run refused the lock
 * can name its owner.
 *
 * The listener is unreferenced, so it never keeps the process alive. Node
 * opens it close-on-exec, so the Compose child processes do not inherit it
 * and cannot hold the port after this run ends.
 *
 * @param root - The repository root, recorded so a refused run can name the
 *   checkout that owns the stack.
 * @returns The release function. It closes the listener without waiting for
 *   connected clients, which it disconnects, runs once however often it is
 *   called, and never throws: release runs while a run ends, where the run's
 *   own result or setup error must be the one reported.
 * @throws Error naming the owner's pid, start time and checkout when another
 *   fresh run holds the port; Error naming the port when another program
 *   holds it; Error naming the address and the failure when the port cannot
 *   be listened on for any other reason. No Compose call has run in any of
 *   these cases.
 */
async function acquireAcceptanceLock(root: string): Promise<() => void> {
  const owner: LockOwner = {
    lock: ACCEPTANCE_PROJECT,
    pid: process.pid,
    root,
    startedAt: new Date().toISOString(),
  };
  const record = `${JSON.stringify(owner)}\n`;
  const lockAddress = `${ACCEPTANCE_LOCK_HOST}:${ACCEPTANCE_LOCK_PORT}`;
  const clients = new Set<net.Socket>();
  const server = net.createServer((socket) => {
    clients.add(socket);
    socket.on('close', () => clients.delete(socket));
    // A persistent listener: a client that resets the connection must not
    // fail the run that owns the stack.
    socket.on('error', () => socket.destroy());
    socket.end(record);
  });
  // Errors after 'listening', such as a failed accept, leave the port held.
  // The message names only the address and the failure's validated code.
  const reportServerError = (err: unknown) => {
    console.error(`The acceptance-run lock on ${lockAddress} reported an error${errorCodeNote(err)} and stays held.`);
  };

  try {
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject);
      server.once('listening', () => {
        server.removeListener('error', reject);
        server.on('error', reportServerError);
        resolve();
      });
      server.listen({ host: ACCEPTANCE_LOCK_HOST, port: ACCEPTANCE_LOCK_PORT });
    });
  } catch (err) {
    if (errorCode(err) !== 'EADDRINUSE') {
      throw new Error(
        `Could not listen on ${lockAddress}, which a fresh acceptance run holds while it owns the ${ACCEPTANCE_PROJECT} stack${errorCodeNote(err)}.`,
      );
    }
    const holder = parseLockOwner(await readLockReply());
    if (holder !== undefined) {
      throw new Error(
        `Another fresh acceptance run owns the ${ACCEPTANCE_PROJECT} stack (pid ${holder.pid}, started ${displayText(holder.startedAt)}, from ${displayText(holder.root)}). Wait for it to finish and run the tests again.`,
      );
    }
    throw new Error(
      `Port ${ACCEPTANCE_LOCK_PORT} on ${ACCEPTANCE_LOCK_HOST}, which a fresh acceptance run holds while it owns the ${ACCEPTANCE_PROJECT} stack, is in use by another process. Free it and run the tests again.`,
    );
  }
  server.unref();

  let held = true;
  return () => {
    if (!held) {
      return;
    }
    held = false;
    try {
      server.close();
      for (const socket of clients) {
        socket.destroy();
      }
      clients.clear();
    } catch (err) {
      console.error(
        `Could not close the acceptance-run lock on ${lockAddress}${errorCodeNote(err)}; it is freed when this run ends.`,
      );
    }
  };
}

/**
 * Prepares the stack the end-to-end specs run against.
 *
 * Fresh mode, the default, is the only mode whose results count as evidence
 * for success criteria 1, 2, 6 and 7. It removes the dedicated
 * `smarthotel-acceptance` Compose project with its volumes, fails when another
 * process holds host port 18080 or 14200, then builds current images and
 * starts db, api and web from fresh volumes with generated secrets, waiting
 * until all three are healthy. It then requires the web app's `/config.json`
 * to name `http://localhost:18080/api` as its API, so the browser specs and
 * the direct API requests reach the same stack and database. Every Compose
 * call names the project, the repository's `docker-compose.yml` and the empty
 * `deploy/acceptance.env` explicitly and runs with the Compose and
 * `SMARTHOTEL_*` variables neutralized, so a developer's stack, `.env` and
 * overrides are never touched or read. It never switches to other ports. The
 * stack is left running after the run; remove it with
 * `docker compose -p smarthotel-acceptance down -v`.
 *
 * One fresh run at a time owns the acceptance project on a host. Before its
 * first Compose call a fresh run listens on `127.0.0.1` port
 * {@link ACCEPTANCE_LOCK_PORT} (18079) and holds it until the suite's global
 * teardown; the operating system frees the port when the run ends, however it
 * ends. A second fresh run started meanwhile fails before any Compose call,
 * with "Another fresh acceptance run owns the smarthotel-acceptance stack
 * (pid …, started …, from …)", so it never removes the stack the first run is
 * testing; when another program holds the port, it fails with a message
 * naming that port. A setup failure releases the lock before its error
 * propagates.
 *
 * Every Compose command runs under a deadline: 5 minutes for each `down`
 * ({@link COMPOSE_DOWN_TIMEOUT_MS}) and 30 minutes for the `up`
 * ({@link COMPOSE_UP_TIMEOUT_MS}), which covers a first build on a clean
 * machine. A command that outlives it is stopped with its whole process
 * tree, SIGTERM and then SIGKILL, and drained before setup fails
 * ({@link runCompose}), so the lock is released only after that tree has
 * stopped. A timed-out `up` is followed by the same `down -v` as a failed
 * one.
 *
 * Reuse mode (`E2E_REUSE_STACK=1`) is for local iteration. It takes no lock,
 * makes no Compose call and only probes the stack at `E2E_BASE_URL` and
 * `E2E_API_URL` (defaults `http://localhost:4200` and
 * `http://localhost:8080/api`), as {@link resolveStackTarget} validated and
 * canonicalized them, following no redirect; it does not read `/config.json`.
 * It then prints the canonical pair.
 *
 * @returns In fresh mode, the global teardown Playwright runs after the
 *   suite: it releases the lock and leaves the stack running. In reuse mode,
 *   nothing.
 * @throws Error when another fresh run owns the acceptance stack, another
 *   program holds the lock port or it cannot be listened on, Docker or
 *   Compose is unavailable, fails or outlives its deadline, an acceptance
 *   port is taken, the target does not answer, or in fresh mode the web
 *   app's `/config.json` does not name the acceptance API; the message says
 *   which. A failed or timed-out Compose command's filtered output is printed
 *   before the throw.
 */
export default async function globalSetup(): Promise<(() => Promise<void>) | void> {
  const target = resolveStackTarget();

  if (isReuseMode()) {
    await probeTarget(target);
    console.log(
      `Reusing the running stack at ${target.baseUrl} (API ${target.apiUrl}); results are not acceptance evidence.`,
    );
    return;
  }

  const root = findRepositoryRoot();
  const releaseLock = await acquireAcceptanceLock(root);
  try {
    await buildAcceptanceStack(root, target);
  } catch (err) {
    releaseLock();
    throw err;
  }
  return async () => {
    releaseLock();
  };
}

/**
 * The fresh path of {@link globalSetup}, run while this run holds the lock:
 * removes the acceptance project, checks the acceptance ports, starts the
 * stack, probes `target` and requires the web app's `/config.json` to name
 * `target.apiUrl`.
 *
 * @param root - The repository root.
 * @param target - The fresh target, the stack this run builds.
 * @throws Error when a Compose command fails or outlives its deadline, an
 *   acceptance port is taken, the target does not answer or the web app's
 *   `/config.json` does not name the acceptance API.
 */
async function buildAcceptanceStack(root: string, target: StackTarget): Promise<void> {
  console.log(
    `Building the ${ACCEPTANCE_PROJECT} stack (web :${ACCEPTANCE_WEB_PORT}, API :${ACCEPTANCE_API_PORT})…`,
  );

  const down = await runCompose(root, DOWN_ARGS, COMPOSE_DOWN_TIMEOUT_MS);
  if (down.timedOut) {
    printComposeFailure(DOWN_ARGS.join(' '), down, COMPOSE_DOWN_TIMEOUT_MS);
    throw new Error(
      `docker compose down did not finish within ${describeDuration(COMPOSE_DOWN_TIMEOUT_MS)} for project ${ACCEPTANCE_PROJECT} and was stopped; is the Docker daemon responding?`,
    );
  }
  if (down.code !== 0) {
    printComposeFailure(DOWN_ARGS.join(' '), down, COMPOSE_DOWN_TIMEOUT_MS);
    throw new Error(
      `docker compose down failed for project ${ACCEPTANCE_PROJECT}; is the Docker daemon running?`,
    );
  }

  // Checked only after the down, which has released the project's own
  // published ports, so a port that still answers belongs to someone else.
  const busyPorts: number[] = [];
  for (const port of [ACCEPTANCE_API_PORT, ACCEPTANCE_WEB_PORT]) {
    if (await isPortInUse(port)) {
      busyPorts.push(port);
    }
  }
  if (busyPorts.length > 0) {
    const subject =
      busyPorts.length === 1 ? `Port ${busyPorts[0]} is` : `Ports ${busyPorts.join(' and ')} are`;
    const pronoun = busyPorts.length === 1 ? 'it' : 'them';
    throw new Error(
      `${subject} already in use by another process. Free ${pronoun} and run the tests again; the acceptance stack never switches ports.`,
    );
  }

  console.log('Building images and starting db, api and web; a first build can take several minutes.');
  const up = await runCompose(root, UP_ARGS, COMPOSE_UP_TIMEOUT_MS);
  if (up.timedOut || up.code !== 0) {
    printComposeFailure(UP_ARGS.join(' '), up, COMPOSE_UP_TIMEOUT_MS);
    await removeFailedStack(root);
    throw new Error(
      up.timedOut
        ? `docker compose up --wait did not finish within ${describeDuration(COMPOSE_UP_TIMEOUT_MS)} for project ${ACCEPTANCE_PROJECT} and was stopped; see the Compose output above.`
        : `docker compose up --wait failed for project ${ACCEPTANCE_PROJECT}; see the Compose output above.`,
    );
  }

  await probeTarget(target);
  console.log(`Acceptance stack ready at ${target.baseUrl} (API ${target.apiUrl}).`);
}
