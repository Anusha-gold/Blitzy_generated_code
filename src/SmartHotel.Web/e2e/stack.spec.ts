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
 * API requests use absolute URLs from `apiUrl()`, because the configured
 * `baseURL` is the web origin.
 */
import {
  test,
  expect,
  request,
  type APIRequestContext,
  type APIResponse,
  type Page,
  type Route,
} from '@playwright/test';

import { apiUrl, utcDates } from './support/helpers';

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

/** The number of seeded hotels across all cities. */
const SEEDED_HOTEL_COUNT = 5;

/** The room type names every seeded hotel offers, sorted. */
const SEEDED_ROOM_TYPE_NAMES = ['Deluxe', 'Family', 'Standard', 'Suite'];

/** The number of rooms seeded for every room type. */
const ROOMS_PER_TYPE = 10;

/** Virtual users in the latency run, each with its own request context. */
const USERS = 20;

/** Sequential measured requests each virtual user sends. */
const REQUESTS_PER_USER = 25;

/** Warm-up requests sent before measuring; their timings are discarded. */
const WARM_UP = 20;

/** The highest acceptable 95th-percentile latency. */
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
