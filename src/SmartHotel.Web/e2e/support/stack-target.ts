/**
 * The acceptance-stack contract: which Compose project the web tests build,
 * on which host ports, how Compose is selected, and which URLs the tests
 * target.
 *
 * Consumers:
 * - `playwright.config.ts` resolves the target with {@link resolveStackTarget},
 *   sets `use.baseURL` and writes the pair into `E2E_BASE_URL` and
 *   `E2E_API_URL`, so every spec and worker reads the same target.
 * - `e2e/global-setup.ts` builds the stack with {@link composeArgs} and
 *   {@link composeEnvironment}, from the root {@link findRepositoryRoot} finds.
 * - `e2e/support/helpers.ts` falls back to {@link resolveStackTarget} for the
 *   API URL.
 *
 * Playwright transpiles this file as CommonJS (the workspace `package.json`
 * declares no `"type": "module"`), so `__dirname` and `process` are globals.
 * The workspace declares no `@types/node` and `tsconfig.app.json` excludes
 * `e2e/**`, so the type annotations here are stripped and never checked.
 *
 * Importing this module has no side effects: it performs no I/O, logs
 * nothing and never writes to `process.env`.
 */
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * The dedicated Compose project of the acceptance stack. Its containers,
 * networks and volumes are its own, so a developer's stack, under its own
 * project name, is never stopped, reset or read.
 */
export const ACCEPTANCE_PROJECT = 'smarthotel-acceptance';

/**
 * Host port of the acceptance `api` service, passed to Compose as
 * `SMARTHOTEL_API_PORT`.
 */
export const ACCEPTANCE_API_PORT = 18080;

/**
 * Host port of the acceptance `web` service, passed to Compose as
 * `SMARTHOTEL_WEB_PORT`.
 */
export const ACCEPTANCE_WEB_PORT = 14200;

/**
 * The `127.0.0.1` port a fresh run listens on while it owns the acceptance
 * project, so only one fresh run per host owns it at a time. It is never a
 * stack port, and Compose never publishes it.
 */
export const ACCEPTANCE_LOCK_PORT = 18079;

/** Reuse-mode default web URL: the developer stack's web origin. */
export const DEFAULT_REUSE_BASE_URL = 'http://localhost:4200';

/** Reuse-mode default API base URL: the developer stack's API. */
export const DEFAULT_REUSE_API_URL = 'http://localhost:8080/api';

/** The web app and API a test run targets. */
export interface StackTarget {
  /** Web origin, with no trailing slash, for example `http://localhost:14200`. */
  baseUrl: string;
  /**
   * API base ending in `/api`, with no trailing slash, for example
   * `http://localhost:18080/api`.
   */
  apiUrl: string;
  /** `true` only in reuse mode (`E2E_REUSE_STACK=1`). */
  reuse: boolean;
}

/**
 * Compose variables that could select another project, file, profile or env
 * file. Held upper-case and compared against upper-cased names.
 */
const DROPPED_COMPOSE_VARIABLES = new Set([
  'COMPOSE_PROJECT_NAME',
  'COMPOSE_FILE',
  'COMPOSE_PROFILES',
  'COMPOSE_ENV_FILES',
]);

/** Prefix of the stack's own variables: ports and first-start secret overrides. */
const SMARTHOTEL_VARIABLE_PREFIX = 'SMARTHOTEL_';

/** Removes exactly one trailing `/`, if present. */
function trimTrailingSlash(value: string): string {
  return value.endsWith('/') ? value.slice(0, -1) : value;
}

/**
 * Tells whether the run reuses an already running stack instead of building
 * the acceptance stack.
 *
 * `E2E_REUSE_STACK` accepts exactly the string `1`. Every other value is
 * rejected rather than read as "not reuse mode", because a fresh run removes
 * and rebuilds the acceptance stack: a mistyped value such as `true`, `yes`,
 * ` 1` or `0` must stop the run before any Compose call instead of silently
 * selecting that destructive path.
 *
 * @param env - The environment to read; defaults to `process.env`.
 * @returns `true` when `E2E_REUSE_STACK` is exactly `1`; `false` when it is
 *   unset or empty.
 * @throws Error when `E2E_REUSE_STACK` holds any other value. The message names
 *   the variable and its accepted value, never the value it holds.
 */
export function isReuseMode(env: Record<string, string | undefined> = process.env): boolean {
  const value = env['E2E_REUSE_STACK'];
  if (value === undefined || value === '') {
    return false;
  }
  if (value === '1') {
    return true;
  }
  throw new Error(
    'E2E_REUSE_STACK accepts only 1, which reuses a running stack; leave it unset or empty for a fresh acceptance run. Any other value is rejected so a mistyped value never removes and rebuilds the acceptance stack.',
  );
}

/**
 * Resolves the web and API URLs the tests target.
 *
 * In a fresh run the target is always the acceptance stack the run just built,
 * `http://localhost:14200` and `http://localhost:18080/api`, and
 * `E2E_BASE_URL` and `E2E_API_URL` are ignored, so a stale value can never
 * point a fresh run at another stack.
 *
 * In reuse mode the target is `E2E_BASE_URL` and `E2E_API_URL`, each with one
 * trailing slash trimmed; an unset or empty value falls back to
 * {@link DEFAULT_REUSE_BASE_URL} or {@link DEFAULT_REUSE_API_URL}. Reuse mode
 * is for local iteration only and never counts as evidence for any success
 * criterion.
 *
 * The fresh target uses `localhost`, never `127.0.0.1`: the stack derives its
 * CORS origin (`http://localhost:<web port>`) and the SPA's API base URL
 * (`http://localhost:<api port>/api`) from the same port variables, and a
 * different host string would be a different origin for CORS and for the
 * SPA's token-attachment check.
 *
 * @param env - The environment to read; defaults to `process.env`.
 * @returns The resolved {@link StackTarget}.
 * @throws Error when `E2E_REUSE_STACK` is set to anything other than `1` or
 *   the empty string, as {@link isReuseMode} reports it. `playwright.config.ts`
 *   resolves the target when it loads, so such a run fails before global
 *   setup makes any Compose call.
 */
export function resolveStackTarget(
  env: Record<string, string | undefined> = process.env,
): StackTarget {
  if (!isReuseMode(env)) {
    return {
      baseUrl: `http://localhost:${ACCEPTANCE_WEB_PORT}`,
      apiUrl: `http://localhost:${ACCEPTANCE_API_PORT}/api`,
      reuse: false,
    };
  }
  return {
    baseUrl: trimTrailingSlash(env['E2E_BASE_URL'] || DEFAULT_REUSE_BASE_URL),
    apiUrl: trimTrailingSlash(env['E2E_API_URL'] || DEFAULT_REUSE_API_URL),
    reuse: true,
  };
}

/**
 * Finds the repository root by walking up from `startDir` to the first
 * directory that holds `SmartHotel.sln`, the same marker the API integration
 * tests use.
 *
 * @param startDir - The directory to start from; defaults to this module's
 *   directory, from which the root is four levels up.
 * @returns The absolute path of the directory holding `SmartHotel.sln`.
 * @throws Error when no ancestor of `startDir` holds `SmartHotel.sln`. The
 *   message names only the start path.
 */
export function findRepositoryRoot(startDir: string = __dirname): string {
  let dir = path.resolve(startDir);
  while (!fs.existsSync(path.join(dir, 'SmartHotel.sln'))) {
    const parent = path.dirname(dir);
    if (parent === dir) {
      throw new Error(
        `Could not find SmartHotel.sln above ${startDir}; run the tests from inside the SmartHotel repository.`,
      );
    }
    dir = parent;
  }
  return dir;
}

/**
 * Builds the `docker` arguments for a Compose command on the acceptance
 * project, with the global options ahead of the subcommand:
 *
 * - `-p smarthotel-acceptance` selects the dedicated project.
 * - `-f <root>/docker-compose.yml` names the file explicitly, which also stops
 *   Compose from merging a `docker-compose.override.yml`.
 * - `--project-directory <root>` pins the Compose file's relative paths, such
 *   as the `deploy/db/db-entrypoint.sh` bind mount and the build contexts, to
 *   the repository root.
 * - `--env-file <root>/deploy/acceptance.env` replaces the default `.env`
 *   lookup with an intentionally empty file, so a developer's `.env` and its
 *   `SMARTHOTEL_*` overrides are never read.
 *
 * Example: `composeArgs(root, 'up', '-d', '--build', '--force-recreate', '--wait')`.
 *
 * @param root - The repository root, as {@link findRepositoryRoot} returns it.
 * @param args - The Compose subcommand and its own arguments.
 * @returns The full argument list for `docker`.
 */
export function composeArgs(root: string, ...args: string[]): string[] {
  return [
    'compose',
    '-p',
    ACCEPTANCE_PROJECT,
    '-f',
    path.join(root, 'docker-compose.yml'),
    '--project-directory',
    root,
    '--env-file',
    path.join(root, 'deploy', 'acceptance.env'),
    ...args,
  ];
}

/**
 * Builds the environment for every Compose child process of the acceptance
 * stack, as a new object; `env` itself is never modified.
 *
 * It drops `COMPOSE_PROJECT_NAME`, `COMPOSE_FILE`, `COMPOSE_PROFILES`,
 * `COMPOSE_ENV_FILES` and every `SMARTHOTEL_*` variable, then sets only
 * `SMARTHOTEL_API_PORT` and `SMARTHOTEL_WEB_PORT` to the acceptance ports.
 * As a result no profile is enabled, so `db-test` never starts; no first-start
 * override (`SMARTHOTEL_DB_PASSWORD`, `SMARTHOTEL_JWT_SIGNING_KEY`) leaks in,
 * so the stack generates fresh secrets; and nothing selects another project or
 * file. Every other variable, `PATH` and `DOCKER_HOST` included, passes
 * through unchanged, so the Docker CLI still works.
 *
 * @param env - The environment to start from; defaults to `process.env`.
 * @returns The filtered environment with the two acceptance ports set.
 */
export function composeEnvironment(
  env: Record<string, string | undefined> = process.env,
): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(env)) {
    // Names are compared upper-cased because Windows environment names are
    // case-insensitive: a differently cased variable must not reach Compose.
    const name = key.toUpperCase();
    if (
      typeof value !== 'string' ||
      DROPPED_COMPOSE_VARIABLES.has(name) ||
      name.startsWith(SMARTHOTEL_VARIABLE_PREFIX)
    ) {
      continue;
    }
    result[key] = value;
  }
  result['SMARTHOTEL_API_PORT'] = String(ACCEPTANCE_API_PORT);
  result['SMARTHOTEL_WEB_PORT'] = String(ACCEPTANCE_WEB_PORT);
  return result;
}
