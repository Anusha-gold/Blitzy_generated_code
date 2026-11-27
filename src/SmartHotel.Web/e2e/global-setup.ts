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
 * Playwright transpiles this file as CommonJS and it is never type-checked
 * (no `@types/node`; `tsconfig.app.json` excludes `e2e/**`). It uses Node's
 * built-in modules and globals only.
 *
 * Nothing here prints an environment value: the Compose child receives the
 * environment but no message names a variable's value, and the stack's
 * secrets are generated inside the `db` container, so Compose output, which
 * is printed on failure, never holds them.
 */
import { spawn } from 'node:child_process';
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
  /** The exit code; a child that ended on a signal counts as `1`. */
  code: number;
  /** Everything the command wrote to stdout and stderr, in arrival order. */
  output: string;
}

/** Removes the acceptance project with its containers, networks and volumes. */
const DOWN_ARGS = ['down', '-v', '--remove-orphans'];

/**
 * Builds current images and starts db, api and web from fresh volumes.
 * `--wait` (Compose v2.1.1 or later) returns only once every service's
 * healthcheck passes, and the `api` healthcheck passes only after migration
 * and seeding, so success means a migrated and seeded stack. No timeout is
 * placed around it: an image build can take several minutes.
 */
const UP_ARGS = ['up', '-d', '--build', '--force-recreate', '--wait'];

/** HTTP probe attempts, which absorb a listener that has only just started. */
const PROBE_ATTEMPTS = 10;

/** Pause between HTTP probe attempts. */
const PROBE_INTERVAL_MS = 1_000;

/** Upper bound for one HTTP probe request, body included. */
const PROBE_REQUEST_TIMEOUT_MS = 10_000;

/** Upper bound for one TCP connection attempt of the port check. */
const PORT_CHECK_TIMEOUT_MS = 1_000;

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

/**
 * Describes a failure in one line, without headers, bodies or environment
 * values.
 */
function describeFailure(err: unknown): string {
  if (!(err instanceof Error)) {
    return String(err);
  }
  // fetch reports every network failure as "fetch failed" and keeps the
  // socket error, such as ECONNREFUSED, in `cause`. When several addresses
  // fail, Node can raise an AggregateError with an empty message, so its code
  // stands in.
  const cause = (err as { cause?: unknown }).cause;
  const detail = cause instanceof Error ? cause.message || String(errorCode(cause) ?? '') : '';
  return detail ? `${err.message}: ${detail}` : err.message;
}

/**
 * Runs one Compose command on the acceptance project, from the repository
 * root, with the neutralized Compose environment. Output is collected, not
 * streamed, so it is printed once and only when a command fails.
 *
 * @param root - The repository root.
 * @param args - The Compose subcommand and its arguments.
 * @returns The exit code and the collected output.
 * @throws Error when `docker` cannot be started at all.
 */
function runCompose(root: string, args: string[]): Promise<ComposeResult> {
  return new Promise((resolve, reject) => {
    const child = spawn('docker', composeArgs(root, ...args), {
      cwd: root,
      env: composeEnvironment(),
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    const chunks: string[] = [];
    const collect = (chunk: string) => chunks.push(chunk);
    // Decoding on the stream keeps a multi-byte character split across two
    // chunks intact.
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', collect);
    child.stderr.on('data', collect);
    child.on('error', (err: unknown) => {
      if (errorCode(err) === 'ENOENT') {
        reject(
          new Error(
            'The docker CLI was not found on PATH. The acceptance stack needs Docker Engine 28.0 or later with Docker Compose v2.1.1 or later.',
          ),
        );
        return;
      }
      reject(
        new Error(
          `Could not run "docker compose ${args.join(' ')}" for project ${ACCEPTANCE_PROJECT}: ${describeFailure(err)}`,
        ),
      );
    });
    // A promise settles once, so a 'close' that follows a spawn 'error'
    // changes nothing.
    child.on('close', (code: number | null) => {
      resolve({ code: code ?? 1, output: chunks.join('') });
    });
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

/**
 * Requires `url` to answer `200`, and with `expectBody` as its trimmed body
 * when given, within {@link PROBE_ATTEMPTS} attempts.
 *
 * @param url - The absolute URL to request.
 * @param expectBody - The exact trimmed body required, if any.
 * @throws Error naming the URL and the last failure (a status code or an error
 *   message) when every attempt fails.
 */
async function probe(url: string, expectBody?: string): Promise<void> {
  let reason = '';
  for (let attempt = 1; attempt <= PROBE_ATTEMPTS; attempt += 1) {
    if (attempt > 1) {
      await delay(PROBE_INTERVAL_MS);
    }
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(PROBE_REQUEST_TIMEOUT_MS) });
      // Reading the body on every path releases the connection.
      const body = await response.text();
      if (response.status !== 200) {
        reason = `HTTP ${response.status}`;
      } else if (expectBody !== undefined && body.trim() !== expectBody) {
        reason = `HTTP 200 without the expected "${expectBody}" body`;
      } else {
        return;
      }
    } catch (err) {
      reason = describeFailure(err);
    }
  }
  throw new Error(`The acceptance stack is not reachable at ${url} (${reason}).`);
}

/**
 * Confirms the pair the specs target: the API readiness probe answers
 * `Healthy` and the web root answers `200`.
 */
async function probeTarget(target: StackTarget): Promise<void> {
  await probe(`${target.apiUrl}/health`, 'Healthy');
  await probe(`${target.baseUrl}/`);
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
 * clean. Its own failure is only reported, so the `up` failure stays the one
 * the run fails with. Whether the removal exits non-zero, after its Compose
 * output, or `docker` cannot be started, the report ends with the same
 * {@link manualRemovalHint}.
 *
 * @param root - The repository root.
 */
async function removeFailedStack(root: string): Promise<void> {
  try {
    const result = await runCompose(root, DOWN_ARGS);
    if (result.code !== 0) {
      console.error(result.output);
      console.error(`Removing the failed ${ACCEPTANCE_PROJECT} stack also failed. ${manualRemovalHint(root)}`);
    }
  } catch (err) {
    console.error(
      `Removing the failed ${ACCEPTANCE_PROJECT} stack also failed (${describeFailure(err)}). ${manualRemovalHint(root)}`,
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
  // The message names only the address and the failure.
  const reportServerError = (err: unknown) => {
    console.error(`The acceptance-run lock on ${lockAddress} reported an error and stays held: ${describeFailure(err)}`);
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
        `Could not listen on ${lockAddress}, which a fresh acceptance run holds while it owns the ${ACCEPTANCE_PROJECT} stack: ${describeFailure(err)}`,
      );
    }
    const holder = parseLockOwner(await readLockReply());
    if (holder !== undefined) {
      throw new Error(
        `Another fresh acceptance run owns the ${ACCEPTANCE_PROJECT} stack (pid ${holder.pid}, started ${holder.startedAt}, from ${holder.root}). Wait for it to finish and run the tests again.`,
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
        `Could not close the acceptance-run lock on ${lockAddress} (${describeFailure(err)}); it is freed when this run ends.`,
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
 * until all three are healthy. Every Compose call names the project, the
 * repository's `docker-compose.yml` and the empty `deploy/acceptance.env`
 * explicitly and runs with the Compose and `SMARTHOTEL_*` variables
 * neutralized, so a developer's stack, `.env` and overrides are never touched
 * or read. It never switches to other ports. The stack is left running after
 * the run; remove it with `docker compose -p smarthotel-acceptance down -v`.
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
 * Reuse mode (`E2E_REUSE_STACK=1`) is for local iteration. It takes no lock,
 * makes no Compose call and only probes the stack at `E2E_BASE_URL` and
 * `E2E_API_URL` (defaults `http://localhost:4200` and
 * `http://localhost:8080/api`).
 *
 * @returns In fresh mode, the global teardown Playwright runs after the
 *   suite: it releases the lock and leaves the stack running. In reuse mode,
 *   nothing.
 * @throws Error when another fresh run owns the acceptance stack, another
 *   program holds the lock port or it cannot be listened on, Docker or
 *   Compose is unavailable or fails, an acceptance port is taken, or the
 *   target does not answer; the message says which.
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
 * stack and probes `target`.
 *
 * @param root - The repository root.
 * @param target - The fresh target, the stack this run builds.
 * @throws Error when Compose fails, an acceptance port is taken or the target
 *   does not answer.
 */
async function buildAcceptanceStack(root: string, target: StackTarget): Promise<void> {
  console.log(
    `Building the ${ACCEPTANCE_PROJECT} stack (web :${ACCEPTANCE_WEB_PORT}, API :${ACCEPTANCE_API_PORT})…`,
  );

  const down = await runCompose(root, DOWN_ARGS);
  if (down.code !== 0) {
    console.error(down.output);
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
  const up = await runCompose(root, UP_ARGS);
  if (up.code !== 0) {
    console.error(up.output);
    await removeFailedStack(root);
    throw new Error(
      `docker compose up --wait failed for project ${ACCEPTANCE_PROJECT}; see the Compose output above.`,
    );
  }

  await probeTarget(target);
  console.log(`Acceptance stack ready at ${target.baseUrl} (API ${target.apiUrl}).`);
}
