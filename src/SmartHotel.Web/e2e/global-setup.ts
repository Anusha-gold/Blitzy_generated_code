/**
 * Playwright global setup for the web end-to-end suite, named as
 * `globalSetup` by `playwright.config.ts`. A fresh run, the default, pins
 * this machine's local Docker daemon, builds the acceptance stack and holds
 * this host's acceptance-run lock while it owns it; a barrier file keeps
 * later fresh runs out after a Compose command it could not see finish.
 * Reuse mode (`E2E_REUSE_STACK=1`) only probes a running stack. The target
 * is the one `./support/stack-target` resolves for the configuration too.
 * Its own messages hold fixed text and validated values only, never a raw
 * `E2E_*` setting or a caught error's text, and of a failed Compose
 * command's output it prints only the status lines it recognizes.
 * Playwright transpiles this file as CommonJS and never type-checks it, so
 * it uses Node's built-in modules and globals only.
 */
import { type ChildProcess, execFile, spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';

import {
  ACCEPTANCE_API_PORT,
  ACCEPTANCE_LOCK_PORT,
  ACCEPTANCE_PROJECT,
  ACCEPTANCE_WEB_PORT,
  DOCKER_ENDPOINT_QUERY_ARGS,
  type StackTarget,
  composeArgs,
  composeEnvironment,
  findRepositoryRoot,
  isReuseMode,
  localDockerEndpoint,
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
  /** Whether the command outlived its deadline, so setup tried to stop it. */
  timedOut: boolean;
  /**
   * Whether the command's end was confirmed: the `docker` CLI exited and its
   * output pipes closed and, after a timeout, on POSIX the stop step
   * ({@link createProcessTreeStop}) found its whole process tree gone, or on
   * Windows `taskkill /T /F` exited 0. It is `false` when the drain deadline
   * ({@link COMPOSE_DRAIN_TIMEOUT_MS}) ended the wait first, a command that
   * exited on its own but whose pipes stayed open until then included, and
   * when `taskkill` failed, so only the CLI was ended. Some of the command's
   * processes may then still be running.
   */
  ended: boolean;
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
 * this side, and the command's end counts as unconfirmed
 * ({@link ComposeResult.ended}).
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

/**
 * The most lines of a failed command's output that are printed: the last of
 * the lines {@link filterComposeOutput} recognizes.
 */
const COMPOSE_OUTPUT_MAX_LINES = 200;

/** The most characters of one output line that are printed. */
const COMPOSE_OUTPUT_MAX_LINE_LENGTH = 500;

/**
 * Prefix of every printed output line, so text from the child process cannot
 * pass for a message of this setup.
 */
const COMPOSE_OUTPUT_PREFIX = '  compose | ';

/**
 * What replaces the details of a Compose resource event that are neither a
 * duration nor one of Compose's own dependency-failure details
 * ({@link COMPOSE_EVENT_DETAILS}), such as the daemon's message after `Error`.
 */
const DETAILS_WITHHELD = '[details withheld]';

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

/** Pattern source for the stack's service names: db, api and web. */
const SERVICE_NAME_SOURCE = 'db|api|web';

/**
 * Pattern source for the acceptance project name and the names Compose
 * derives from it: containers `<project>-<service>-<n>`, the images it builds
 * `<project>-<service>`, and volumes and networks `<project>_<name>`, such as
 * `<project>_db-data` or `<project>_default`, whose name is a short
 * lower-case word. Compose writes them in lower case.
 */
const PROJECT_NAME_SOURCE = String.raw`${escapeRegExp(ACCEPTANCE_PROJECT)}(?:-(?:${SERVICE_NAME_SOURCE})(?:-\d{1,3})?|_[a-z][a-z0-9-]{0,15})?`;

/**
 * Wraps a pattern source so it matches the name bare or, as older Compose v2
 * releases write volume names, in double quotes.
 */
function bareOrQuoted(source: string): string {
  return String.raw`(?:"(?:${source})"|(?:${source}))`;
}

/** Pattern source for one of the project's own names ({@link PROJECT_NAME_SOURCE}), bare or quoted. */
const OWN_NAME_SOURCE = bareOrQuoted(PROJECT_NAME_SOURCE);

/**
 * Pattern source for the names of the images the stack builds,
 * `<project>-api` and `<project>-web`, bare or quoted. The base images it
 * pulls, such as `postgres`, are not the project's own names, so their
 * events are withheld.
 */
const OWN_IMAGE_SOURCE = bareOrQuoted(String.raw`${escapeRegExp(ACCEPTANCE_PROJECT)}-(?:api|web)`);

/**
 * Pattern source for a duration as Compose and BuildKit print one, such as
 * `0.5s` or `123.4s`.
 */
const DURATION_SOURCE = String.raw`\d{1,5}(?:\.\d{1,3})?s`;

/**
 * Pattern source for Compose's report that one of the project's containers
 * ended its start-up: `container <name> exited (<code>)` or
 * `container <name> is unhealthy`.
 */
const CONTAINER_END_SOURCE = String.raw`container ${OWN_NAME_SOURCE} (?:exited \(\d{1,3}\)|is unhealthy)`;

/**
 * Pattern source for Compose's report that a dependency of a service did not
 * start: `dependency failed to start: ` and a {@link CONTAINER_END_SOURCE}.
 */
const DEPENDENCY_FAILURE_SOURCE = `dependency failed to start: ${CONTAINER_END_SOURCE}`;

/**
 * The one-word statuses a Compose resource event reports, as Compose writes
 * them, as in `Container <project>-db-1 Healthy`.
 */
const COMPOSE_EVENT_STATUSES = [
  'Creating',
  'Created',
  'Recreate',
  'Recreating',
  'Recreated',
  'Starting',
  'Started',
  'Waiting',
  'Healthy',
  'Unhealthy',
  'Running',
  'Restarting',
  'Restarted',
  'Stopping',
  'Stopped',
  'Killing',
  'Killed',
  'Removing',
  'Removed',
  'Building',
  'Built',
  'Pulling',
  'Pulled',
  'Skipped',
  'Exited',
  'Error',
  'Warning',
  'Interrupted',
  'Canceled',
  'Done',
];

/**
 * The start of a Compose resource event, `<Kind> <name> <Status>`, as Compose
 * writes it: `Container`, `Network`, `Volume` or `Service` with one of the
 * project's own names ({@link OWN_NAME_SOURCE}), or `Image` with the name of
 * an image the stack builds ({@link OWN_IMAGE_SOURCE}), then one of
 * {@link COMPOSE_EVENT_STATUSES} as a whole word. The event's details, if
 * any, follow the match.
 */
const COMPOSE_EVENT = new RegExp(
  String.raw`^(?:(?:Container|Network|Volume|Service) ${OWN_NAME_SOURCE}|Image ${OWN_IMAGE_SOURCE}) (?:${COMPOSE_EVENT_STATUSES.join('|')})(?= |$)`,
);

/**
 * The details of a Compose resource event that are printed, from the space
 * after its status: one duration ({@link DURATION_SOURCE}), as in
 * `Container <project>-db-1 Healthy 0.5s`; `dependency <service> failed to
 * start`; or a {@link DEPENDENCY_FAILURE_SOURCE} report. Nothing else may
 * follow.
 */
const COMPOSE_EVENT_DETAILS = new RegExp(
  String.raw`^ (?:${DURATION_SOURCE}|dependency (?:${SERVICE_NAME_SOURCE}) failed to start|${DEPENDENCY_FAILURE_SOURCE})$`,
);

/**
 * Compose's own messages about the project's containers and services, each a
 * whole line, that are printed as they stand:
 *
 * - `Container <name> not started: condition "<condition>" of service
 *   "<service>" not met` or `… not reached`, the condition `service_healthy`,
 *   `service_started` or `service_completed_successfully`, optionally
 *   followed by `: interrupted by the failure of Container <name>`;
 * - a {@link DEPENDENCY_FAILURE_SOURCE} report, or the
 *   {@link CONTAINER_END_SOURCE} report on its own;
 * - `service "<service>" didn't complete successfully: exit <code>`.
 *
 * Every name is one of the project's own ({@link OWN_NAME_SOURCE}), every
 * service db, api or web, and every exit code at most 3 digits.
 */
const COMPOSE_MESSAGE = new RegExp(
  `^(?:${[
    `Container ${OWN_NAME_SOURCE} not started: condition "service_(?:healthy|started|completed_successfully)" of service "(?:${SERVICE_NAME_SOURCE})" not (?:met|reached)(?:: interrupted by the failure of Container ${OWN_NAME_SOURCE})?`,
    DEPENDENCY_FAILURE_SOURCE,
    CONTAINER_END_SOURCE,
    String.raw`service "(?:${SERVICE_NAME_SOURCE})" didn't complete successfully: exit \d{1,3}`,
  ].join('|')})$`,
);

/**
 * Recognizes a Compose resource event of the project's own resources
 * ({@link COMPOSE_EVENT}) and gives its printable form: the line as it stands
 * when it ends at its status or its details are {@link COMPOSE_EVENT_DETAILS},
 * and otherwise `<Kind> <name> <Status>` followed by
 * {@link DETAILS_WITHHELD}.
 *
 * @param line - One display line, trimmed, its whitespace runs collapsed.
 * @returns The printable form of the event, or `undefined` when the line is
 *   no event this setup recognizes.
 */
function recognizeResourceEvent(line: string): string | undefined {
  const event = COMPOSE_EVENT.exec(line);
  if (event === null) {
    return undefined;
  }
  const details = line.slice(event[0].length);
  return details === '' || COMPOSE_EVENT_DETAILS.test(details) ? line : `${event[0]} ${DETAILS_WITHHELD}`;
}

/** The Dockerfile instructions a BuildKit step header can start with, in upper case. */
const DOCKERFILE_INSTRUCTIONS = new Set([
  'FROM',
  'RUN',
  'COPY',
  'ADD',
  'WORKDIR',
  'ENV',
  'ARG',
  'USER',
  'EXPOSE',
  'ENTRYPOINT',
  'CMD',
  'HEALTHCHECK',
  'LABEL',
  'VOLUME',
  'SHELL',
  'STOPSIGNAL',
  'ONBUILD',
]);

/**
 * Pattern source for the build stages a BuildKit step header names: the
 * stages the repository's Dockerfiles name, `build` (api and web) and
 * `runtime` (api), and `stage-<n>`, BuildKit's name for an unnamed stage,
 * such as web's final nginx stage, `stage-1`.
 */
const BUILD_STAGE_SOURCE = String.raw`build|runtime|stage-\d{1,2}`;

/**
 * A BuildKit step header, `#<n> [<step>] <description>`, with its whitespace
 * runs collapsed, whose step is one of the repository's own builds:
 * `internal`, a service (`api`), a service's internal step (`api internal`),
 * or a stage's step `<i>/<n>`, after the service and the stage
 * ({@link BUILD_STAGE_SOURCE}) when the build names them (`api build 6/10`,
 * `web stage-1 1/6`, `web 1/3`, `2/2`). Groups: the step number, the step and
 * the first word of the description.
 */
const BUILDKIT_STEP_HEADER = new RegExp(
  String.raw`^#(\d{1,6}) \[(internal|(?:${SERVICE_NAME_SOURCE})(?: internal)?|(?:(?:${SERVICE_NAME_SOURCE}) )?(?:(?:${BUILD_STAGE_SOURCE}) )?\d{1,4}/\d{1,4})\](?: (\S+))?`,
);

/**
 * BuildKit's markers for the end of a step, each a whole line:
 * `#<n> DONE <duration>` ({@link DURATION_SOURCE}), `#<n> CACHED` and
 * `#<n> CANCELED`.
 */
const BUILDKIT_STEP_END = new RegExp(String.raw`^#\d{1,6} (?:DONE ${DURATION_SOURCE}|CACHED|CANCELED)$`);

/** BuildKit's marker for a failed step, `#<n> ERROR…`. Group: the step number. */
const BUILDKIT_STEP_ERROR = /^#(\d{1,6}) ERROR(?![\w-])/;

/**
 * Recognizes a BuildKit marker line: a step header
 * ({@link BUILDKIT_STEP_HEADER}), printed as `#<n> [<step>] <INSTRUCTION>`
 * with the instruction's arguments dropped, or as `#<n> [<step>]` when its
 * description starts with no Dockerfile instruction; a step end
 * ({@link BUILDKIT_STEP_END}), printed as it stands; or a failed step,
 * printed as `#<n> ERROR` with its message dropped. A header whose step is
 * not one of the repository's own builds, and tool output, which BuildKit
 * writes as `#<n> <seconds> <text>`, are none of these.
 *
 * @param line - One display line, trimmed, its whitespace runs collapsed.
 * @returns The printable form of the marker, or `undefined` when the line is
 *   no marker this setup recognizes.
 */
function recognizeBuildKitMarker(line: string): string | undefined {
  if (BUILDKIT_STEP_END.test(line)) {
    return line;
  }
  const error = BUILDKIT_STEP_ERROR.exec(line);
  if (error !== null) {
    return `#${error[1]} ERROR`;
  }
  const header = BUILDKIT_STEP_HEADER.exec(line);
  if (header === null) {
    return undefined;
  }
  const instruction = (header[3] ?? '').toUpperCase();
  return DOCKERFILE_INSTRUCTIONS.has(instruction)
    ? `#${header[1]} [${header[2]}] ${instruction}`
    : `#${header[1]} [${header[2]}]`;
}

/** Shortens a line to {@link COMPOSE_OUTPUT_MAX_LINE_LENGTH} characters, marking the cut. */
function truncateLine(line: string): string {
  const characters = Array.from(line);
  return characters.length > COMPOSE_OUTPUT_MAX_LINE_LENGTH
    ? `${characters.slice(0, COMPOSE_OUTPUT_MAX_LINE_LENGTH).join('')} [line truncated]`
    : line;
}

/**
 * Selects, by allowlist, what of a Compose command's output is printed. Each
 * display line ({@link splitDisplayLines}, which removes control characters
 * and escape sequences) is trimmed and its whitespace runs collapsed, and a
 * line is kept only when it is one of this setup's canonical messages:
 *
 * - one of Compose's own messages about the project's containers and
 *   services ({@link COMPOSE_MESSAGE}), such as
 *   `dependency failed to start: container <project>-api-1 exited (1)`, as
 *   it stands;
 * - a resource event of the project's own containers, networks, volumes and
 *   built images ({@link recognizeResourceEvent}), its details withheld
 *   unless they are a duration or one of Compose's own dependency-failure
 *   details;
 * - a BuildKit marker of the repository's own builds
 *   ({@link recognizeBuildKitMarker}), the arguments of its instruction and
 *   the text of its error dropped.
 *
 * Names in them are only the project's own and numbers only exit codes,
 * container indexes, step numbers and counters, unnamed-stage numbers and
 * durations. Every other non-blank line, base-image pulls, tool output,
 * Dockerfile excerpts and the daemon's, a registry's and BuildKit's own
 * messages included, is withheld, because it can carry credentials that no
 * pattern recognizes, and only counted. Of the kept lines, the last
 * {@link COMPOSE_OUTPUT_MAX_LINES} are returned, each cut by
 * {@link truncateLine}.
 *
 * @param output - The kept output of one Compose command.
 * @returns The printable lines, the count of earlier kept lines left out and
 *   the count of lines withheld.
 */
function filterComposeOutput(output: string): { lines: string[]; omitted: number; withheld: number } {
  const recognized: string[] = [];
  let withheld = 0;
  for (const displayLine of splitDisplayLines(output)) {
    const line = displayLine.trim().replace(/\s+/g, ' ');
    if (line === '') {
      continue;
    }
    const printable = COMPOSE_MESSAGE.test(line)
      ? line
      : recognizeResourceEvent(line) ?? recognizeBuildKitMarker(line);
    if (printable === undefined) {
      withheld += 1;
    } else {
      recognized.push(printable);
    }
  }
  const kept = recognized.slice(-COMPOSE_OUTPUT_MAX_LINES);
  return {
    lines: kept.map((line) => truncateLine(line)),
    omitted: recognized.length - kept.length,
    withheld,
  };
}

/**
 * The likely causes of a failed Compose command this setup recognizes, in
 * the order they are reported, each with the phrases, matched without regard
 * to case, that Docker, Compose or BuildKit report it with. Only a cause's
 * own fixed text is ever printed, never the output that matched it.
 */
const COMPOSE_FAILURE_CAUSES: { cause: string; phrases: string[] }[] = [
  {
    cause: 'the Docker daemon is not reachable, or this user may not use it',
    phrases: [
      'failed to connect to the docker api',
      'cannot connect to the docker daemon',
      'error during connect',
      'permission denied while trying to connect to the docker',
      'is the docker daemon running',
    ],
  },
  {
    cause: 'the docker CLI has no Compose v2 plugin, or one older than v2.1.1, which up --wait needs',
    phrases: ['unknown command: docker compose', "'compose' is not a docker command", 'unknown flag: --wait'],
  },
  {
    cause: `host port ${ACCEPTANCE_API_PORT} or ${ACCEPTANCE_WEB_PORT}, which the stack publishes, is already in use`,
    phrases: [
      'port is already allocated',
      'address already in use',
      'ports are not available',
      'only one usage of each socket address',
    ],
  },
  {
    cause: 'a container name of the stack is already taken by another container',
    phrases: ['is already in use by container'],
  },
  {
    cause: 'a base image could not be pulled',
    phrases: [
      'pull access denied',
      'manifest unknown',
      'failed to resolve reference',
      'toomanyrequests',
      'failed to authorize',
    ],
  },
  { cause: 'an image build step failed', phrases: ['failed to solve', 'did not complete successfully'] },
  { cause: 'a container did not become healthy', phrases: ['is unhealthy'] },
  { cause: 'a container exited during start-up', phrases: ['exited (', "didn't complete successfully"] },
  { cause: 'the disk Docker stores its data on is full', phrases: ['no space left on device'] },
  {
    cause: 'a network or volume of the project is still in use',
    phrases: ['has active endpoints', 'volume is in use'],
  },
  {
    cause: 'a request to the Docker daemon or a registry timed out',
    phrases: ['i/o timeout', 'tls handshake timeout', 'context deadline exceeded'],
  },
  {
    cause: 'the Compose file, or a variable it interpolates, is invalid',
    phrases: ['invalid interpolation format', 'yaml:'],
  },
];

/**
 * Finds the likely causes of a failed Compose command in its kept output,
 * withheld lines included: each {@link COMPOSE_FAILURE_CAUSES} entry one of
 * whose phrases occurs in it, at most once each, in that list's order.
 *
 * @param output - The kept output of the command.
 * @returns The fixed text of each cause found, and none of the output.
 */
function findComposeFailureCauses(output: string): string[] {
  const text = output.toLowerCase();
  return COMPOSE_FAILURE_CAUSES.filter(({ phrases }) => phrases.some((phrase) => text.includes(phrase))).map(
    ({ cause }) => cause,
  );
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
 * failed (its exit code, or the deadline it outlived), whether its end was
 * unconfirmed ({@link ComposeResult.ended}) and the project, and
 * saying that only the status lines this setup recognizes are printed; then
 * those lines as {@link filterComposeOutput} selects them, every one prefixed
 * with {@link COMPOSE_OUTPUT_PREFIX}; then the likely causes
 * {@link findComposeFailureCauses} finds, as fixed text, and a hint that the
 * same command run by hand shows its full output. Marker lines say when
 * output was dropped while it was collected
 * ({@link ComposeResult.outputDropped}), when earlier recognized lines are
 * not printed and how many other lines were withheld. No other text of the
 * output is printed.
 *
 * @param operation - The Compose subcommand and its arguments, as run.
 * @param result - The command's outcome.
 * @param deadlineMs - The deadline the command ran under, which the header
 *   names when the command outlived it.
 */
function printComposeFailure(operation: string, result: ComposeResult, deadlineMs: number): void {
  const { lines, omitted, withheld } = filterComposeOutput(result.output);
  let outcome: string;
  if (result.timedOut) {
    outcome = `for project ${ACCEPTANCE_PROJECT} timed out after ${describeDuration(deadlineMs)} and ${
      result.ended ? 'was stopped' : 'setup tried to stop it, but could not confirm that it ended'
    }`;
  } else {
    outcome = `exited with code ${result.code} for project ${ACCEPTANCE_PROJECT}${
      result.ended ? '' : `, but its output pipes were still open ${describeDuration(COMPOSE_DRAIN_TIMEOUT_MS)} later`
    }`;
  }
  const report = [
    `docker compose ${operation} ${outcome}. Only the Compose status lines this setup recognizes are printed; other Docker, registry and build output is withheld, because it can carry credentials:`,
  ];
  if (result.outputDropped) {
    report.push(
      `${COMPOSE_OUTPUT_PREFIX}[earlier output omitted: only its last ${COMPOSE_OUTPUT_MAX_CHARACTERS} characters were kept, from the first line that starts within them]`,
    );
  }
  if (omitted > 0) {
    report.push(
      `${COMPOSE_OUTPUT_PREFIX}[${omitted} earlier recognized line${omitted === 1 ? '' : 's'} omitted]`,
    );
  }
  if (lines.length === 0 && withheld === 0 && !result.outputDropped) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}[no output]`);
  }
  for (const line of lines) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}${line}`);
  }
  if (withheld > 0) {
    report.push(`${COMPOSE_OUTPUT_PREFIX}[${withheld} other line${withheld === 1 ? '' : 's'} withheld]`);
  }
  const causes = findComposeFailureCauses(result.output);
  if (causes.length === 0) {
    report.push('Likely cause: none this setup recognizes in the Compose output.');
  }
  for (const cause of causes) {
    report.push(`Likely cause: ${cause}.`);
  }
  report.push(
    `Running the same docker compose ${operation} command by hand for project ${ACCEPTANCE_PROJECT} shows its full output.`,
  );
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
 * within it. The line the cut falls in is dropped whole, so no fragment of a
 * line is kept, whose end could otherwise be read, and printed, as a whole
 * line {@link filterComposeOutput} recognizes. When no line break follows the
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
 * is held, so the end of a line, which could pass for a whole line, is never
 * kept without the line's start.
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
 * @returns `true` when `taskkill` ran and exited 0; `false` when it could not
 *   be run or failed, so at most the CLI was ended and the rest of its tree
 *   may still run.
 */
function stopWindowsProcessTree(child: ChildProcess): boolean {
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
  return ended;
}

/**
 * Upper bound for the `docker context inspect` call that reports which Docker
 * daemon the CLI selects: 30 seconds. The call reads the CLI's configuration
 * only and contacts no daemon, so only a stalled CLI reaches it. At the bound
 * the CLI is sent SIGKILL; the query fails then, or at the latest
 * {@link DOCKER_ENDPOINT_QUERY_SETTLE_GRACE_MS} later.
 */
const DOCKER_ENDPOINT_QUERY_TIMEOUT_MS = 30_000;

/**
 * How long after {@link DOCKER_ENDPOINT_QUERY_TIMEOUT_MS} the query waits for
 * the killed CLI to be reported as ended: 5 seconds. Node reports a child's
 * end only once it has exited and its output pipes have closed, which a CLI
 * that survives SIGKILL, or a process it started that still holds the pipes,
 * can postpone indefinitely. When the grace ends first, the query kills the
 * CLI again, closes the pipes from this side and fails without waiting for
 * it, so the run, and the acceptance lock it holds, wait at most the bound
 * plus this grace.
 */
const DOCKER_ENDPOINT_QUERY_SETTLE_GRACE_MS = 5_000;

/**
 * Upper bound, in bytes, for each output stream of that call: 64 KiB, far
 * more than the one endpoint line it prints.
 */
const DOCKER_ENDPOINT_QUERY_MAX_BYTES = 64 * 1_024;

/** What the run tells the user to check when the docker CLI cannot report its daemon. */
const DOCKER_ENDPOINT_QUERY_HINT =
  'Check DOCKER_HOST, DOCKER_CONTEXT and `docker context ls`, then run the tests again.';

/**
 * Asks the docker CLI which Docker daemon it selects and returns that
 * endpoint once {@link localDockerEndpoint} has accepted it as a daemon on
 * this machine, a local unix socket or a local Windows named pipe. Every
 * Compose call of the run then names it with `--host` ({@link composeArgs}).
 *
 * The call is `docker` with {@link DOCKER_ENDPOINT_QUERY_ARGS}, run without a
 * shell, in the neutralized Compose environment ({@link composeEnvironment}),
 * within {@link DOCKER_ENDPOINT_QUERY_TIMEOUT_MS} and with each output stream
 * capped at {@link DOCKER_ENDPOINT_QUERY_MAX_BYTES}. Its stderr, its stdout
 * when it fails and the system's own error message are never printed or
 * attached, because they can hold paths, user names and hosts.
 *
 * At the bound, or past the cap, `execFile` sends the CLI SIGKILL, which it
 * cannot ignore. `execFile` reports the CLI's end only once it has exited and
 * its output pipes have closed, so the promise does not rely on that report
 * alone: a timer of its own, the bound plus
 * {@link DOCKER_ENDPOINT_QUERY_SETTLE_GRACE_MS}, kills the CLI again (SIGKILL;
 * on Windows `child.kill()`, which terminates it), closes its output pipes
 * from this side, stops it keeping Node's event loop alive and fails with the
 * same timeout text. The promise settles exactly once, on whichever comes
 * first, and the timer is cleared when the report comes first.
 *
 * @returns The local endpoint, such as `unix:///var/run/docker.sock`.
 * @throws Error with fixed text when `docker` is not on PATH or cannot be
 *   started (with at most a validated error code), does not finish within
 *   the bound, prints more than the cap, ends on a signal or exits non-zero
 *   (naming only the exit code); or the error of {@link localDockerEndpoint}
 *   when the endpoint it reports is not local.
 */
function resolveDockerEndpoint(): Promise<string> {
  return new Promise((resolve, reject) => {
    let settled = false;
    let settleTimer: ReturnType<typeof setTimeout> | undefined;
    // Settles once, on execFile's report or on settleTimer, whichever runs
    // first, and clears the timer.
    const settle = (outcome: () => void) => {
      if (settled) {
        return;
      }
      settled = true;
      if (settleTimer !== undefined) {
        clearTimeout(settleTimer);
        settleTimer = undefined;
      }
      outcome();
    };
    const fail = (message: string, code?: string) =>
      settle(() => reject(Object.assign(new Error(message), { code })));
    const query = 'docker context inspect';
    const timedOut = () =>
      fail(
        `The docker CLI did not report which Docker daemon it selects within ${DOCKER_ENDPOINT_QUERY_TIMEOUT_MS / 1_000} s (${query}) and was stopped. ${DOCKER_ENDPOINT_QUERY_HINT}`,
      );
    // The system's message can quote the arguments or the environment, so
    // only the validated code is kept.
    const startFailure = (err: unknown) => {
      const validated = validatedErrorCode(err);
      fail(
        `Could not run "${query}" to find which Docker daemon the docker CLI selects${validated === undefined ? '' : ` (${validated})`}.`,
        validated,
      );
    };
    let child: ChildProcess;
    try {
      child = execFile(
        'docker',
        DOCKER_ENDPOINT_QUERY_ARGS,
        {
          env: composeEnvironment(),
          timeout: DOCKER_ENDPOINT_QUERY_TIMEOUT_MS,
          killSignal: 'SIGKILL',
          maxBuffer: DOCKER_ENDPOINT_QUERY_MAX_BYTES,
          encoding: 'utf8',
          windowsHide: true,
        },
        (err: unknown, stdout: string) => {
          if (err === null) {
            let endpoint: string;
            try {
              endpoint = localDockerEndpoint(stdout);
            } catch (refused) {
              settle(() => reject(refused));
              return;
            }
            settle(() => resolve(endpoint));
            return;
          }
          const code = errorCode(err);
          const signal = typeof err === 'object' && err !== null && 'signal' in err
            ? (err as { signal: unknown }).signal
            : undefined;
          const killed = typeof err === 'object' && err !== null && 'killed' in err
            && (err as { killed: unknown }).killed === true;
          if (code === 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER') {
            fail(
              `The docker CLI printed more than ${DOCKER_ENDPOINT_QUERY_MAX_BYTES / 1_024} KiB when asked which Docker daemon it selects (${query}), which is not one endpoint. ${DOCKER_ENDPOINT_QUERY_HINT}`,
            );
          } else if (code === 'ENOENT') {
            fail(
              'The docker CLI was not found on PATH. The acceptance stack needs Docker Engine 28.0 or later with Docker Compose v2.1.1 or later.',
              code,
            );
          } else if (typeof code === 'string') {
            startFailure(err);
          } else if (killed) {
            timedOut();
          } else if (typeof code === 'number' && Number.isInteger(code)) {
            fail(
              `The docker CLI could not report which Docker daemon it selects (${query} exited with code ${code}). ${DOCKER_ENDPOINT_QUERY_HINT}`,
            );
          } else if (typeof signal === 'string' && /^SIG[A-Z0-9]{1,16}$/.test(signal)) {
            fail(
              `The docker CLI could not report which Docker daemon it selects (${query} ended on ${signal}). ${DOCKER_ENDPOINT_QUERY_HINT}`,
            );
          } else {
            fail(`The docker CLI could not report which Docker daemon it selects (${query} failed). ${DOCKER_ENDPOINT_QUERY_HINT}`);
          }
        },
      );
    } catch (err) {
      // execFile throws synchronously on an invalid argument or environment
      // entry.
      startFailure(err);
      return;
    }
    // execFile reports only once the CLI has exited and its pipes have
    // closed. This timer settles the query without waiting for either.
    settleTimer = setTimeout(() => {
      settleTimer = undefined;
      if (settled) {
        return;
      }
      try {
        if (process.platform === 'win32') {
          child.kill();
        } else {
          child.kill('SIGKILL');
        }
      } catch {
        // Deliberately ignored: the query fails below whether or not the
        // CLI could be signalled.
      }
      child.stdout?.destroy();
      child.stderr?.destroy();
      child.unref();
      timedOut();
    }, DOCKER_ENDPOINT_QUERY_TIMEOUT_MS + DOCKER_ENDPOINT_QUERY_SETTLE_GRACE_MS);
  });
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
 * once, at most `deadlineMs` plus the grace and the drain after the command
 * started: when the command has ended, its pipes have closed and, after a
 * timeout, its tree is gone; when the drain ends; or when `docker` cannot be
 * started. Every timer is cleared then. {@link ComposeResult.ended} tells the
 * first case from the drain ending the wait, or a failed `taskkill`, so a
 * caller that must not overlap the command, by cleaning up, deleting the
 * barrier file or releasing the acceptance-run lock, checks it first
 * ({@link commandCompleted}).
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint {@link resolveDockerEndpoint}
 *   returned, passed as `--host` so the command acts on that daemon only.
 * @param args - The Compose subcommand and its arguments.
 * @param deadlineMs - How long the command may run before it is stopped.
 * @returns The exit code, whether the command timed out, whether its end was
 *   confirmed, the kept output and whether earlier output was dropped.
 * @throws Error when `docker` cannot be started at all. Its message is fixed
 *   text with at most a validated error code, such as `EACCES`, never the
 *   system's own message.
 */
function runCompose(root: string, endpoint: string, args: string[], deadlineMs: number): Promise<ComposeResult> {
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
      const child = spawn('docker', composeArgs(endpoint, root, ...args), {
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
      // Set at the deadline on Windows: whether `taskkill /T /F` succeeded.
      let windowsTreeStopped: boolean | undefined;
      const finishWith = (code: number, ended: boolean) =>
        settle(() => resolve({ code, timedOut, ended, ...collector.read() }));
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
          // The pipes, or after a timeout the tree, outlived the drain, so
          // the command's end is not confirmed.
          finishWith(child.exitCode ?? 1, false);
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
          finishWith(child.exitCode ?? 1, true);
        }
      };
      deadlineTimer = setTimeout(() => {
        timedOut = true;
        if (process.platform === 'win32') {
          windowsTreeStopped = stopWindowsProcessTree(child);
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
      // closed pipes settle it only once the tree is gone as well; after one
      // on Windows, the end is confirmed only if `taskkill` succeeded.
      child.on('close', (code: number | null) => {
        pipesClosed = true;
        if (stopTree === undefined) {
          finishWith(code ?? 1, !timedOut || windowsTreeStopped === true);
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
 * {@link DOWN_ARGS}: it names the same Docker daemon this run used with
 * `--host`, so `DOCKER_HOST`, `DOCKER_CONTEXT` or the current context cannot
 * send it to another daemon, and the project, the Compose file, the project
 * directory and the empty env file with absolute paths, so it works from any
 * directory and never reads a developer's `.env` or override file. A pasted
 * command cannot carry setup's neutralized environment, so the text names the
 * variables to unset; it never shows a variable's value. Running the tests
 * again in fresh mode is offered as well, and is the only route offered when
 * the repository path cannot be quoted for every shell. A rerun is not
 * pinned: it checks the daemon again and removes the stack only from the
 * daemon the docker CLI selects then, so the text names this run's endpoint,
 * already validated as local by {@link composeArgs}, as the one the CLI must
 * still select.
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint this run's Compose calls used.
 * @returns One or more lines of instructions, without a trailing newline.
 */
function manualRemovalHint(root: string, endpoint: string): string {
  const command = portableCommandLine(['docker', ...composeArgs(endpoint, root, ...DOWN_ARGS)]);
  const rerun =
    `running the tests again in fresh mode (E2E_REUSE_STACK unset) while the docker CLI still selects this run's Docker daemon, ${endpoint}: ` +
    'a fresh run checks the daemon again and removes the stack only from the daemon the docker CLI selects then';
  if (command === undefined) {
    return `Remove it by ${rerun}. No command is shown, because the repository path holds a character that POSIX shells, PowerShell and cmd do not all read the same way.`;
  }
  return [
    'Remove it from any directory, in a shell where COMPOSE_PROJECT_NAME, COMPOSE_FILE, COMPOSE_PROFILES, COMPOSE_ENV_FILES and every SMARTHOTEL_* variable are unset, with this command, which names this run\'s Docker daemon with --host:',
    `  ${command}`,
    `or by ${rerun}.`,
  ].join('\n');
}

/** The directory, in the user's home directory, that holds the barrier file ({@link barrierPath}). */
const BARRIER_DIRECTORY = '.smarthotel';

/**
 * A character the barrier path may not hold, because messages print it: a
 * control character, a Unicode line or paragraph separator, or a
 * bidirectional control, any of which could break or reorder the text shown.
 */
const UNPRINTABLE_PATH_CHARACTER = /[\p{Cc}\u2028\u2029\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069]/u;

/**
 * The error a barrier file operation fails with ({@link barrierPath},
 * {@link barrierExists}, {@link setBarrier}, {@link clearBarrier}). Its
 * message is fixed text with at most the barrier path and a validated error
 * code.
 */
class BarrierFileError extends Error {
  /** @param message - The fixed-text message. */
  constructor(message: string) {
    super(message);
    this.name = 'BarrierFileError';
  }
}

/** The barrier path once {@link barrierPath} has resolved it. */
let resolvedBarrierPath: string | undefined;

/**
 * Returns the path of the acceptance-run barrier file,
 * `<home>/.smarthotel/<project>.unsettled` in the home directory of the user
 * running the tests, so every checkout that user runs on this host shares it.
 * Setup writes the file before each Compose command that changes the
 * acceptance project and deletes it once that command has completed
 * ({@link runProjectCommand}); while it exists, every fresh run of that user
 * refuses to start ({@link buildAcceptanceStack}). The path is resolved once
 * per run, so the file written, the file deleted and the file a message names
 * are always the same, and a call after the first never throws.
 *
 * @returns The absolute path.
 * @throws BarrierFileError when the home directory cannot be determined, is
 *   empty or relative, or holds an {@link UNPRINTABLE_PATH_CHARACTER}.
 */
function barrierPath(): string {
  if (resolvedBarrierPath !== undefined) {
    return resolvedBarrierPath;
  }
  const name = path.join(BARRIER_DIRECTORY, `${ACCEPTANCE_PROJECT}.unsettled`);
  let home: string;
  try {
    home = os.homedir();
  } catch (err) {
    throw new BarrierFileError(
      `Could not determine this user's home directory${errorCodeNote(err)}, which holds the acceptance-run barrier file ${name}, so setup stops before its next Compose call.`,
    );
  }
  if (home === '' || !path.isAbsolute(home) || UNPRINTABLE_PATH_CHARACTER.test(home)) {
    throw new BarrierFileError(
      `This user's home directory, which holds the acceptance-run barrier file ${name}, is empty, not an absolute path or holds a control character, so setup stops before its next Compose call.`,
    );
  }
  resolvedBarrierPath = path.join(home, name);
  return resolvedBarrierPath;
}

/**
 * Tells whether the barrier file ({@link barrierPath}) exists. Anything at
 * the path counts, a directory or a dangling symbolic link included, because
 * only its absence shows that no earlier run left a Compose command on the
 * project unfinished.
 *
 * @returns `true` when anything exists at the path; `false` when nothing does.
 * @throws BarrierFileError naming the path and the failure's validated code
 *   when that cannot be told, so the run fails closed.
 */
function barrierExists(): boolean {
  const file = barrierPath();
  try {
    fs.lstatSync(file);
    return true;
  } catch (err) {
    if (errorCode(err) === 'ENOENT') {
      return false;
    }
    throw new BarrierFileError(
      `Could not check whether the acceptance-run barrier file ${file} exists${errorCodeNote(err)}, so this run stops before any Compose call.`,
    );
  }
}

/**
 * Writes the barrier file ({@link barrierPath}) before a Compose command that
 * changes the acceptance project. Its directory is created when missing, for
 * its owner only (mode `0700`); the file is created empty, with mode `0600`,
 * and never over an existing entry, so a symbolic link placed there is never
 * followed. The file holds nothing: its existence is the whole record.
 *
 * @throws BarrierFileError naming the path and the failure's validated code
 *   when the directory or the file cannot be created, or something already
 *   exists at the path. The command then does not run.
 */
function setBarrier(): void {
  const file = barrierPath();
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
    fs.writeFileSync(file, '', { flag: 'wx', mode: 0o600 });
  } catch (err) {
    throw new BarrierFileError(
      `Could not write the acceptance-run barrier file ${file}${errorCodeNote(err)}, which setup writes before each docker compose command that changes project ${ACCEPTANCE_PROJECT}, so that command does not run.`,
    );
  }
}

/**
 * Deletes the barrier file ({@link barrierPath}) once the Compose command it
 * guards has completed ({@link commandCompleted}) or could not be started. A
 * file already gone counts as deleted.
 *
 * @throws BarrierFileError naming the path and the failure's validated code
 *   when the file cannot be deleted, so the run fails instead of leaving a
 *   stale barrier silently.
 */
function clearBarrier(): void {
  const file = barrierPath();
  try {
    fs.unlinkSync(file);
  } catch (err) {
    if (errorCode(err) !== 'ENOENT') {
      throw new BarrierFileError(
        `Could not delete the acceptance-run barrier file ${file}${errorCodeNote(err)} after the docker compose command it guards had completed or could not be started. While it exists, every fresh run by this user on this host refuses to start; delete it and run the tests again.`,
      );
    }
  }
}

/**
 * Tells whether a Compose command completed: it exited on its own before its
 * deadline and its output pipes closed ({@link ComposeResult.ended}), so
 * Compose itself waited for the daemon calls it made. Only then may setup
 * delete the barrier file and run another Compose command on the project.
 *
 * @param result - The command's outcome.
 * @returns `true` when the command completed, whatever its exit code.
 */
function commandCompleted(result: ComposeResult): boolean {
  return !result.timedOut && result.ended;
}

/**
 * Runs one Compose command that changes the acceptance project behind the
 * barrier file: {@link setBarrier}, then {@link runCompose}, then
 * {@link clearBarrier} only when the command completed
 * ({@link commandCompleted}). The file stays after a command stopped at its
 * deadline or whose end could not be confirmed, and after a run stopped,
 * cancelled or killed before the command ended, since nothing deletes it
 * then; every later fresh run refuses to start until an operator has checked
 * the project and deleted it. When `docker` cannot be started nothing ran, and
 * the file is deleted before the error propagates.
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint every Compose call of this run
 *   names ({@link resolveDockerEndpoint}).
 * @param args - The Compose subcommand and its arguments.
 * @param deadlineMs - How long the command may run before it is stopped.
 * @returns The command's outcome, as {@link runCompose} reports it.
 * @throws BarrierFileError when the barrier file cannot be written, and the
 *   command then does not run, or cannot be deleted. Error as
 *   {@link runCompose} throws it when `docker` cannot be started.
 */
async function runProjectCommand(
  root: string,
  endpoint: string,
  args: string[],
  deadlineMs: number,
): Promise<ComposeResult> {
  setBarrier();
  let result: ComposeResult;
  try {
    result = await runCompose(root, endpoint, args, deadlineMs);
  } catch (err) {
    // docker could not be started, so nothing ran.
    clearBarrier();
    throw err;
  }
  if (commandCompleted(result)) {
    clearBarrier();
  }
  return result;
}

/**
 * Tells an operator how to check that the acceptance project has settled:
 * no Compose command for it still runs on this host and its containers have
 * settled, as `docker --host <endpoint> compose … ps --all` shows, with that
 * command line when this checkout's paths can be quoted for every shell
 * ({@link portableCommandLine}).
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint this run's Compose calls name.
 * @returns The instruction, in lower case so it can continue a sentence, over
 *   one or more lines, without a final full stop.
 */
function settledProjectCheck(root: string, endpoint: string): string {
  const listing = portableCommandLine(['docker', ...composeArgs(endpoint, root, 'ps', '--all')]);
  const shown =
    listing === undefined
      ? `as docker --host ${endpoint} compose ps --all, with this checkout's project, Compose file, project directory and env file, shows`
      : `as\n  ${listing}\nshows`;
  return `make sure no docker compose command for project ${ACCEPTANCE_PROJECT} is still running on this host and that its containers have settled, ${shown}`;
}

/**
 * Builds the message a fresh run fails with, before any Compose call, while
 * the barrier file exists ({@link barrierExists}): fixed text naming the file
 * and the project, then the recovery guidance ({@link settledProjectCheck}).
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint this run's Compose calls name.
 * @returns The message, over several lines.
 */
function barrierRefusalMessage(root: string, endpoint: string): string {
  const file = barrierPath();
  return [
    `The acceptance-run barrier file ${file} exists: a fresh run on this host was stopped, killed or timed out while a docker compose command on project ${ACCEPTANCE_PROJECT} was in progress, or could not confirm that one had finished, so this run stops before any Compose call.`,
    `To recover, ${settledProjectCheck(root, endpoint)}. Then delete ${file} and run the tests again; a fresh run starts by removing the project with down -v.`,
  ].join('\n');
}

/**
 * Builds the message of an {@link UnsettledProjectError}: the failure to state
 * first, if any, then fixed text naming the operation, the project and the
 * barrier file the run leaves ({@link barrierPath}), then the recovery
 * guidance ({@link settledProjectCheck}, {@link manualRemovalHint}).
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint the command named.
 * @param operation - The Compose subcommand and its arguments, as run.
 * @param failure - A failure to state first, if any.
 * @returns The message, over several lines.
 */
function unsettledProjectMessage(
  root: string,
  endpoint: string,
  operation: string,
  failure: string | undefined,
): string {
  const file = barrierPath();
  return [
    ...(failure === undefined ? [] : [failure]),
    `Setup could not confirm that docker compose ${operation} for project ${ACCEPTANCE_PROJECT} finished (it was stopped at its deadline or its end could not be confirmed), so it left the project as it is, keeps the acceptance-run lock until this run ends, and left the barrier file ${file}, which makes every fresh run by this user on this host refuse to start while it exists.`,
    `To recover, ${settledProjectCheck(root, endpoint)}. Then delete ${file} and run the tests again, which starts by removing the project with down -v; or, to remove the project by hand before deleting the file:`,
    manualRemovalHint(root, endpoint),
  ].join('\n');
}

/**
 * The error setup fails with when a Compose command that changes the
 * acceptance project did not complete ({@link commandCompleted}): it was
 * stopped at its deadline or its end could not be confirmed. Setup then runs
 * no further Compose command, leaves the project and the barrier file
 * ({@link barrierPath}) as they are and keeps the acceptance-run lock until
 * this run ends ({@link globalSetup}). Its message is built by
 * {@link unsettledProjectMessage}.
 */
class UnsettledProjectError extends Error {
  /**
   * @param root - The repository root, for the commands in the guidance.
   * @param endpoint - The local Docker endpoint the command named, for the
   *   commands in the guidance.
   * @param operation - The Compose subcommand that did not complete, with its
   *   arguments, as run.
   * @param failure - The failure the run reports first, such as that of the
   *   `up` an unfinished removal followed, if any.
   */
  constructor(root: string, endpoint: string, operation: string, failure?: string) {
    super(unsettledProjectMessage(root, endpoint, operation, failure));
    this.name = 'UnsettledProjectError';
  }
}

/**
 * Removes the acceptance project after an `up` that completed
 * ({@link commandCompleted}) with a failure, so the next run starts clean.
 * The removal runs through {@link runProjectCommand}, behind the barrier
 * file, under {@link COMPOSE_DOWN_TIMEOUT_MS}. Its own failure is only
 * reported, so the `up` failure stays the one the run fails with: when the
 * removal completes with a non-zero exit code, after its Compose output, when
 * `docker` cannot be started, and when the barrier file cannot be written or
 * deleted, the report ends with the same {@link manualRemovalHint}. A removal
 * that did not complete, because it was stopped at its deadline or its end
 * could not be confirmed, is not settled: its Compose output is printed, the
 * barrier file stays and the caller fails with an
 * {@link UnsettledProjectError}, which carries the guidance.
 *
 * @param root - The repository root.
 * @param endpoint - The local Docker endpoint the failed `up` used.
 * @returns `false` when the removal did not complete, so the run must keep the
 *   acceptance-run lock; `true` otherwise. It never rejects.
 */
async function removeFailedStack(root: string, endpoint: string): Promise<boolean> {
  let result: ComposeResult;
  try {
    result = await runProjectCommand(root, endpoint, DOWN_ARGS, COMPOSE_DOWN_TIMEOUT_MS);
  } catch (err) {
    if (err instanceof BarrierFileError) {
      // Fixed text naming the barrier path and at most a validated code.
      console.error(
        `Removing the failed ${ACCEPTANCE_PROJECT} stack ran into a barrier file error. ${err.message} ${manualRemovalHint(root, endpoint)}`,
      );
      return true;
    }
    // runCompose rejects with fixed text; only its validated code is repeated.
    const code = validatedErrorCode(err);
    console.error(
      `Removing the failed ${ACCEPTANCE_PROJECT} stack also failed (docker compose could not be started${code === undefined ? '' : `, ${code}`}). ${manualRemovalHint(root, endpoint)}`,
    );
    return true;
  }
  if (!commandCompleted(result)) {
    printComposeFailure(DOWN_ARGS.join(' '), result, COMPOSE_DOWN_TIMEOUT_MS);
    return false;
  }
  if (result.code !== 0) {
    printComposeFailure(DOWN_ARGS.join(' '), result, COMPOSE_DOWN_TIMEOUT_MS);
    console.error(
      `Removing the failed ${ACCEPTANCE_PROJECT} stack also failed. ${manualRemovalHint(root, endpoint)}`,
    );
  }
  return true;
}

/** The loopback address {@link ACCEPTANCE_LOCK_PORT} is listened on. */
const ACCEPTANCE_LOCK_HOST = '127.0.0.1';

/**
 * Makes this run the single owner of the acceptance project on this host by
 * listening on `127.0.0.1` port {@link ACCEPTANCE_LOCK_PORT}. The operating
 * system lets only one socket listen there and frees the port when this
 * process ends, however it ends, a kill included, so no lock is ever left
 * behind and none is ever taken over.
 *
 * The lock is content-free: every client that connects is disconnected at
 * once and sent nothing, so the port tells a local client only that it is
 * held. A run refused the lock reads nothing from the port either, so its
 * message names only the port and cannot tell another fresh run from another
 * program.
 *
 * The listener is unreferenced, so it never keeps the process alive. Node
 * opens it close-on-exec, so the Compose child processes do not inherit it
 * and cannot hold the port after this run ends.
 *
 * @returns The release function. It closes the listener, runs once however
 *   often it is called, and never throws: release runs while a run ends,
 *   where the run's own result or setup error must be the one reported.
 * @throws Error with one fixed message naming the port when it is already in
 *   use, by another fresh run or by another program; Error naming the address
 *   and the failure's validated code when the port cannot be listened on for
 *   any other reason. No Compose call has run in any of these cases.
 */
async function acquireAcceptanceLock(): Promise<() => void> {
  const lockAddress = `${ACCEPTANCE_LOCK_HOST}:${ACCEPTANCE_LOCK_PORT}`;
  const server = net.createServer((socket) => {
    // A persistent listener: a client that resets the connection before it is
    // closed must not fail the run that owns the stack.
    socket.on('error', () => socket.destroy());
    socket.destroy();
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
    throw new Error(
      `Port ${ACCEPTANCE_LOCK_PORT} on ${ACCEPTANCE_LOCK_HOST}, which a fresh acceptance run holds while it owns the ${ACCEPTANCE_PROJECT} stack, is in use: another fresh acceptance run owns the stack, or another program holds the port. Wait for that run to finish, or free the port, and run the tests again.`,
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
 * the direct API requests reach the same stack and database. Before its first
 * Compose call it asks the docker CLI which Docker daemon it selects
 * (`DOCKER_HOST`, else `DOCKER_CONTEXT`, else the current context) and fails
 * unless that is a local unix socket or a local Windows named pipe, because
 * the run removes the project's volumes and reaches the stack on this
 * machine's `127.0.0.1` ports; every Compose call then names that daemon with
 * `--host`, so no other daemon's project of the same name is ever removed.
 * Every Compose call also names the project, the repository's
 * `docker-compose.yml` and the empty `deploy/acceptance.env` explicitly and
 * runs with the Compose and `SMARTHOTEL_*` variables neutralized, so a
 * developer's stack, `.env` and overrides are never touched or read. It never
 * switches to other ports. The stack is left running after the run; the next
 * fresh run removes it, and so does
 * `docker --host <endpoint> compose -p smarthotel-acceptance -f <root>/docker-compose.yml --project-directory <root> --env-file <root>/deploy/acceptance.env down -v --remove-orphans`,
 * with the local endpoint the run printed, in a shell without the Compose
 * selection and `SMARTHOTEL_*` variables.
 *
 * One fresh run per host owns the acceptance project. Before its first
 * Compose call a fresh run listens on `127.0.0.1` port
 * {@link ACCEPTANCE_LOCK_PORT} (18079), which serves no content, and holds it
 * until the suite's global teardown; the operating system frees the port when
 * the run ends, however it ends. A second fresh run started meanwhile, or any
 * fresh run while another program holds the port, fails before any Compose
 * call with one message naming the port and nothing about its holder
 * ({@link acquireAcceptanceLock}), so it never removes the stack another run
 * is testing.
 *
 * The lock ends with the run, so a barrier file outlives it: before each
 * Compose command that changes the project, setup writes an empty file in
 * the user's home directory ({@link barrierPath}) and deletes it once the
 * command has completed, that is, ended on its own before its deadline with
 * its output pipes closed ({@link runProjectCommand}). A command stopped at
 * its deadline, one whose end could not be confirmed, and a run stopped,
 * cancelled or killed before the command ended leave the file. Still before
 * any Compose call, a fresh run holding the lock fails while the file exists,
 * on every platform, until an operator has checked the project and deleted
 * it. A setup failure releases the lock before its error propagates, except
 * after a Compose command that did not complete, as below.
 *
 * Every Compose command runs under a deadline: 5 minutes for each `down`
 * ({@link COMPOSE_DOWN_TIMEOUT_MS}) and 30 minutes for the `up`
 * ({@link COMPOSE_UP_TIMEOUT_MS}), which covers a first build on a clean
 * machine. At a deadline setup attempts to stop the command's process tree
 * (on POSIX SIGTERM, then SIGSTOP and SIGKILL; on Windows `taskkill /T /F`,
 * falling back to the CLI alone) and bounds the drain that follows
 * ({@link runCompose}); processes can outlive that drain. A command that did
 * not complete ({@link commandCompleted}) is followed by no further Compose
 * command, not even the `down -v` after a failed `up`: setup leaves the
 * project and the barrier file as they are, keeps the lock until this run
 * ends and fails with recovery guidance ({@link UnsettledProjectError}). An
 * `up` that completed with a failure is followed by `down -v`.
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
 * @throws Error when the lock port is in use or cannot be listened on, the
 *   docker CLI selects a Docker daemon that is not local or cannot say which
 *   it selects, the barrier file exists or cannot be checked, written or
 *   deleted, Docker or Compose is unavailable or fails, an acceptance port is
 *   taken, the target does not answer, or in fresh mode the web app's
 *   `/config.json` does not name the acceptance API; the message says which.
 *   An {@link UnsettledProjectError}, after which the lock stays held until
 *   the run ends and the barrier file remains, when a Compose command was
 *   stopped at its deadline or its end could not be confirmed. A failed,
 *   timed-out or unconfirmed Compose command's recognized status lines and
 *   likely causes are printed before the throw.
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
  const releaseLock = await acquireAcceptanceLock();
  try {
    await buildAcceptanceStack(root, target);
  } catch (err) {
    // An unsettled project keeps the lock until this run ends (the OS frees
    // it then), and the barrier file it leaves keeps later fresh runs out
    // after that.
    if (!(err instanceof UnsettledProjectError)) {
      releaseLock();
    }
    throw err;
  }
  return async () => {
    releaseLock();
  };
}

/**
 * The fresh path of {@link globalSetup}, run while this run holds the lock:
 * resolves the local Docker daemon every Compose call names, refuses to start
 * while the barrier file exists ({@link barrierExists}), removes the
 * acceptance project, checks the acceptance ports, starts the stack, probes
 * `target` and requires the web app's `/config.json` to name
 * `target.apiUrl`. Each `down` and the `up` run behind the barrier file
 * ({@link runProjectCommand}); after one that did not complete
 * ({@link commandCompleted}) it runs no further Compose command and fails
 * with an {@link UnsettledProjectError}, leaving the file in place.
 *
 * @param root - The repository root.
 * @param target - The fresh target, the stack this run builds.
 * @throws Error when the docker CLI selects a Docker daemon that is not local
 *   or cannot say which it selects ({@link resolveDockerEndpoint}), the
 *   barrier file exists, both before any Compose call, a Compose command
 *   completes with a failure or cannot be started, an acceptance port is
 *   taken, the target does not answer or the web app's `/config.json` does
 *   not name the acceptance API. BarrierFileError when the barrier file
 *   cannot be checked, written or deleted. {@link UnsettledProjectError} when
 *   a Compose command was stopped at its deadline or its end could not be
 *   confirmed.
 */
async function buildAcceptanceStack(root: string, target: StackTarget): Promise<void> {
  // First, before any Compose call: the removal below must reach this
  // machine's Docker daemon and no other.
  const endpoint = await resolveDockerEndpoint();
  console.log(`Docker daemon: ${endpoint} (local).`);
  // Still before any Compose call, so a refused run has started nothing and
  // releases the lock as usual.
  if (barrierExists()) {
    throw new Error(barrierRefusalMessage(root, endpoint));
  }
  console.log(
    `Building the ${ACCEPTANCE_PROJECT} stack (web :${ACCEPTANCE_WEB_PORT}, API :${ACCEPTANCE_API_PORT})…`,
  );

  const down = await runProjectCommand(root, endpoint, DOWN_ARGS, COMPOSE_DOWN_TIMEOUT_MS);
  if (!commandCompleted(down)) {
    printComposeFailure(DOWN_ARGS.join(' '), down, COMPOSE_DOWN_TIMEOUT_MS);
    throw new UnsettledProjectError(root, endpoint, DOWN_ARGS.join(' '));
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
  const up = await runProjectCommand(root, endpoint, UP_ARGS, COMPOSE_UP_TIMEOUT_MS);
  if (!commandCompleted(up)) {
    printComposeFailure(UP_ARGS.join(' '), up, COMPOSE_UP_TIMEOUT_MS);
    // The `down -v` a failed `up` is followed by is deferred, because nothing
    // can show the daemon has finished an `up` that was stopped or whose end
    // is unconfirmed. The barrier file keeps every fresh run out until an
    // operator has checked the project, and the next fresh run then starts
    // with that same removal.
    throw new UnsettledProjectError(
      root,
      endpoint,
      UP_ARGS.join(' '),
      up.timedOut
        ? `docker compose up --wait did not finish within ${describeDuration(COMPOSE_UP_TIMEOUT_MS)} for project ${ACCEPTANCE_PROJECT}; see the Compose output above.`
        : `docker compose up --wait exited with code ${up.code} for project ${ACCEPTANCE_PROJECT}, but its end could not be confirmed; see the Compose output above.`,
    );
  }
  if (up.code !== 0) {
    printComposeFailure(UP_ARGS.join(' '), up, COMPOSE_UP_TIMEOUT_MS);
    const failure = `docker compose up --wait failed for project ${ACCEPTANCE_PROJECT}; see the Compose output above.`;
    if (!(await removeFailedStack(root, endpoint))) {
      throw new UnsettledProjectError(root, endpoint, DOWN_ARGS.join(' '), failure);
    }
    throw new Error(failure);
  }

  await probeTarget(target);
  console.log(`Acceptance stack ready at ${target.baseUrl} (API ${target.apiUrl}).`);
}
