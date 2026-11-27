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
 * Tells whether something already accepts TCP connections on a loopback port,
 * the address Compose publishes the acceptance ports on.
 *
 * @param port - The host port to check.
 * @returns `true` when a connection succeeds; `false` when it is refused, fails
 *   otherwise or does not complete within {@link PORT_CHECK_TIMEOUT_MS}.
 */
function isPortInUse(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = net.connect({ host: '127.0.0.1', port });
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
 * Removes the acceptance project after a failed `up`, so the next run starts
 * clean. Its own failure is only reported, so the `up` failure stays the one
 * the run fails with.
 */
async function removeFailedStack(root: string): Promise<void> {
  const manualCommand = `docker compose -p ${ACCEPTANCE_PROJECT} down -v`;
  try {
    const result = await runCompose(root, DOWN_ARGS);
    if (result.code !== 0) {
      console.error(result.output);
      console.error(`Removing the failed ${ACCEPTANCE_PROJECT} stack also failed; remove it with "${manualCommand}".`);
    }
  } catch (err) {
    console.error(
      `Removing the failed ${ACCEPTANCE_PROJECT} stack also failed (${describeFailure(err)}); remove it with "${manualCommand}".`,
    );
  }
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
 * Reuse mode (`E2E_REUSE_STACK=1`) is for local iteration. It makes no Compose
 * call and only probes the stack at `E2E_BASE_URL` and `E2E_API_URL`
 * (defaults `http://localhost:4200` and `http://localhost:8080/api`).
 *
 * @throws Error when Docker or Compose is unavailable or fails, an acceptance
 *   port is taken, or the target does not answer; the message says which.
 */
export default async function globalSetup(): Promise<void> {
  const target = resolveStackTarget();

  if (isReuseMode()) {
    await probeTarget(target);
    console.log(
      `Reusing the running stack at ${target.baseUrl} (API ${target.apiUrl}); results are not acceptance evidence.`,
    );
    return;
  }

  const root = findRepositoryRoot();
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
