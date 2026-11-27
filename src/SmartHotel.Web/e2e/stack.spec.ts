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
 * API requests use absolute URLs from `apiUrl()`, because the configured
 * `baseURL` is the web origin.
 */
import { test, expect, request, type APIRequestContext, type APIResponse } from '@playwright/test';

import { apiUrl, utcDate } from './support/helpers';

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
  const stay = { checkIn: utcDate(180), checkOut: utcDate(181), guests: '1' };

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

  const url = apiQueryUrl('/hotels', {
    city: 'Seattle',
    checkIn: utcDate(60),
    checkOut: utcDate(62),
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
