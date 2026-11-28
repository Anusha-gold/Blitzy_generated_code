/**
 * Success criterion 7: `docker compose up` on a clean machine starts all three
 * services and seeds the data with no manual step, and `GET /api/hotels` for a
 * seeded city answers within 1 second at the 95th percentile with 20
 * concurrent users.
 *
 * The clean start is proved before this spec runs: in a fresh run
 * `e2e/global-setup.ts` builds the dedicated acceptance stack from fresh
 * volumes, and its `docker compose up --wait` must exit 0 with db, api and web
 * healthy. The first test then checks that the stack answers and holds exactly
 * the seeded catalogue: 2 hotels in Seattle, 2 in New York and 1 in Chicago,
 * each with the room types Standard, Deluxe, Family and Suite and 10 free
 * rooms of each.
 *
 * The second test measures search latency with this load profile: 20 virtual
 * users, each with its own `APIRequestContext`; 20 warm-up requests, one per
 * user, discarded; then 25 sequential requests per user with no think time,
 * all users running concurrently, for 500 samples of wall-clock time per
 * request. The nearest-rank 95th percentile must be at most 1,000 ms. Search
 * requests do not follow redirects, so a 3xx fails rather than standing in for
 * the direct `GET /api/hotels` response.
 *
 * The remaining tests cover the two states the served page shows before
 * Angular starts, which a successful boot passes through unseen. With
 * `/config.json` held back in a route handler, the static boot-loading message
 * from `index.html` is on screen and no part of the app shell is; once the
 * stack's real response is let through, the search page replaces it. One
 * parameterized test then fails the configuration in each way the SPA must
 * survive: the request fails, the server answers 500, the body is not JSON,
 * the configuration names a plain-http API on another host outside
 * Development, or no answer arrives. The last case runs the page on a paused
 * clock and moves it across the SPA's configuration deadline, checking that
 * the page still shows boot-loading one millisecond before it. In every case
 * the exact boot-error alert replaces boot-loading, Angular renders nothing,
 * the browser sends nothing to the API or to the rejected host, and the app
 * writes nothing to the console. The global stylesheet is blocked throughout,
 * so the alert's colour, weight and padding must come from the host page
 * itself.
 *
 * The next tests cover the web server's security headers. One parameterized
 * test requests the shell at `/`, a deep link with search criteria,
 * `/config.json`, a hashed bundle the served shell names, and a hashed name
 * the build does not hold. Every answer, that 404 included, must carry
 * exactly one X-Frame-Options `DENY`, X-Content-Type-Options `nosniff`,
 * Referrer-Policy `no-referrer` and Content-Security-Policy. The policy must
 * deny framing, allow no script but this origin's and hashed event handlers,
 * and let the page connect only to its own origin and the API origin of
 * `apiUrl()`. The Cache-Control each location sets must be sent unchanged
 * beside them, which leaves the 404 without one, and neither the Server
 * header nor the body may name a server version. The other test loads the
 * shell in the browser under that policy: the search page renders, the global
 * stylesheet's load handler switches it to `media="all"`, and the page reports
 * no policy violation and writes nothing to the console.
 *
 * The tests after those render the web server as the web image would for
 * inputs the stack under test does not use. Each render starts the runtime
 * image the web Dockerfile names, with that file's template filter and input
 * guard and the checkout's nginx template, so the guard checks and rewrites
 * the inputs before the template is rendered, and reads the
 * Content-Security-Policy, the served `/config.json` and the guard's
 * warnings. The container publishes no port and has no network.
 *
 * One parameterized test checks the policy a Production deployment is served
 * with, which the stack, a Development one, cannot show, once per API base
 * URL. connect-src must admit exactly the API origin the browser derives from
 * a host with a trailing dot; from an upper-case scheme and host with the
 * default port and a trailing slash; from an IPv4 address with a port and
 * from one with a trailing dot; from a value with a leading space, no
 * slashes, an empty user name and an empty port; from a plain host; from a
 * percent-encoded host letter, with or without a non-ASCII path segment the
 * browser removes; from an IPv4 address written as one number and from one
 * in hexadecimal, decimal and octal parts; and from a zero-padded default
 * port. `/config.json` must serve the base URL with that same origin, spelled
 * the same way, and the rest exactly as given. connect-src must be `'self'`
 * alone for an IPv6 literal and for a base URL with a user name, and
 * `/config.json` must serve either as given. For a host with a percent-encoded
 * fullwidth letter and for a non-ASCII domain name, which the browser maps to
 * an ASCII form, the guard must issue exactly one warning, which names the
 * input and the reason and never the host, `/config.json` must serve an empty
 * `apiBaseUrl` and connect-src must be `'self'` alone. Every other case must
 * draw no warning. A page served under the rendered policy then requests the
 * API origin the browser derives from the base URL, which must succeed
 * exactly when it is admitted, and an unrelated origin, which Chromium must
 * refuse every time. The page's requests are answered in the browser, so this
 * test reaches neither the stack nor the network.
 *
 * The next test renders the target API's base URL with the first character
 * of its host percent-encoded, in Development: the guard issues no warning,
 * `/config.json` serves the target's own base URL and connect-src admits its
 * origin. The stack's web app is then loaded with the rendered policy in
 * place of its own on every document and the rendered `/config.json`, and a
 * search for Seattle, 1 guest, through the page must show the 2 seeded
 * hotels, from one successful request to the target API, with no policy
 * violation and no app console output.
 *
 * The last, parameterized test checks the guard's 1000-byte limit for each
 * input: a value of exactly 1000 bytes, in ASCII characters or in 500
 * two-byte ones, is kept with no warning, and one of 1001 bytes is served as
 * empty with exactly one warning, which names the input and the limit and
 * never the value. No test name or failure message carries one of those
 * values.
 *
 * API requests use absolute URLs from `apiUrl()`, because the configured
 * `baseURL` is the web origin. The security-header tests send nothing to the
 * API, so they also pass against a web server the API's CORS policy does not
 * admit.
 */
import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { promisify } from 'node:util';

import {
  test,
  expect,
  request,
  type APIRequestContext,
  type APIResponse,
  type Page,
  type Route,
} from '@playwright/test';

import { apiUrl, fillBound, isApiEndpoint, utcDates } from './support/helpers';

/** One element of the `GET /api/hotels` array. */
interface HotelSummary {
  id: number;
  name: string;
  city: string;
  address: string;
  lowestNightlyPrice: number;
}

/** The members of the `GET /api/hotels/{id}` body this spec reads. */
interface HotelDetail {
  id: number;
  roomTypes: { id: number; name: string; maxGuests: number; availableRooms: number }[];
}

/** Every seeded city, as stored, with the number of hotels seeded in it. */
const SEEDED_CITIES: ReadonlyArray<readonly [string, number]> = [
  ['Seattle', 2],
  ['New York', 2],
  ['Chicago', 1],
];

const SEEDED_HOTEL_COUNT = 5;

const SEEDED_ROOM_TYPE_NAMES = ['Deluxe', 'Family', 'Standard', 'Suite'];

const ROOMS_PER_TYPE = 10;

const USERS = 20;

const REQUESTS_PER_USER = 25;

const WARM_UP = 20;

const THRESHOLD_MS = 1_000;

/** Request options that return a redirect as-is, so only the direct response is checked and timed. */
const NO_REDIRECTS = { maxRedirects: 0 } as const;

/** The exact text of the static boot-loading element in `src/index.html`. */
const BOOT_LOADING_TEXT = 'Loading SmartHotel…';

/** The exact text of the boot-error alert `src/main.ts` shows. */
const BOOT_ERROR_TEXT =
  'SmartHotel could not start: its configuration is unavailable. Reload to try again.';

/**
 * How long the SPA waits for `/config.json` before it shows the boot-error
 * alert: `CONFIG_DEADLINE_MS` in `src/app/core/config/runtime-config.ts`,
 * repeated here because that module imports Angular, which the test runner
 * cannot load.
 */
const CONFIG_DEADLINE_MS = 10_000;

/** The host the rejected configuration names; the browser must never contact it. */
const REJECTED_API_HOST = 'evil.example.com';

/**
 * The computed style the host page's inline rules give the boot-error alert:
 * the error colour token `#b00020`, weight 600 and 1rem of padding.
 */
const BOOT_ERROR_STYLE: ReadonlyArray<readonly [string, string]> = [
  ['color', 'rgb(176, 0, 32)'],
  ['font-weight', '600'],
  ['padding-top', '16px'],
];

/**
 * Where the never-answered case installs the page clock, before the page
 * loads. An installed clock runs in real time until it is paused at
 * {@link CLOCK_PAUSED_AT} by a separate call, so from the page's first script
 * on no timer fires until the test moves it.
 */
const CLOCK_INSTALLED_AT = new Date('2026-06-01T08:00:00Z');

/**
 * Where the never-answered case pauses the page clock: a day after
 * {@link CLOCK_INSTALLED_AT}. A clock cannot be paused at a time it has
 * already passed, and the installed clock keeps running in real time until
 * the pause call reaches the browser, so the gap is far longer than the whole
 * test may take: however long scheduling delays that call, the target is
 * still ahead. Both calls happen before the page loads, so no page timer
 * exists to fire across the jump.
 */
const CLOCK_PAUSED_AT = new Date('2026-06-02T08:00:00Z');

/**
 * A path in the build's hashed-bundle form that the served build does not
 * hold, so the web server's hashed-asset location answers it with a 404
 * rather than the HTML shell.
 */
const MISSING_HASHED_FILE = '/main-ZZZZZZZZ.js';

/** The security headers every web server response carries, with their exact values. */
const SECURITY_HEADERS: ReadonlyArray<readonly [string, string]> = [
  ['x-frame-options', 'DENY'],
  ['x-content-type-options', 'nosniff'],
  ['referrer-policy', 'no-referrer'],
];

/**
 * The Content-Security-Policy directives with a fixed source list, in the
 * order the web server sends their sources. `connect-src`, which names the
 * target's API origin, and `script-src`, which carries the build's handler
 * hash, are checked on their own.
 */
const FIXED_CSP_DIRECTIVES: ReadonlyArray<readonly [string, readonly string[]]> = [
  ['default-src', ["'self'"]],
  ['style-src', ["'self'", "'unsafe-inline'"]],
  ['object-src', ["'none'"]],
  ['base-uri', ["'self'"]],
  ['form-action', ["'self'"]],
  ['frame-ancestors', ["'none'"]],
];

/**
 * The only `script-src` sources the policy may hold: this origin, the keyword
 * that lets a hash match an event-handler attribute, and SHA-256 hashes. Any
 * other source, `'unsafe-inline'` or `'unsafe-eval'` among them, would allow
 * script the build does not ship.
 */
const ALLOWED_SCRIPT_SOURCE = /^(?:'self'|'unsafe-hashes'|'sha256-[A-Za-z0-9+/]+={0,2}')$/;

/**
 * The web workspace, the parent of this spec's directory: it holds the web
 * image's Dockerfile and the nginx template.
 */
const WEB_WORKSPACE = join(__dirname, '..');

/**
 * Where the web image's runtime stage reads the nginx template, and where a
 * render mounts the checkout's template.
 */
const TEMPLATE_TARGET = '/etc/nginx/templates/default.conf.template';

/** Runs a command and resolves with its output once it exits with code 0. */
const execFileAsync = promisify(execFile);

/**
 * Upper bound for one render: the `docker run` of the runtime image, its
 * input guard and template rendering, the nginx start and the two requests,
 * with room for a first pull of the pinned image.
 */
const RENDER_TIMEOUT_MS = 120_000;

/**
 * Upper bound, in seconds, for the image's entrypoint inside the render
 * container. With {@link RENDER_REQUEST_TIMEOUT_S} it lets the container end
 * by itself even when the `docker` CLI that started it is stopped.
 */
const RENDER_ENTRYPOINT_TIMEOUT_S = 30;

/** Upper bound, in seconds, for each request inside the render container. */
const RENDER_REQUEST_TIMEOUT_S = 10;

/**
 * The line the render container prints before the response headers, which
 * keeps them apart from anything else written to its output.
 */
const HEADERS_BEGIN = '--- smarthotel-web response headers begin ---';

/** The line the render container prints after the response headers. */
const HEADERS_END = '--- smarthotel-web response headers end ---';

/** The line the render container prints before the `/config.json` body. */
const CONFIG_BEGIN = '--- smarthotel-web config.json begin ---';

/** The line the render container prints after the `/config.json` body. */
const CONFIG_END = '--- smarthotel-web config.json end ---';

/**
 * The environment variable that carries the image's input guard into the
 * render container. The container writes the guard where the image keeps it
 * and removes the variable before the image's entrypoint runs.
 */
const INPUT_GUARD_VARIABLE = 'SMARTHOTEL_RENDER_INPUT_GUARD';

/** The most lines of a failed render's error output that its error message quotes: its last ones. */
const RENDER_OUTPUT_MAX_LINES = 20;

/** The page origin the production-policy browser check serves its document from. */
const POLICY_PAGE_ORIGIN = 'https://app.example.test';

/** An origin that is not the API's: the production policy must refuse the page's requests to it. */
const UNRELATED_ORIGIN = 'https://evil.example.test';

/**
 * How long the production-policy browser check waits for the browser to
 * report the refused request to {@link UNRELATED_ORIGIN}.
 */
const VIOLATION_REPORT_TIMEOUT_MS = 5_000;

/** Matches the SPA's request for its runtime configuration. */
function isConfigRequest(url: URL): boolean {
  return url.pathname === '/config.json';
}

/** Matches the build's global stylesheet, `styles-<hash>.css`. */
function isGlobalStylesheet(url: URL): boolean {
  return /^\/styles-[A-Z0-9]+\.css$/.test(url.pathname);
}

/** A configuration the SPA accepts for the target stack, as a JSON document. */
function acceptedConfigBody(): string {
  return JSON.stringify({ apiBaseUrl: apiUrl(), environment: 'Development' });
}

/** The hold a route handler keeps on the page's `/config.json` request. */
interface ConfigGate {
  /** Settles once the request has reached the route handler. */
  reached: Promise<void>;
  /** Lets the held request go on to its answer; calling it again changes nothing. */
  release(): void;
}

/**
 * Holds every `/config.json` request of `page` in a route handler until the
 * returned gate is released, and then answers it with `answer`. The test
 * releases the gate and unroutes in cleanup, so no request stays held.
 */
async function holdConfigRequest(page: Page, answer: (route: Route) => Promise<void>): Promise<ConfigGate> {
  let markReached = (): void => undefined;
  let release = (): void => undefined;
  const reached = new Promise<void>((resolve) => {
    markReached = resolve;
  });
  const released = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(isConfigRequest, async (route) => {
    markReached();
    await released;
    await answer(route);
  });
  return { reached, release };
}

/** Asserts that Angular has rendered no part of the app shell. */
async function expectNoShell(page: Page): Promise<void> {
  await expect(page.locator('app-nav-bar')).toHaveCount(0);
  await expect(page.locator('main')).toHaveCount(0);
}

/**
 * Records, from now on, every console message and uncaught error of `page`,
 * and returns the list it fills. Chromium's own "Failed to load resource"
 * notice for the configuration or the stylesheet, the requests a test fails
 * on purpose, is left out: the browser writes it, not the app, and it names
 * the failed request's URL as its location.
 */
function recordAppConsole(page: Page): string[] {
  const output: string[] = [];
  page.on('console', (message) => {
    const location = new URL(message.location().url || 'about:blank');
    const failedOnPurpose = isConfigRequest(location) || isGlobalStylesheet(location);
    if (failedOnPurpose && message.text().startsWith('Failed to load resource:')) {
      return;
    }
    output.push(`${message.type()}: ${message.text()}`);
  });
  page.on('pageerror', (error) => {
    output.push(`uncaught: ${error.message}`);
  });
  return output;
}

/** One way `/config.json` can fail, and how the test makes it fail. */
interface ConfigFailure {
  /** The failure, as the test name states it. */
  name: string;
  /**
   * Answers the `/config.json` request with the failure. Absent when the
   * request is never answered: the test then moves the page clock across the
   * configuration deadline, and only cleanup aborts the held request.
   */
  answer?: (route: Route) => Promise<void>;
}

/** Every configuration failure the SPA must turn into the boot-error state. */
const CONFIG_FAILURES: readonly ConfigFailure[] = [
  {
    name: 'the /config.json request fails',
    answer: (route) => route.abort('failed'),
  },
  {
    // The body is a configuration the SPA would accept, so only the status
    // can reject it.
    name: '/config.json answers HTTP 500',
    answer: (route) =>
      route.fulfill({ status: 500, contentType: 'application/json', body: acceptedConfigBody() }),
  },
  {
    // An accepted configuration cut short before its closing brace.
    name: '/config.json is not valid JSON',
    answer: (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: acceptedConfigBody().slice(0, -1) }),
  },
  {
    name: '/config.json names a plain-http API on another host outside Development',
    answer: (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ apiBaseUrl: `http://${REJECTED_API_HOST}/api`, environment: 'Production' }),
      }),
  },
  {
    name: '/config.json does not answer within the configuration deadline',
  },
];

/**
 * Reads the served shell and returns the path of the main bundle it names, a
 * hashed file such as `/main-VNW5AL5S.js`.
 *
 * @throws Error when the shell names no `main-<hash>.js` script.
 */
async function hashedBundlePath(web: APIRequestContext): Promise<string> {
  const shell = await web.get('/', NO_REDIRECTS);
  expect(shell.status(), 'GET / for the shell').toBe(200);
  const bundle = /<script src="(main-[A-Z0-9]{8}\.js)"/.exec(await shell.text());
  if (bundle === null) {
    throw new Error('The served shell names no main-<hash>.js bundle.');
  }
  return `/${bundle[1]}`;
}

/** One web server response whose headers the security-header test checks. */
interface WebResponseCase {
  /** The response, as the test name states it. */
  name: string;
  /** The path to request on the web origin, or a function that reads it from the web server first. */
  path: string | ((web: APIRequestContext) => Promise<string>);
  /** The status the web server must answer with. */
  status: number;
  /**
   * The answering location's own Cache-Control, which the security headers
   * must not displace; absent when the response must carry none.
   */
  cacheControl?: string;
}

/** Every kind of web server response the security headers must reach. */
const SECURITY_HEADER_CASES: readonly WebResponseCase[] = [
  { name: 'the shell at /', path: '/', status: 200, cacheControl: 'no-cache' },
  { name: 'the deep link /search?city=Seattle', path: '/search?city=Seattle', status: 200, cacheControl: 'no-cache' },
  { name: '/config.json', path: '/config.json', status: 200, cacheControl: 'no-store' },
  {
    name: 'the hashed main bundle the shell names',
    path: hashedBundlePath,
    status: 200,
    cacheControl: 'public, max-age=31536000, immutable',
  },
  {
    // The hashed-asset location's immutable Cache-Control stays off its 404.
    name: `the missing hashed file ${MISSING_HASHED_FILE} (404)`,
    path: MISSING_HASHED_FILE,
    status: 404,
  },
];

/**
 * Returns the value of the one `name` header `response` carries, and fails
 * when it carries none or several: a second copy would mean a location sends
 * the header again instead of adding its own headers to the server's.
 *
 * @param name - The header name, lower-cased.
 */
function singleHeader(response: APIResponse, name: string): string {
  const values = response
    .headersArray()
    .filter((header) => header.name.toLowerCase() === name)
    .map((header) => header.value);
  expect(values, `${name} headers`).toHaveLength(1);
  return values[0];
}

/**
 * Parses a Content-Security-Policy into its directives, each lower-cased name
 * mapped to its source list. A repeated directive keeps its first occurrence,
 * as browsers do.
 */
function cspDirectives(policy: string): Map<string, string[]> {
  const directives = new Map<string, string[]>();
  for (const directive of policy.split(';')) {
    const [name, ...sources] = directive.trim().split(/\s+/);
    const key = name.toLowerCase();
    if (key !== '' && !directives.has(key)) {
      directives.set(key, sources);
    }
  }
  return directives;
}

/** Where the browser test's init script keeps the policy violations the page reports. */
interface CspViolationLog {
  smarthotelCspViolations?: string[];
}

/** One production API base URL whose rendered policy the production-policy test checks. */
type ProductionOriginCase = {
  /** What the base URL exercises, as the test name states it. */
  name: string;
  /** The `API_BASE_URL` the web server is started with. */
  apiBaseUrl: string;
} & (
  | {
      /**
       * connect-src admits the API origin the browser derives from the base
       * URL, and the input guard issues no warning.
       */
      outcome: 'admitted';
      /**
       * The `apiBaseUrl` `/config.json` serves: the base URL with its origin
       * as the browser serializes it and the rest exactly as given.
       */
      served: string;
    }
  | {
      /**
       * `'served as given'`: the input guard issues no warning, `/config.json`
       * serves the base URL exactly as given, and connect-src is `'self'`
       * alone. `'served as empty'`: the input guard issues exactly one
       * warning, `/config.json` serves an empty `apiBaseUrl`, which the SPA
       * rejects, and connect-src is `'self'` alone.
       */
      outcome: 'served as given' | 'served as empty';
    }
);

/**
 * The warning the input guard issues, after its file name, for a base URL
 * whose host holds a non-ASCII character: it names the input and the reason,
 * never the value.
 */
const NON_ASCII_HOST_WARNING = /: API_BASE_URL has a host with a non-ASCII character\b.*; it is served as empty$/;

/**
 * Production base URLs in the forms a deployment can write. The admitted ones
 * are each a base URL the SPA accepts in Production, written in a form the
 * browser's URL parser rewrites before it sends a request: the policy must
 * name the origin the SPA actually calls. Those served as given must leave
 * connect-src at `'self'`, never widen it. Those served as empty have a host
 * the browser maps to an ASCII form the image cannot compute, so the image
 * clears them rather than serve a base URL whose origin the policy would not
 * name.
 */
const PRODUCTION_ORIGIN_CASES: readonly ProductionOriginCase[] = [
  {
    name: 'a host with a trailing dot',
    apiBaseUrl: 'https://api.example.test./api',
    outcome: 'admitted',
    served: 'https://api.example.test./api',
  },
  {
    name: 'an upper-case scheme and host, the default port and a trailing slash',
    apiBaseUrl: 'HTTPS://API.Example.TEST:443/api/',
    outcome: 'admitted',
    served: 'https://api.example.test/api/',
  },
  {
    name: 'an IPv4 address with a port',
    apiBaseUrl: 'https://192.0.2.10:8443/api',
    outcome: 'admitted',
    served: 'https://192.0.2.10:8443/api',
  },
  {
    name: 'an IPv4 address with a trailing dot',
    apiBaseUrl: 'https://192.0.2.10./api',
    outcome: 'admitted',
    served: 'https://192.0.2.10/api',
  },
  {
    name: 'a leading space, no slashes, an empty user name and an empty port',
    apiBaseUrl: ' https:@api.example.test:/api',
    outcome: 'admitted',
    served: 'https://api.example.test/api',
  },
  {
    name: 'a plain host',
    apiBaseUrl: 'https://api.example.test/api',
    outcome: 'admitted',
    served: 'https://api.example.test/api',
  },
  {
    name: 'a percent-encoded host letter',
    apiBaseUrl: 'https://%61pi.example.test/api',
    outcome: 'admitted',
    served: 'https://api.example.test/api',
  },
  {
    name: 'a percent-encoded host letter and a non-ASCII path segment the browser removes',
    apiBaseUrl: 'https://%61pi.example.test/caf\u00e9/../api',
    outcome: 'admitted',
    served: 'https://api.example.test/caf\u00e9/../api',
  },
  {
    name: 'an IPv4 address written as one number',
    apiBaseUrl: 'https://3221225994/api',
    outcome: 'admitted',
    served: 'https://192.0.2.10/api',
  },
  {
    name: 'an IPv4 address in hexadecimal, decimal and octal parts with a port',
    apiBaseUrl: 'https://0xC0.0.02.0x0a:8443/api',
    outcome: 'admitted',
    served: 'https://192.0.2.10:8443/api',
  },
  {
    name: 'a zero-padded default port',
    apiBaseUrl: 'https://api.example.test:000443/api',
    outcome: 'admitted',
    served: 'https://api.example.test/api',
  },
  {
    name: 'an IPv6 literal, which no source expression can name',
    apiBaseUrl: 'https://[2001:db8::1]/api',
    outcome: 'served as given',
  },
  {
    name: 'a user name, which the SPA rejects',
    apiBaseUrl: 'https://user@api.example.test/api',
    outcome: 'served as given',
  },
  {
    name: 'a percent-encoded fullwidth host letter, which the browser maps to ASCII',
    apiBaseUrl: 'https://%EF%BD%81pi.example.test/api',
    outcome: 'served as empty',
  },
  {
    name: 'a non-ASCII domain name, which the browser writes in punycode',
    apiBaseUrl: 'https://b\u00fccher.example/api',
    outcome: 'served as empty',
  },
];

/** The input guard's limit for each of its two inputs, in bytes. */
const GUARD_LIMIT_BYTES = 1_000;

/** The API base URL the input-guard test starts the web server with while it checks `WEB_ENVIRONMENT`. */
const GUARD_API_BASE_URL = 'https://api.example.test/api';

/** One input length the input-guard test checks at the guard's limit. */
interface GuardLengthCase {
  /** The value's length and make-up, as the test name states it; never the value itself. */
  name: string;
  /** The input the value is given as. */
  variable: 'API_BASE_URL' | 'WEB_ENVIRONMENT';
  /** The value. */
  value: string;
  /** Whether the guard keeps the value. When it does not, it serves the input as empty with one warning. */
  kept: boolean;
}

/**
 * Each input at the guard's limit and one byte over it, in ASCII characters
 * and in two-byte UTF-8 ones (`é`). A limit counted in characters would keep
 * the 1001-byte value of 501 characters.
 */
const GUARD_LENGTH_CASES: readonly GuardLengthCase[] = (['API_BASE_URL', 'WEB_ENVIRONMENT'] as const).flatMap(
  (variable) => [
    { name: `${GUARD_LIMIT_BYTES} ASCII bytes`, variable, value: 'a'.repeat(GUARD_LIMIT_BYTES), kept: true },
    { name: `${GUARD_LIMIT_BYTES + 1} ASCII bytes`, variable, value: 'a'.repeat(GUARD_LIMIT_BYTES + 1), kept: false },
    {
      name: `${GUARD_LIMIT_BYTES} bytes in ${GUARD_LIMIT_BYTES / 2} two-byte characters`,
      variable,
      value: '\u00e9'.repeat(GUARD_LIMIT_BYTES / 2),
      kept: true,
    },
    {
      name: `${GUARD_LIMIT_BYTES + 1} bytes in ${GUARD_LIMIT_BYTES / 2} two-byte characters and one ASCII one`,
      variable,
      value: `${'\u00e9'.repeat(GUARD_LIMIT_BYTES / 2)}a`,
      kept: false,
    },
  ],
);

/** The runtime stage of the web image, as its Dockerfile sets it. */
interface WebRuntime {
  /** The final stage's base image, which serves the SPA. */
  image: string;
  /** The image's `NGINX_ENVSUBST_FILTER`, which limits template substitution to its two inputs. */
  envsubstFilter: string;
  /** Where the image keeps its input guard, a file under `/docker-entrypoint.d/` the entrypoint sources. */
  inputGuardPath: string;
  /** The input guard's text, as the image holds it. */
  inputGuard: string;
}

/**
 * Reads the runtime image, the template filter and the input guard from the
 * web image's Dockerfile, so a render runs what the image runs. The guard is
 * the `.envsh` file the Dockerfile writes into `/docker-entrypoint.d/` from a
 * quoted heredoc; like the Dockerfile, which strips a carriage return from the
 * end of each of its lines, this drops those, so a Windows checkout renders
 * the same guard.
 *
 * @throws Error when the Dockerfile names no stage, no filter or no guard.
 */
function webRuntime(): WebRuntime {
  const dockerfile = readFileSync(join(WEB_WORKSPACE, 'Dockerfile'), 'utf8');
  const stages = [...dockerfile.matchAll(/^FROM\s+(?:--\S+\s+)*(\S+)/gim)].map((stage) => stage[1]);
  const filter = /^ENV\s+NGINX_ENVSUBST_FILTER=(?:'([^']*)'|([^\s'"]+))\s*$/m.exec(dockerfile);
  const guard =
    /^COPY[ \t]+(?:--\S+[ \t]+)*<<'([A-Za-z_]\w*)'[ \t]+(\/docker-entrypoint\.d\/[\w.-]+\.envsh)[ \t]*\r?\n([\s\S]*?)^\1\r?$/m.exec(
      dockerfile,
    );
  if (stages.length === 0 || filter === null || guard === null) {
    throw new Error(
      'The web Dockerfile names no FROM stage, no single-quoted or unquoted NGINX_ENVSUBST_FILTER, or no quoted heredoc COPY of an .envsh file into /docker-entrypoint.d/.',
    );
  }
  return {
    image: stages[stages.length - 1],
    envsubstFilter: filter[1] ?? filter[2],
    inputGuardPath: guard[2],
    inputGuard: guard[3].replace(/\r$/gm, ''),
  };
}

/** The members of the error `execFile` rejects with that a failed render reports. */
interface ExecFailure {
  /** The exit code, or a Node.js error code such as `ENOENT` when `docker` could not run. */
  code?: unknown;
  /** Whether the command was stopped, as it is at its deadline. */
  killed?: unknown;
  /** What the command wrote to stderr. */
  stderr?: unknown;
}

/**
 * Describes a failed render in one message: what was rendered, as `subject`
 * names it, the image, why `docker run` failed, and the last lines of its
 * error output with control characters removed, each prefixed `  docker | `
 * so it cannot pass for this message. A Node.js error code is named only when
 * it has the shape of one. The container's environment holds only the case's
 * fixed values, the image's input guard and the image's own values, and the
 * guard never prints an input, so that output can name nothing else.
 */
function renderFailure(subject: string, image: string, error: unknown): string {
  const failure = (typeof error === 'object' && error !== null ? error : {}) as ExecFailure;
  let reason = 'docker could not be run';
  if (typeof failure.code === 'number') {
    reason = `exit code ${failure.code}`;
  } else if (typeof failure.code === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(failure.code)) {
    reason = `docker could not be run: ${failure.code}`;
  } else if (failure.killed === true) {
    reason = `stopped after ${RENDER_TIMEOUT_MS} ms`;
  }
  const output = typeof failure.stderr === 'string' ? failure.stderr : '';
  const lines = output
    .split(/\r?\n/)
    .map((line) =>
      [...line]
        .filter((ch) => {
          const code = ch.codePointAt(0) ?? 0;
          return code >= 0x20 && (code < 0x7f || code > 0x9f);
        })
        .join(''),
    )
    .filter((line) => line.trim() !== '')
    .slice(-RENDER_OUTPUT_MAX_LINES);
  return [
    `Rendering the nginx template in ${image} for ${subject} failed (${reason}).`,
    ...lines.map((line) => `  docker | ${line}`),
  ].join('\n');
}

/** The two inputs a render starts the web server with. */
interface RenderInputs {
  /** The value of `API_BASE_URL`. */
  apiBaseUrl: string;
  /** The value of `WEB_ENVIRONMENT`. */
  webEnvironment: string;
}

/** The members of `/config.json` the SPA reads. */
interface ServedConfig {
  apiBaseUrl: string;
  environment: string;
}

/** What one render's web server served and its input guard reported. */
interface WebRender {
  /** The one Content-Security-Policy nginx sends for `/`. */
  policy: string;
  /** The raw `/config.json` body nginx serves. */
  configBody: string;
  /** That body, parsed. */
  config: ServedConfig;
  /** Each line the input guard wrote to its error output, in order. */
  guardMessages: string[];
}

/**
 * Returns the text `output` holds between a line `begin` and a line `end`, or
 * `undefined` when it holds no such block.
 */
function outputBlock(output: string, begin: string, end: string): string | undefined {
  const start = output.indexOf(`${begin}\n`);
  const stop = start === -1 ? -1 : output.indexOf(`\n${end}\n`, start + begin.length);
  return stop === -1 ? undefined : output.slice(start + begin.length + 1, stop);
}

/**
 * Starts the web image's runtime image with `inputs`, as a deployment of the
 * web image would, and returns the Content-Security-Policy nginx sends for `/`,
 * the `/config.json` it serves and what the image's input guard reported.
 *
 * The container runs what the image runs: the template filter and the input
 * guard come from the web Dockerfile ({@link webRuntime}), the guard is
 * written where the image keeps it, executable, and the checkout's nginx
 * template is mounted read-only where the image keeps its own. The image's
 * entrypoint then sources its own files and the guard and renders the
 * template, so the guard sees the inputs before the template does, and curl
 * inside the container requests the headers of `/` and the body of
 * `/config.json`. The container publishes no port and has no network but its
 * loopback, and nothing is written on the host.
 *
 * @param subject - What the render is for, as error messages name it; it
 *   carries an input only when that input may be printed.
 * @throws Error when the container fails, outlives {@link RENDER_TIMEOUT_MS},
 *   does not source the guard, or prints no header block or no `/config.json`
 *   object of strings.
 */
async function renderWebServer(inputs: RenderInputs, subject: string): Promise<WebRender> {
  const runtime = webRuntime();
  // --mount reads its value as one CSV record, so the path is a quoted field
  // and a comma or quote in it stays part of the path.
  const templateSource = `"source=${join(WEB_WORKSPACE, 'nginx', 'default.conf.template').replace(/"/g, '""')}"`;
  const script = [
    `printf '%s' "$${INPUT_GUARD_VARIABLE}" > '${runtime.inputGuardPath}'`,
    `chmod 0755 '${runtime.inputGuardPath}'`,
    `unset ${INPUT_GUARD_VARIABLE}`,
    `timeout ${RENDER_ENTRYPOINT_TIMEOUT_S} /docker-entrypoint.sh nginx >&2`,
    `echo '${HEADERS_BEGIN}'`,
    `curl -sS --max-time ${RENDER_REQUEST_TIMEOUT_S} -I http://127.0.0.1/`,
    `echo '${HEADERS_END}'`,
    `echo '${CONFIG_BEGIN}'`,
    `curl -sS --max-time ${RENDER_REQUEST_TIMEOUT_S} http://127.0.0.1/config.json`,
    'echo',
    `echo '${CONFIG_END}'`,
  ].join(' && ');
  let stdout: string;
  let stderr: string;
  try {
    ({ stdout, stderr } = await execFileAsync(
      'docker',
      [
        'run',
        '--rm',
        '--network',
        'none',
        '--env',
        `API_BASE_URL=${inputs.apiBaseUrl}`,
        '--env',
        `WEB_ENVIRONMENT=${inputs.webEnvironment}`,
        '--env',
        `NGINX_ENVSUBST_FILTER=${runtime.envsubstFilter}`,
        '--env',
        `${INPUT_GUARD_VARIABLE}=${runtime.inputGuard}`,
        '--mount',
        `type=bind,${templateSource},target=${TEMPLATE_TARGET},readonly`,
        runtime.image,
        'sh',
        '-c',
        script,
      ],
      { encoding: 'utf8', timeout: RENDER_TIMEOUT_MS, killSignal: 'SIGKILL' },
    ));
  } catch (error) {
    throw new Error(renderFailure(subject, runtime.image, error));
  }

  if (!stderr.split(/\r?\n/).some((line) => line.endsWith(`: Sourcing ${runtime.inputGuardPath}`))) {
    throw new Error(`The render for ${subject} did not source the input guard ${runtime.inputGuardPath}.`);
  }
  const guardPrefix = `${runtime.inputGuardPath.slice(runtime.inputGuardPath.lastIndexOf('/') + 1)}: `;
  const guardMessages = stderr.split(/\r?\n/).filter((line) => line.startsWith(guardPrefix));

  const headers = outputBlock(stdout, HEADERS_BEGIN, HEADERS_END);
  const configBody = outputBlock(stdout, CONFIG_BEGIN, CONFIG_END);
  if (headers === undefined || configBody === undefined) {
    throw new Error(`The render for ${subject} printed no response headers or no /config.json body.`);
  }
  const policies = headers
    .split(/\r?\n/)
    .filter((line) => /^content-security-policy:/i.test(line))
    .map((line) => line.slice(line.indexOf(':') + 1).trim());
  expect(policies, `Content-Security-Policy headers for ${subject}`).toHaveLength(1);

  let parsed: unknown;
  try {
    parsed = JSON.parse(configBody);
  } catch {
    parsed = undefined;
  }
  const served: Record<string, unknown> =
    typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : {};
  const apiBaseUrl = served['apiBaseUrl'];
  const environment = served['environment'];
  if (typeof apiBaseUrl !== 'string' || typeof environment !== 'string') {
    throw new Error(`The render for ${subject} served a /config.json that is not an object with string apiBaseUrl and environment.`);
  }
  return { policy: policies[0], configBody, config: { apiBaseUrl, environment }, guardMessages };
}

/** What the production-policy browser check observed in its page. */
interface PolicyProbe {
  /** The API origin the browser derives from the base URL, as the SPA does. */
  apiOrigin: string;
  /** The outcome of the page's request to the API origin: `HTTP <status>` or `refused`. */
  api: string;
  /** The outcome of the page's request to {@link UNRELATED_ORIGIN}. */
  unrelated: string;
  /** Each policy violation the page reported, as `<directive> <origin of the refused URL>`. */
  violations: string[];
  /** Whether the refusal of the unrelated request was reported within {@link VIOLATION_REPORT_TIMEOUT_MS}. */
  unrelatedReported: boolean;
}

/** Builds an absolute API URL with `query` encoded, so `New York` is safe. */
function apiQueryUrl(path: string, query: Record<string, string>): string {
  return `${apiUrl()}${path}?${new URLSearchParams(query).toString()}`;
}

/**
 * Describes why a search response does not count as a successful result, or
 * returns `undefined` when it is `200` with a non-empty JSON array.
 */
async function searchFailure(res: APIResponse): Promise<string | undefined> {
  if (res.status() !== 200) {
    return `HTTP ${res.status()}`;
  }
  let body: unknown;
  try {
    body = await res.json();
  } catch {
    return 'the body is not JSON';
  }
  return Array.isArray(body) && body.length > 0 ? undefined : 'the body is not a non-empty array';
}

test('stack is healthy and seeded', async ({ page, request: api }) => {
  const health = await api.get(`${apiUrl()}/health`);
  expect(health.status(), 'GET /api/health').toBe(200);
  expect((await health.text()).trim()).toBe('Healthy');

  // Far enough ahead that no other spec books on these dates, so every room
  // of the catalogue is still free.
  const [checkIn, checkOut] = utcDates(180, 181);
  const stay = { checkIn, checkOut, guests: '1' };

  const hotelIds: number[] = [];
  for (const [city, expectedCount] of SEEDED_CITIES) {
    const res = await api.get(apiQueryUrl('/hotels', { city, ...stay }));
    expect(res.status(), `GET /api/hotels for ${city}`).toBe(200);
    const hotels = (await res.json()) as HotelSummary[];
    expect(hotels, `hotels in ${city}`).toHaveLength(expectedCount);
    for (const hotel of hotels) {
      expect(hotel.city, `city of hotel ${hotel.id}`).toBe(city);
      hotelIds.push(hotel.id);
    }
  }
  expect(new Set(hotelIds).size, 'distinct seeded hotels').toBe(SEEDED_HOTEL_COUNT);

  for (const id of hotelIds) {
    const res = await api.get(apiQueryUrl(`/hotels/${id}`, stay));
    expect(res.status(), `GET /api/hotels/${id}`).toBe(200);
    const detail = (await res.json()) as HotelDetail;
    expect(detail.id).toBe(id);
    expect(detail.roomTypes, `room types of hotel ${id}`).toHaveLength(SEEDED_ROOM_TYPE_NAMES.length);
    expect(detail.roomTypes.map((roomType) => roomType.name).sort()).toEqual(SEEDED_ROOM_TYPE_NAMES);
    for (const roomType of detail.roomTypes) {
      expect(roomType.availableRooms, `free ${roomType.name} rooms in hotel ${id}`).toBe(ROOMS_PER_TYPE);
    }
  }

  // The search page renders only once the SPA has loaded /config.json and
  // bootstrapped, so its heading also rules out the static boot-error state.
  await page.goto('/');
  await expect(page).toHaveURL(/\/search$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Find a hotel', exact: true })).toBeVisible();
  await expect(page.getByTestId('boot-error')).toHaveCount(0);
});

// Latency is hardware-dependent: this measures the machine running the
// Compose stack, an accepted risk of the first version.
test('hotel search p95 within 1 second with 20 concurrent users', async () => {
  test.setTimeout(120_000);

  const [checkIn, checkOut] = utcDates(60, 62);
  const url = apiQueryUrl('/hotels', {
    city: 'Seattle',
    checkIn,
    checkOut,
    guests: '2',
  });

  // Contexts are created inside the try, so any created before a failure are
  // still disposed.
  const contexts: APIRequestContext[] = [];
  try {
    for (let user = 0; user < USERS; user += 1) {
      contexts.push(await request.newContext());
    }

    const warmUp = await Promise.all(
      Array.from({ length: WARM_UP }, (_, i) => contexts[i % contexts.length].get(url, NO_REDIRECTS)),
    );
    for (const res of warmUp) {
      expect(res.status(), 'warm-up GET /api/hotels').toBe(200);
    }

    const samples: number[] = [];
    const failures: string[] = [];
    await Promise.all(
      contexts.map(async (ctx, user) => {
        for (let i = 0; i < REQUESTS_PER_USER; i += 1) {
          const start = performance.now();
          const res = await ctx.get(url, NO_REDIRECTS);
          const elapsed = performance.now() - start;
          samples.push(elapsed);
          // Checked outside the timed span, and recorded rather than thrown,
          // so one bad response never cuts the other users' runs short.
          const failure = await searchFailure(res);
          if (failure !== undefined) {
            failures.push(`user ${user + 1}, request ${i + 1}: ${failure}`);
          }
        }
      }),
    );

    expect(failures).toEqual([]);
    expect(samples).toHaveLength(USERS * REQUESTS_PER_USER);

    // Nearest rank: with 500 samples this is the 475th smallest, index 474.
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.ceil(0.95 * sorted.length) - 1];

    console.log(
      `GET /api/hotels p95 = ${p95.toFixed(1)} ms over ${sorted.length} samples (${USERS} users × ${REQUESTS_PER_USER} requests)`,
    );
    test.info().annotations.push({ type: 'p95-ms', description: p95.toFixed(1) });

    expect(p95).toBeLessThanOrEqual(THRESHOLD_MS);
  } finally {
    await Promise.all(contexts.map((ctx) => ctx.dispose()));
  }
});

test('boot-loading shows while /config.json is pending and the search page replaces it once the configuration arrives', async ({
  page,
}) => {
  const config = await holdConfigRequest(page, (route) => route.continue());
  try {
    await page.goto('/search');
    await config.reached;

    const bootLoading = page.getByTestId('boot-loading');
    await expect(bootLoading).toBeVisible();
    await expect(bootLoading).toHaveText(BOOT_LOADING_TEXT);
    await expect(page.getByTestId('boot-error')).toHaveCount(0);
    await expectNoShell(page);
    await expect(page.locator('app-root')).not.toHaveAttribute('ng-version');

    // The stack's own server answers the released request.
    config.release();
    await expect(page.getByRole('heading', { level: 1, name: 'Find a hotel', exact: true })).toBeVisible();
    await expect(bootLoading).toHaveCount(0);
    await expect(page.getByTestId('boot-error')).toHaveCount(0);
    await expect(page.locator('app-root')).toHaveAttribute('ng-version', /^\d+\./);
    await expect(page).toHaveURL(/\/search$/);
  } finally {
    config.release();
    await page.unrouteAll({ behavior: 'wait' });
  }
});

for (const failure of CONFIG_FAILURES) {
  test(`when ${failure.name}, the boot-error alert replaces boot-loading, styled by the host page alone, with no shell, API request or app console output`, async ({
    page,
  }) => {
    const apiOrigin = new URL(apiUrl()).origin;
    const forbiddenRequests: string[] = [];
    page.on('request', (sent) => {
      const url = new URL(sent.url());
      if (url.origin === apiOrigin || url.hostname === REJECTED_API_HOST) {
        forbiddenRequests.push(`${sent.method()} ${url.origin}${url.pathname}`);
      }
    });
    const appConsole = recordAppConsole(page);

    // The global stylesheet never arrives, so the alert's style can come only
    // from the host page.
    let blockedStylesheets = 0;
    await page.route(isGlobalStylesheet, (route) => {
      blockedStylesheets += 1;
      return route.abort();
    });

    const { answer } = failure;
    if (answer === undefined) {
      await page.clock.install({ time: CLOCK_INSTALLED_AT });
      await page.clock.pauseAt(CLOCK_PAUSED_AT);
    }
    const config = await holdConfigRequest(page, answer ?? ((route) => route.abort()));
    try {
      if (answer !== undefined) {
        config.release();
      }
      await page.goto('/search');
      await config.reached;

      if (answer === undefined) {
        // The request is held and the clock is paused, so only the deadline
        // can end the wait: one millisecond short of it the page still shows
        // boot-loading, and the last millisecond aborts the request.
        const bootLoading = page.getByTestId('boot-loading');
        await expect(bootLoading).toBeVisible();
        await expect(bootLoading).toHaveText(BOOT_LOADING_TEXT);
        await page.clock.runFor(CONFIG_DEADLINE_MS - 1);
        await expect(bootLoading).toBeVisible();
        await expect(page.getByTestId('boot-error')).toHaveCount(0);
        await page.clock.runFor(1);
      }

      const alert = page.locator('[data-testid="boot-error"]');
      await expect(alert).toHaveCount(1);
      await expect(alert).toBeVisible();
      await expect(alert).toHaveAttribute('role', 'alert');
      await expect(alert).toHaveText(BOOT_ERROR_TEXT);
      await expect(page.getByRole('alert')).toHaveCount(1);
      for (const [property, value] of BOOT_ERROR_STYLE) {
        await expect(alert).toHaveCSS(property, value);
      }
      expect(blockedStylesheets, 'global stylesheet requests blocked').toBeGreaterThan(0);

      await expect(page.getByTestId('boot-loading')).toHaveCount(0);
      await expectNoShell(page);
      await expect(page.locator('app-root')).not.toHaveAttribute('ng-version');
      expect(forbiddenRequests, 'requests to the API or the rejected host').toEqual([]);
      expect(appConsole, 'app console output').toEqual([]);
    } finally {
      config.release();
      await page.unrouteAll({ behavior: 'wait' });
    }
  });
}

for (const target of SECURITY_HEADER_CASES) {
  test(`security headers on ${target.name}: framing denied, nosniff, no-referrer and the Content-Security-Policy, with the location's own Cache-Control unchanged and no server version`, async ({
    request: web,
  }) => {
    const path = typeof target.path === 'string' ? target.path : await target.path(web);
    const response = await web.get(path, NO_REDIRECTS);
    expect(response.status(), `GET ${path}`).toBe(target.status);

    for (const [name, value] of SECURITY_HEADERS) {
      expect(singleHeader(response, name), name).toBe(value);
    }

    const policy = cspDirectives(singleHeader(response, 'content-security-policy'));
    for (const [name, sources] of FIXED_CSP_DIRECTIVES) {
      expect(policy.get(name), `CSP ${name}`).toEqual(sources);
    }
    expect(policy.get('connect-src'), 'CSP connect-src').toEqual(["'self'", new URL(apiUrl()).origin]);
    const scriptSources = policy.get('script-src') ?? [];
    expect(scriptSources, 'CSP script-src').toContain("'self'");
    expect(
      scriptSources.filter((source) => !ALLOWED_SCRIPT_SOURCE.test(source)),
      'CSP script-src sources other than self, unsafe-hashes and SHA-256 hashes',
    ).toEqual([]);

    expect(response.headers()['cache-control'], 'Cache-Control').toBe(target.cacheControl);
    expect(response.headers()['server'] ?? '', 'Server header').not.toMatch(/\d/);
    expect(await response.text(), 'body').not.toMatch(/nginx\/\d/);
  });
}

test('security headers in the browser: under the Content-Security-Policy the shell boots, applies the global stylesheet and reports no violation', async ({
  page,
}) => {
  // Registered before any page script runs, so a violation while the shell
  // is parsed, such as a blocked stylesheet load handler, is recorded too.
  await page.addInitScript(() => {
    const violations: string[] = [];
    (window as unknown as CspViolationLog).smarthotelCspViolations = violations;
    document.addEventListener('securitypolicyviolation', (event) => {
      violations.push(`${event.effectiveDirective}: ${event.blockedURI || 'inline code'}`);
    });
  });
  const appConsole = recordAppConsole(page);

  const shell = await page.goto('/');
  expect(shell?.status(), 'GET / for the shell').toBe(200);
  expect(shell?.headers()['content-security-policy'] ?? '', "the shell's Content-Security-Policy").toContain(
    "frame-ancestors 'none'",
  );

  // The search page renders only once the module bundles have run and
  // /config.json has loaded, which script-src and connect-src must allow.
  await expect(page).toHaveURL(/\/search$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Find a hotel', exact: true })).toBeVisible();

  // The build loads the global stylesheet as media="print" and lets its
  // inline load handler switch it to all, which the policy allows by hash.
  const stylesheet = page.locator('head > link[rel="stylesheet"]');
  await expect(stylesheet).toHaveCount(1);
  await expect(stylesheet).toHaveAttribute('href', /^styles-[A-Z0-9]+\.css$/);
  await expect(stylesheet).toHaveAttribute('media', 'all');

  const violations = await page.evaluate(
    () => (window as unknown as CspViolationLog).smarthotelCspViolations ?? null,
  );
  expect(violations, 'policy violations the page reported').toEqual([]);
  expect(appConsole, 'console output').toEqual([]);
});

/** What the production-policy test name states for each outcome. */
const PRODUCTION_ORIGIN_OUTCOME_TITLES: Readonly<Record<ProductionOriginCase['outcome'], string>> = {
  admitted: 'connect-src admits the API origin, which Chromium matches',
  'served as given': "it is served as given and connect-src is 'self' alone",
  'served as empty': "the input guard serves it as empty with one warning and connect-src is 'self' alone",
};

for (const target of PRODUCTION_ORIGIN_CASES) {
  test(`production Content-Security-Policy for API_BASE_URL ${JSON.stringify(target.apiBaseUrl)}, ${target.name}: ${PRODUCTION_ORIGIN_OUTCOME_TITLES[target.outcome]}, and a request to an unrelated origin is refused`, async ({
    page,
  }) => {
    test.setTimeout(RENDER_TIMEOUT_MS + 30_000);
    const admitted = target.outcome === 'admitted';

    const render = await renderWebServer(
      { apiBaseUrl: target.apiBaseUrl, webEnvironment: 'Production' },
      `API_BASE_URL ${JSON.stringify(target.apiBaseUrl)}`,
    );
    if (target.outcome === 'served as empty') {
      expect(render.guardMessages, 'input guard warnings').toHaveLength(1);
      const [warning] = render.guardMessages;
      expect(warning, 'the input guard warning').toMatch(NON_ASCII_HOST_WARNING);
      expect(warning.includes(target.apiBaseUrl.split('/')[2]), 'the warning quotes the host').toBe(false);
      expect(render.config.apiBaseUrl, "/config.json's apiBaseUrl").toBe('');
    } else {
      expect(render.guardMessages, 'input guard warnings').toEqual([]);
    }
    const renderedPolicy = render.policy;
    const policy = cspDirectives(renderedPolicy);
    for (const [name, sources] of FIXED_CSP_DIRECTIVES) {
      expect(policy.get(name), `CSP ${name}`).toEqual(sources);
    }
    const connectSources = policy.get('connect-src') ?? [];
    expect(connectSources, 'CSP connect-src').toHaveLength(admitted ? 2 : 1);
    expect(connectSources[0], "CSP connect-src's first source").toBe("'self'");

    // The page is served under the rendered policy, and every request the
    // browser sends is answered here, so nothing leaves it.
    const reached: string[] = [];
    await page.route(
      () => true,
      async (route) => {
        const url = new URL(route.request().url());
        if (url.origin === POLICY_PAGE_ORIGIN) {
          await route.fulfill(
            url.pathname === '/'
              ? {
                  status: 200,
                  contentType: 'text/html',
                  headers: { 'content-security-policy': renderedPolicy },
                  body: '<!doctype html><title>SmartHotel policy check</title>',
                }
              : { status: 404 },
          );
          return;
        }
        reached.push(url.origin);
        await route.fulfill({
          status: 200,
          contentType: 'text/plain',
          headers: { 'access-control-allow-origin': POLICY_PAGE_ORIGIN },
          body: 'Healthy',
        });
      },
    );
    try {
      await page.goto(`${POLICY_PAGE_ORIGIN}/`);
      const probe = await page.evaluate(
        async ({ apiBaseUrl, unrelatedOrigin, reportTimeoutMs }): Promise<PolicyProbe> => {
          // The origin the SPA calls: it parses the base URL the same way.
          const apiOrigin = new URL(apiBaseUrl).origin;
          const violations: string[] = [];
          let markReported = (): void => undefined;
          const reported = new Promise<boolean>((resolve) => {
            markReported = () => resolve(true);
          });
          document.addEventListener('securitypolicyviolation', (event) => {
            const origin = /^https?:/i.test(event.blockedURI) ? new URL(event.blockedURI).origin : event.blockedURI;
            violations.push(`${event.effectiveDirective} ${origin}`);
            if (origin === unrelatedOrigin) {
              markReported();
            }
          });
          const attempt = async (url: string): Promise<string> => {
            try {
              return `HTTP ${(await fetch(url)).status}`;
            } catch {
              return 'refused';
            }
          };
          const api = await attempt(`${apiOrigin}/api/health`);
          const unrelated = await attempt(`${unrelatedOrigin}/api/health`);
          // The page reports violations in the order the browser refuses the
          // requests, so once the unrelated request's report is in, a report
          // for the API request would be in as well.
          const unrelatedReported = await Promise.race([
            reported,
            new Promise<boolean>((resolve) => setTimeout(() => resolve(false), reportTimeoutMs)),
          ]);
          return { apiOrigin, api, unrelated, violations: [...violations], unrelatedReported };
        },
        {
          apiBaseUrl: target.apiBaseUrl,
          unrelatedOrigin: UNRELATED_ORIGIN,
          reportTimeoutMs: VIOLATION_REPORT_TIMEOUT_MS,
        },
      );

      expect(probe.unrelatedReported, `the page reported the refused request to ${UNRELATED_ORIGIN}`).toBe(true);
      expect(probe.unrelated, `the request to ${UNRELATED_ORIGIN}`).toBe('refused');
      expect(probe.api, `the request to the API origin ${probe.apiOrigin}`).toBe(admitted ? 'HTTP 200' : 'refused');
      const refusedOrigins = admitted ? [UNRELATED_ORIGIN] : [probe.apiOrigin, UNRELATED_ORIGIN];
      expect(probe.violations, 'policy violations the page reported').toEqual(
        refusedOrigins.map((origin) => `connect-src ${origin}`),
      );
      expect(reached, 'origins the page reached').toEqual(admitted ? [probe.apiOrigin] : []);
      if (target.outcome === 'admitted') {
        // The API source is exactly the origin the browser calls, in whatever
        // form the base URL wrote it, and /config.json serves the base URL
        // with that same origin spelled the same way and the rest as given,
        // so the SPA calls the origin the policy names.
        expect(connectSources[1], "CSP connect-src's API source").toBe(probe.apiOrigin);
        expect(render.config.apiBaseUrl, "/config.json's apiBaseUrl").toBe(target.served);
        expect(new URL(render.config.apiBaseUrl).origin, "the origin of /config.json's apiBaseUrl").toBe(
          connectSources[1],
        );
        expect(
          render.config.apiBaseUrl.slice(0, connectSources[1].length + 1),
          "the start of /config.json's apiBaseUrl",
        ).toBe(`${connectSources[1]}/`);
      } else if (target.outcome === 'served as given') {
        // A host no source expression can name, or a user name, is served
        // exactly as it was given.
        expect(render.config.apiBaseUrl, "/config.json's apiBaseUrl").toBe(target.apiBaseUrl);
      }
    } finally {
      await page.unrouteAll({ behavior: 'wait' });
    }
  });
}

test('a percent-encoded spelling of the API host in Development: the image serves the canonical origin in /config.json and connect-src, and a search in the browser reaches the API', async ({
  page,
  context,
  baseURL,
}) => {
  test.setTimeout(RENDER_TIMEOUT_MS + 60_000);
  if (baseURL === undefined) {
    throw new Error('The run configures no baseURL for the web app.');
  }
  const webOrigin = new URL(baseURL).origin;

  // The target API with the first character of its host percent-encoded, as
  // http://%6cocalhost:8080/api spells http://localhost:8080/api: the SPA
  // accepts it in Development, and the browser decodes it to the same origin.
  const api = new URL(apiUrl());
  const first = api.hostname.charAt(0);
  expect(/^[a-z0-9]$/.test(first), `the API host ${api.hostname} starts with a letter or digit`).toBe(true);
  const spelling = `${api.protocol}//%${first.charCodeAt(0).toString(16)}${api.hostname.slice(1)}${api.port === '' ? '' : `:${api.port}`}${api.pathname}`;
  expect(new URL(spelling).origin, `the origin of ${spelling}`).toBe(api.origin);

  const render = await renderWebServer(
    { apiBaseUrl: spelling, webEnvironment: 'Development' },
    `API_BASE_URL ${JSON.stringify(spelling)}`,
  );
  expect(render.guardMessages, 'input guard warnings').toEqual([]);
  expect(render.config, '/config.json').toEqual({ apiBaseUrl: `${api.origin}${api.pathname}`, environment: 'Development' });
  expect(cspDirectives(render.policy).get('connect-src'), 'CSP connect-src').toEqual(["'self'", api.origin]);

  // The running web server answers every request but two kinds, which the
  // render decides: each document is sent with the rendered policy in place
  // of its own, and /config.json with the rendered body. Chromium places a
  // document the test answers in the public address space, whatever its
  // origin, and refuses its requests to a loopback API unless the page may
  // reach the local network; the permission restores what the stack's own
  // loopback document may do, and the policy still decides every request.
  await context.grantPermissions(['local-network-access'], { origin: webOrigin });
  await page.route(
    (url) => url.origin === webOrigin,
    async (route) => {
      if (isConfigRequest(new URL(route.request().url()))) {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          headers: { 'cache-control': 'no-store' },
          body: render.configBody,
        });
      } else if (route.request().resourceType() === 'document') {
        const response = await route.fetch();
        await route.fulfill({
          response,
          headers: { ...response.headers(), 'content-security-policy': render.policy },
        });
      } else {
        await route.continue();
      }
    },
  );
  await page.addInitScript(() => {
    const violations: string[] = [];
    (window as unknown as CspViolationLog).smarthotelCspViolations = violations;
    document.addEventListener('securitypolicyviolation', (event) => {
      violations.push(`${event.effectiveDirective}: ${event.blockedURI || 'inline code'}`);
    });
  });
  const appConsole = recordAppConsole(page);
  const searches: string[] = [];
  page.on('response', (response) => {
    if (isApiEndpoint(response.url(), '/hotels') && response.request().method() === 'GET') {
      searches.push(`HTTP ${response.status()}`);
    }
  });

  try {
    const shell = await page.goto('/search');
    expect(shell?.headers()['content-security-policy'], "the shell's Content-Security-Policy").toBe(render.policy);
    await expect(page.getByRole('heading', { level: 1, name: 'Find a hotel', exact: true })).toBeVisible();

    // Far enough ahead that both Seattle hotels have rooms free.
    const [checkIn, checkOut] = utcDates(180, 182);
    await fillBound(page, 'search-city', 'Seattle');
    await fillBound(page, 'search-check-in', checkIn);
    await fillBound(page, 'search-check-out', checkOut);
    await fillBound(page, 'search-guests', '1');
    await page.getByTestId('search-submit').click();
    await expect(
      page
        .locator('[data-testid^="hotel-result-"], [data-testid="search-empty"], [data-testid="inline-error"]')
        .first(),
    ).toBeVisible();

    const violations = await page.evaluate(
      () => (window as unknown as CspViolationLog).smarthotelCspViolations ?? null,
    );
    expect(violations, 'policy violations the page reported').toEqual([]);
    await expect(page.getByTestId('inline-error')).toHaveCount(0);
    await expect(page.locator('[data-testid^="hotel-result-"]')).toHaveCount(2);
    expect(searches, `GET /api/hotels responses from ${api.origin}`).toEqual(['HTTP 200']);
    expect(appConsole, 'app console output').toEqual([]);
  } finally {
    await page.unrouteAll({ behavior: 'wait' });
  }
});

for (const target of GUARD_LENGTH_CASES) {
  test(`the input guard ${target.kept ? 'keeps' : 'serves as empty, with one warning naming the variable and the byte limit,'} ${target.variable} of ${target.name}`, async () => {
    test.setTimeout(RENDER_TIMEOUT_MS + 30_000);

    const checksApiBaseUrl = target.variable === 'API_BASE_URL';
    const render = await renderWebServer(
      checksApiBaseUrl
        ? { apiBaseUrl: target.value, webEnvironment: 'Production' }
        : { apiBaseUrl: GUARD_API_BASE_URL, webEnvironment: target.value },
      `the input-guard case ${target.variable} of ${target.name}`,
    );

    // Each check that involves the value compares inside the test and
    // reports only its outcome, so no failure prints the value.
    expect(render.guardMessages.length, 'input guard warnings').toBe(target.kept ? 0 : 1);
    if (!target.kept) {
      const [warning] = render.guardMessages;
      expect(
        new RegExp(`: ${target.variable} is longer than ${GUARD_LIMIT_BYTES} bytes\\b`).test(warning),
        `the warning names ${target.variable} and the ${GUARD_LIMIT_BYTES}-byte limit`,
      ).toBe(true);
      expect(warning.includes(target.value.slice(0, 16)), 'the warning quotes the value').toBe(false);
    }
    if (checksApiBaseUrl) {
      expect(
        render.config.apiBaseUrl === (target.kept ? target.value : ''),
        target.kept ? "/config.json's apiBaseUrl is the value" : "/config.json's apiBaseUrl is empty",
      ).toBe(true);
    } else {
      expect(render.config.apiBaseUrl, "/config.json's apiBaseUrl").toBe(GUARD_API_BASE_URL);
    }
    // Only Development relaxes the SPA's rules, so every other value, kept or
    // cleared, is served as Production.
    expect(render.config.environment, "/config.json's environment").toBe('Production');
  });
}
