/**
 * The acceptance-stack contract: which Compose project the web tests build,
 * on which host ports, how Compose is selected, and which URLs the tests
 * target.
 *
 * Consumers:
 * - `playwright.config.ts` resolves the target with {@link resolveStackTarget},
 *   sets `use.baseURL` and writes the pair into `E2E_BASE_URL` and
 *   `E2E_API_URL`, so every spec and worker reads the same target.
 * - `e2e/global-setup.ts` asks the docker CLI which Docker daemon it selects
 *   with {@link DOCKER_ENDPOINT_QUERY_ARGS}, then builds the stack with
 *   {@link composeArgs}, which accepts only the local endpoint
 *   {@link localDockerEndpoint} validates, and {@link composeEnvironment},
 *   from the root {@link findRepositoryRoot} finds.
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

/**
 * The web app and API a test run targets. Both URLs are canonical: scheme,
 * host and port only, plus the fixed path `/api` for the API, with no user
 * name, password, query, fragment or trailing slash.
 */
export interface StackTarget {
  /** Web origin, with no trailing slash, for example `http://localhost:14200`. */
  baseUrl: string;
  /**
   * API base `<origin>/api`, with no trailing slash, for example
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

/**
 * A reuse URL's host with an optional port: a host name of dot-separated
 * labels of ASCII letters, digits and `-` (no empty label, no trailing dot),
 * or a bracketed IPv6 literal, then optionally `:` and one to five digits.
 */
const REUSE_AUTHORITY = /^(?:[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*|\[[0-9A-Fa-f:.]+\])(?::[0-9]{1,5})?$/;

/**
 * Validates one reuse-mode URL setting and returns its canonical form.
 *
 * The raw value is checked against a strict grammar before any URL parsing,
 * because the parser repairs input: it skips extra slashes after the scheme,
 * resolves `.` and `..` segments, reads `\` as `/` and percent-encodes
 * non-ASCII characters. The value must:
 *
 * - consist only of printable ASCII (U+0021 to U+007E) and hold no `\`;
 * - start with `http://` or `https://`, in any letter case;
 * - hold no `?` or `#` anywhere, so no query or fragment, not even an empty
 *   one;
 * - have, up to the first `/` or the end, a non-empty authority with no user
 *   name or password (`@`) that is a host name of dot-separated labels of
 *   letters, digits and `-` (no empty label, no trailing dot) or a bracketed
 *   IPv6 literal, optionally followed by `:` and one to five digits;
 * - for `web`, have no path beyond trailing `/`, so the URL is an origin; for
 *   `api`, have the path `/api` exactly, in lower case, optionally followed by
 *   trailing `/`.
 *
 * The URL parser then rejects what the grammar admits but no URL allows, such
 * as a port above 65535 or a malformed IPv6 literal. The canonical form is the
 * parsed scheme and host with its port, plus `/api` for `api`: parsing
 * lower-cases the scheme and host and drops a default port. Only those
 * components and the fixed `/api` reach the result, so it never carries a path
 * of the caller's choosing, and canonicalizing a canonical value returns it
 * unchanged.
 *
 * @param name - The variable the value came from, named in the error.
 * @param value - The setting's value.
 * @param kind - `web` for the web origin, `api` for the API base URL.
 * @returns The canonical URL, with no trailing `/`.
 * @throws Error naming the variable, the broken rule and the default. The
 *   message never holds the value or any part of it, and the parser's own
 *   error, whose message can echo the input, is neither shown nor attached.
 */
function canonicalReuseUrl(
  name: 'E2E_BASE_URL' | 'E2E_API_URL',
  value: string,
  kind: 'web' | 'api',
): string {
  const rule =
    kind === 'web'
      ? `an absolute http or https URL in printable ASCII with no user name, password, query or fragment, and no path beyond a trailing / (default ${DEFAULT_REUSE_BASE_URL})`
      : `an absolute http or https URL in printable ASCII with no user name, password, query or fragment, whose path is /api (default ${DEFAULT_REUSE_API_URL})`;
  const invalid = (problem: string) =>
    new Error(`${name} is not valid (${problem}). It must be ${rule}. Its value is not shown.`);

  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code < 0x21 || code > 0x7e) {
      throw invalid('it contains a character other than printable ASCII');
    }
  }
  if (value.includes('\\')) {
    throw invalid('it contains a backslash');
  }
  const scheme = /^https?:\/\//i.exec(value);
  if (scheme === null) {
    throw invalid('it does not start with http:// or https://');
  }
  if (value.includes('?') || value.includes('#')) {
    throw invalid('it has a query or fragment');
  }

  const rest = value.slice(scheme[0].length);
  const pathStart = rest.indexOf('/');
  const authority = pathStart === -1 ? rest : rest.slice(0, pathStart);
  const rawPath = pathStart === -1 ? '' : rest.slice(pathStart);
  if (authority === '') {
    throw invalid('it has no host');
  }
  if (authority.includes('@')) {
    throw invalid('it has a user name or password');
  }
  if (!REUSE_AUTHORITY.test(authority)) {
    throw invalid('its host or port is not valid');
  }
  if (kind === 'web' && !/^\/*$/.test(rawPath)) {
    throw invalid('it has a path');
  }
  if (kind === 'api' && !/^\/api\/*$/.test(rawPath)) {
    throw invalid('its path is not /api');
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    // The parser's message can quote the input, so it is dropped, not chained.
    throw invalid('it is not a valid URL');
  }
  return kind === 'web' ? `${url.protocol}//${url.host}` : `${url.protocol}//${url.host}/api`;
}

/**
 * Tells whether the run reuses an already running stack instead of building
 * the acceptance stack.
 *
 * Unset or empty `E2E_REUSE_STACK` selects fresh mode; exactly `1` selects
 * reuse mode. Any other value, such as `true`, `yes`, ` 1` or `0`, throws
 * before any Compose call rather than being read as fresh mode, because a
 * fresh run removes and rebuilds the acceptance stack: a mistyped value must
 * stop the run, never silently select that destructive path.
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
 * In reuse mode the target is `E2E_BASE_URL` and `E2E_API_URL`; an unset or
 * empty value falls back to {@link DEFAULT_REUSE_BASE_URL} or
 * {@link DEFAULT_REUSE_API_URL}. Each must be an absolute `http` or `https`
 * URL made only of printable ASCII, with no backslash, user name, password,
 * query or fragment, and with a host name or bracketed IPv6 literal and an
 * optional port; `E2E_BASE_URL` must be an origin, optionally with trailing
 * `/`, and the path of `E2E_API_URL` must be `/api`, optionally with trailing
 * `/`. The raw value is checked before URL parsing, so nothing the parser
 * would repair is accepted. Each is returned in canonical form: the scheme
 * and host lower-cased, the port only when it is not the scheme's default,
 * no trailing `/`, and for `E2E_API_URL` the path `/api` and nothing else.
 * Resolving a canonical pair again returns it unchanged, so the pair
 * `playwright.config.ts` writes back into the environment resolves the same
 * way in every worker. Reuse mode is for local iteration only and never
 * counts as evidence for any success criterion.
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
 * @throws Error in reuse mode when `E2E_BASE_URL` or `E2E_API_URL` breaks
 *   these rules. The message names the variable and the rule, never the value.
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
    baseUrl: canonicalReuseUrl('E2E_BASE_URL', env['E2E_BASE_URL'] || DEFAULT_REUSE_BASE_URL, 'web'),
    apiUrl: canonicalReuseUrl('E2E_API_URL', env['E2E_API_URL'] || DEFAULT_REUSE_API_URL, 'api'),
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
 * The `docker` arguments that ask the CLI which Docker daemon it selects.
 * `docker context inspect` with no context name inspects the context the CLI
 * selects: the endpoint `DOCKER_HOST` names when it is set, else the context
 * `DOCKER_CONTEXT` names, else the configured current context. With this
 * format it prints only that context's Docker endpoint, such as
 * `unix:///var/run/docker.sock`, on one line. It reads the CLI's
 * configuration only and contacts no daemon.
 */
export const DOCKER_ENDPOINT_QUERY_ARGS = ['context', 'inspect', '--format', '{{.Endpoints.docker.Host}}'];

/** The longest unix socket path, in characters, {@link localDockerEndpoint} accepts. */
const MAX_SOCKET_PATH_LENGTH = 4096;

/**
 * A unix socket endpoint whose path, captured, is absolute and holds no
 * control character (C0, DEL or C1). {@link localDockerEndpoint} bounds the
 * path's length separately, in UTF-16 code units.
 */
const UNIX_SOCKET_ENDPOINT = /^unix:\/\/(\/\P{Cc}+)$/u;

/**
 * A named pipe on this computer (`.`), whose name is ASCII letters, digits,
 * `_`, `.` and `-`, such as Docker Desktop's `npipe:////./pipe/docker_engine`.
 */
const LOCAL_NAMED_PIPE_ENDPOINT = /^npipe:\/\/\/\/\.\/pipe\/[A-Za-z0-9_.-]+$/;

/**
 * Names, in fixed words, the kind of Docker endpoint {@link localDockerEndpoint}
 * refuses, so its message never repeats the endpoint, which can carry a user
 * name or a host.
 *
 * @param endpoint - The refused endpoint, without its trailing line break.
 * @returns A phrase such as `a TCP endpoint, which can be another machine`.
 */
function describeRefusedEndpoint(endpoint: string): string {
  if (endpoint === '') {
    return 'no endpoint';
  }
  if (/[\r\n]/.test(endpoint)) {
    return 'more than one line instead of one endpoint';
  }
  const scheme = /^([A-Za-z][A-Za-z0-9+.-]*):\/\//.exec(endpoint)?.[1].toLowerCase();
  switch (scheme) {
    case 'tcp':
      return 'a TCP endpoint, which can be another machine';
    case 'http':
    case 'https':
      return 'an HTTP endpoint, which can be another machine';
    case 'ssh':
      return 'an SSH endpoint, which is another machine';
    case 'fd':
      return 'an inherited socket endpoint (fd://), which cannot be shown to be on this machine';
    case 'npipe':
      return 'a named pipe on another computer, or not in the local form npipe:////./pipe/<name>';
    case 'unix':
      return `a unix socket whose path is not an absolute path of at most ${MAX_SOCKET_PATH_LENGTH} characters without control characters`;
    default:
      return 'an endpoint that is neither a local unix socket nor a local Windows named pipe';
  }
}

/**
 * Validates the Docker endpoint the docker CLI reported for
 * {@link DOCKER_ENDPOINT_QUERY_ARGS} and returns it when it is a daemon on
 * this machine. One trailing line break (LF or CR LF) is removed first; the
 * rest must be one line and one of:
 *
 * - `unix://` followed by an absolute path (starting with `/`) of at most
 *   4096 characters with no control character (C0, DEL or C1);
 * - a local Windows named pipe, `npipe:////./pipe/<name>`, whose name is ASCII
 *   letters, digits, `_`, `.` and `-`.
 *
 * Everything else is refused, failing closed: `tcp://`, `http://`, `https://`,
 * `ssh://` and `fd://` endpoints, a named pipe on another computer such as
 * `npipe:////server/pipe/docker_engine`, a relative socket path, an empty
 * reply, several lines and anything unrecognized. The acceptance run removes
 * and rebuilds the `smarthotel-acceptance` project with its volumes and
 * reaches the stack on this machine's `127.0.0.1` ports, so it runs only
 * against this machine's Docker daemon.
 *
 * Trust boundary: no API proves where a daemon runs, so a unix socket or
 * named pipe on this machine is trusted as this machine's Docker daemon,
 * Docker Engine's, rootless Docker's or Docker Desktop's, whose published
 * `127.0.0.1` ports reach this host. This check cannot tell whether such a
 * socket forwards to another machine, as an SSH-forwarded socket does, so
 * `DOCKER_HOST` or a context must never point at one.
 *
 * @param reported - What the docker CLI printed on stdout.
 * @returns The accepted endpoint, without its trailing line break.
 * @throws Error naming the kind of endpoint refused, why the run needs a local
 *   daemon and how to select one. The message never holds the endpoint or any
 *   part of it, because it can carry a user name or a host.
 */
export function localDockerEndpoint(reported: string): string {
  const endpoint = reported.replace(/\r?\n$/, '');
  const socketPath = UNIX_SOCKET_ENDPOINT.exec(endpoint)?.[1];
  if (
    (socketPath !== undefined && socketPath.length <= MAX_SOCKET_PATH_LENGTH) ||
    LOCAL_NAMED_PIPE_ENDPOINT.test(endpoint)
  ) {
    return endpoint;
  }
  throw new Error(
    `Refusing the Docker daemon the docker CLI selects: it reports ${describeRefusedEndpoint(endpoint)}. ` +
      `The acceptance run removes and rebuilds the ${ACCEPTANCE_PROJECT} project and its volumes and reaches the stack on this machine's 127.0.0.1 ports, ` +
      'so it runs only against this machine\'s Docker daemon, through a local unix socket (unix:///<path>) or a local Windows named pipe (npipe:////./pipe/<name>). ' +
      'Unset DOCKER_HOST or DOCKER_CONTEXT, or switch with `docker context use` to a local context such as `default` or Docker Desktop\'s, then run the tests again. ' +
      'The endpoint is not shown.',
  );
}

/**
 * Builds the `docker` arguments for a Compose command on the acceptance
 * project, with the global options ahead of the subcommand:
 *
 * - `--host <endpoint>`, a `docker` option placed before `compose`, names the
 *   Docker daemon: the endpoint the CLI reported for
 *   {@link DOCKER_ENDPOINT_QUERY_ARGS}, validated here by
 *   {@link localDockerEndpoint}, so no argument list for a daemon that is not
 *   local can be built. The CLI ranks `--host` above `DOCKER_HOST`,
 *   `DOCKER_CONTEXT` and the current context, so every Compose call of a run
 *   acts on the same daemon.
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
 * Example:
 * `composeArgs('unix:///var/run/docker.sock', root, 'up', '-d', '--build', '--force-recreate', '--wait')`.
 *
 * @param endpoint - The Docker endpoint the docker CLI reported for
 *   {@link DOCKER_ENDPOINT_QUERY_ARGS}.
 * @param root - The repository root, as {@link findRepositoryRoot} returns it.
 * @param args - The Compose subcommand and its own arguments.
 * @returns The full argument list for `docker`.
 * @throws Error when `endpoint` is not a local unix socket or local Windows
 *   named pipe, as {@link localDockerEndpoint} reports it.
 */
export function composeArgs(endpoint: string, root: string, ...args: string[]): string[] {
  return [
    '--host',
    localDockerEndpoint(endpoint),
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
 * file. Every other variable, `PATH`, `DOCKER_HOST` and `DOCKER_CONTEXT`
 * included, passes through unchanged, so the Docker CLI still works.
 * `DOCKER_HOST` and `DOCKER_CONTEXT` only decide which endpoint
 * {@link DOCKER_ENDPOINT_QUERY_ARGS} reports: every Compose call then names
 * that endpoint with `--host` ({@link composeArgs}), which the CLI ranks above
 * both.
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
