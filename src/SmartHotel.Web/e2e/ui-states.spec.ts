/**
 * The web app's request states (R6) and both branches of the 401 rule,
 * exercised in Chromium against the stack at `baseURL`.
 *
 * R6: the loading indicator while a request is pending, the empty-result
 * message, inline errors for 400, 409 and 401, and the redirect to login when
 * a protected page is opened without a valid session.
 *
 * The 401 rule: a token the server rejects before its local expiry cannot be
 * caught by the route guard. These tests make the real API reject one by
 * rewriting the `Authorization` header of the app's own bookings requests,
 * so the 401 comes from the API's JwtBearer validation, not from a stub.
 * Entering My bookings must then redirect to login with the session-expired
 * notice, and "Book" must stay on the hotel page with the 401 shown inline
 * and a "Log in again" link. In both branches the auth interceptor has
 * cleared the session, so the nav bar offers "Log in" again.
 *
 * URL criteria: the search and hotel detail pages send the text their URL
 * holds, so the server's own binding judges a deep link such as a guests-only
 * search, `guests=2.0`, `guests=2e0` or an empty `guests`, and its 400 shows
 * inline. A hotel id that is not an integer literal, such as `1.0`, `abc` or a
 * NUL reached through the login page's return URL, matches no API route, so
 * the detail page shows the API's 404 inline without sending a request. A
 * detail page reused by one history traversal that changes both its hotel and
 * its stay loads once, for the hotel and stay it lands on.
 *
 * Session expiry: with the browser's clock faked and moved past the token's
 * 60 minutes, the nav bar must show the guest signed out on the public page
 * they stayed on, without the app sending any API request, and focus left on
 * "Log out" must move to the "Log in" link that replaces it.
 *
 * Every test runs in a fresh browser context and therefore starts signed
 * out, because the session lives only in memory.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';

import {
  TEST_PASSWORD,
  expectNoInlineError,
  // Some tests fill forms field by field: the form helpers also wait for
  // their success outcome, which these tests must not wait for (a delayed
  // search) or expect never to happen (a rejected registration or login).
  fillBound,
  logIn,
  registerGuest,
  searchHotels,
  uniqueEmail,
  utcDates,
} from './support/helpers';

/**
 * The hotel search, `GET /api/hotels?…`, and never the detail
 * `/api/hotels/{id}`. A predicate rather than a glob: Playwright globs match
 * `?` literally and never let `*` cross a `/`.
 */
const isHotelSearch = (url: URL): boolean => url.pathname === '/api/hotels';

/**
 * The bookings endpoints: the list, "Book" and "Cancel". The SPA's own
 * `/bookings` page never matches, because its path has no `/api` prefix.
 */
const isBookingsApi = (url: URL): boolean =>
  url.pathname === '/api/bookings' || url.pathname.startsWith('/api/bookings/');

/** The registration endpoint, `/api/auth/register`, never the SPA's `/register` page. */
const isRegisterApi = (url: URL): boolean => url.pathname === '/api/auth/register';

/** The login endpoint, `/api/auth/login`, never the SPA's `/login` page. */
const isLoginApi = (url: URL): boolean => url.pathname === '/api/auth/login';

/** Any API endpoint. The SPA's own routes never match, because none has an `/api` prefix. */
const isApi = (url: URL): boolean => url.pathname === '/api' || url.pathname.startsWith('/api/');

/** Every hotel card on the search page. */
const HOTEL_RESULTS = '[data-testid^="hotel-result-"]';

/** Every "View rooms" link on the search page. */
const VIEW_ROOMS = '[data-testid^="view-rooms-"]';

/**
 * A deep link whose criteria only the server can judge: the page must send
 * the URL's text unchanged and show the server's answer inline.
 */
interface RawCriteriaCase {
  /** What the URL holds, for the test title. */
  title: string;
  /** The page URL; `{stay}` stands for a valid `checkIn=…&checkOut=…` query. */
  url: string;
  /** The path the page's `GET` must request, the hotel id included. */
  apiPath: string;
  /** The `guests` text that `GET` must carry: the URL's own, or `1` when it has none. */
  guests: string;
  /** The status the server answers. */
  status: number;
}

/** The deep links of the raw-criteria test, one test each. */
const RAW_CRITERIA_CASES: readonly RawCriteriaCase[] = [
  {
    title: 'a search URL with only guests',
    url: '/search?guests=2',
    apiPath: '/api/hotels',
    guests: '2',
    status: 400,
  },
  {
    title: 'a search URL with guests=2.0',
    url: '/search?city=Seattle&{stay}&guests=2.0',
    apiPath: '/api/hotels',
    guests: '2.0',
    status: 400,
  },
  {
    title: 'a search URL with an empty guests',
    url: '/search?city=Seattle&{stay}&guests=',
    apiPath: '/api/hotels',
    guests: '',
    status: 400,
  },
  {
    title: 'a detail URL with guests=2e0',
    url: '/hotels/1?{stay}&guests=2e0',
    apiPath: '/api/hotels/1',
    guests: '2e0',
    status: 400,
  },
  {
    title: 'a detail URL with an empty guests',
    url: '/hotels/1?{stay}&guests=',
    apiPath: '/api/hotels/1',
    guests: '',
    status: 400,
  },
];

/**
 * Hotel ids a deep link can carry that are not integer literals, one test
 * each. The API's `{id:int}` route matches none of them.
 */
const NON_INTEGER_HOTEL_IDS: readonly string[] = ['1.0', 'abc'];

/**
 * Records, from now on, every request the page sends to a hotel detail path,
 * `/api/hotels/{id}`, whatever its method, and returns the list it fills.
 */
function recordDetailRequests(page: Page): URL[] {
  const requested: URL[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/hotels/')) {
      requested.push(url);
    }
  });
  return requested;
}

/**
 * Asserts the hotel detail page's answer to a non-integer id: the 404 the API
 * gives such an id is shown inline in place of the content, the page does not
 * report the service as unreachable, and `detailRequests` stayed empty.
 */
async function expectLocal404(page: Page, detailRequests: readonly URL[]): Promise<void> {
  const error = page.getByTestId('inline-error');
  await expect(error).toBeVisible();
  await expect(error).toContainText('404');
  await expect(error).not.toContainText('The service is unreachable.');
  await expect(page.getByTestId('hotel-name')).toHaveCount(0);
  expect(detailRequests.map((u) => u.href)).toEqual([]);
}

/**
 * Clicks a "View rooms" link and waits until the hotel detail page shows the
 * hotel it opens. Returns that hotel's id, read from the link's
 * `view-rooms-<id>` hook, and the name the page shows.
 */
async function openHotel(page: Page, link: Locator): Promise<{ id: string; name: string }> {
  const testId = (await link.getAttribute('data-testid')) ?? '';
  expect(testId).toMatch(/^view-rooms-\d+$/);
  const id = testId.slice('view-rooms-'.length);

  await link.click();
  await expect(page).toHaveURL(new RegExp(`/hotels/${id}\\?`));
  const name = page.getByTestId('hotel-name');
  await expect(name).toHaveText(/\S/);
  return { id, name: (await name.innerText()).trim() };
}

/**
 * Opens the register page, registers a fresh guest and returns the email.
 * The browser is left on `/login?registered=1`.
 */
async function registerNewGuest(page: Page): Promise<string> {
  const email = uniqueEmail();
  await page.goto('/register');
  await registerGuest(page, { email, password: TEST_PASSWORD, fullName: 'States Guest' });
  return email;
}

/**
 * Registers a fresh guest and logs in. With no `returnUrl`, the login page
 * continues to `/search`, so the browser is left there, signed in.
 */
async function registerAndLogIn(page: Page): Promise<void> {
  const email = await registerNewGuest(page);
  await logIn(page, email, TEST_PASSWORD);
  await expect(page).toHaveURL(/\/search$/);
}

/**
 * From now on, sends every bookings API request with a token the API cannot
 * validate, while the app still holds its unexpired session. The guard
 * therefore admits the page and the app sends the request; only the server
 * rejects it.
 */
async function rejectBookingsToken(page: Page): Promise<void> {
  await page.route(isBookingsApi, (route) =>
    route.continue({
      headers: { ...route.request().headers(), authorization: 'Bearer not-a-valid-token' },
    }),
  );
}

test('loading indicator is visible while a request is pending', async ({ page }) => {
  // Holding the search response open keeps the request pending long enough to
  // observe the indicator, which otherwise shows only for a few milliseconds.
  await page.route(isHotelSearch, async (route) => {
    await new Promise<void>((resolve) => setTimeout(resolve, 1500));
    await route.continue();
  });

  const [checkIn, checkOut] = utcDates(30, 32);
  await page.goto('/search');
  await fillBound(page, 'search-city', 'Seattle');
  await fillBound(page, 'search-check-in', checkIn);
  await fillBound(page, 'search-check-out', checkOut);
  await fillBound(page, 'search-guests', '2');

  const indicator = page.getByTestId('loading-indicator');
  await expect(indicator).toHaveCount(0);

  await page.getByTestId('search-submit').click();
  await expect(indicator).toBeVisible();
  await expect(indicator).toHaveAttribute('role', 'progressbar');

  await expect(indicator).toBeHidden({ timeout: 10_000 });
  await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
  // The pending state disabled the focused button; focus returns to it.
  await expect(page.getByTestId('search-submit')).toBeFocused();
});

test('search with check-out equal to check-in shows an inline 400', async ({ page }) => {
  await page.goto('/search');
  const response = page.waitForResponse(
    (r) => isHotelSearch(new URL(r.url())) && r.request().method() === 'GET',
  );
  const [day] = utcDates(30);
  await searchHotels(page, { city: 'Seattle', checkIn: day, checkOut: day, guests: 1 });
  expect((await response).status()).toBe(400);

  const error = page.getByTestId('inline-error');
  await expect(error).toBeVisible();
  await expect(error.getByRole('alert').locator('strong')).toHaveText(/\S/);
  await expect(error).toContainText('checkOut must be after checkIn.');
  await expect(page.locator(HOTEL_RESULTS)).toHaveCount(0);

  // Only the rejected input is marked invalid, described by its own message.
  const checkOut = page.getByTestId('search-check-out');
  await expect(checkOut).toHaveAttribute('aria-invalid', 'true');
  await expect(checkOut).toHaveAccessibleDescription('checkOut must be after checkIn.');
  await expect(page.getByTestId('search-check-in')).not.toHaveAttribute('aria-invalid');
});

test('search with no availability shows the empty message', async ({ page }) => {
  const [checkIn, checkOut] = utcDates(30, 32);
  await page.goto('/search');
  await searchHotels(page, { city: 'Atlantis', checkIn, checkOut, guests: 1 });

  await expect(page.getByTestId('search-empty')).toHaveText(
    'No hotels with available rooms in Atlantis for these dates.',
  );
  // The message sits in the page's polite status region, so it is announced.
  await expect(page.getByRole('status').filter({ has: page.getByTestId('search-empty') })).toHaveCount(1);
  await expectNoInlineError(page);
});

for (const c of RAW_CRITERIA_CASES) {
  test(`${c.title} reaches the server as written and its ${c.status} shows inline`, async ({ page }) => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const url = c.url.replace('{stay}', `checkIn=${checkIn}&checkOut=${checkOut}`);

    const res = await test.step('open the deep link', async () => {
      const response = page.waitForResponse(
        (r) => new URL(r.url()).pathname === c.apiPath && r.request().method() === 'GET',
      );
      await page.goto(url);
      return response;
    });

    await test.step(`the GET carries the URL's text and gets ${c.status}`, () => {
      expect(new URL(res.url()).searchParams.get('guests')).toBe(c.guests);
      expect(res.status()).toBe(c.status);
    });

    await test.step('the page shows the error inline and no hotel', async () => {
      await expect(page.getByTestId('inline-error')).toBeVisible();
      await expect(page.locator(`${HOTEL_RESULTS}, [data-testid="hotel-name"]`)).toHaveCount(0);
    });
  });
}

for (const id of NON_INTEGER_HOTEL_IDS) {
  test(`the hotel id ${id} shows an inline 404 without a request`, async ({ page }) => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const detailRequests = recordDetailRequests(page);
    await page.goto(`/hotels/${id}?checkIn=${checkIn}&checkOut=${checkOut}`);
    await expectLocal404(page, detailRequests);
  });
}

test('the hotel id NUL from the login return URL shows an inline 404 without a request', async ({ page }) => {
  const email = await test.step('register a guest', () => registerNewGuest(page));

  // nginx rejects /hotels/%00 on a direct load, so the in-app navigation
  // after login, which accepts any same-app path, is the way to reach it.
  const detailRequests = recordDetailRequests(page);
  await test.step('log in with a return URL to the hotel id %00', async () => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const returnUrl = `/hotels/%00?checkIn=${checkIn}&checkOut=${checkOut}`;
    await page.goto(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
    await logIn(page, email, TEST_PASSWORD);
    await expect(page).toHaveURL(/\/hotels\/%00\?/);
  });

  await test.step('the page shows the 404 inline and sent no request', () =>
    expectLocal404(page, detailRequests),
  );
});

test('a reused detail page loads once, for the hotel and stay it lands on', async ({ page }) => {
  const [firstIn, firstOut, secondIn, secondOut] = utcDates(40, 42, 44, 47);
  const firstStay = { city: 'Seattle', checkIn: firstIn, checkOut: firstOut, guests: 1 };
  const secondStay = { city: 'Seattle', checkIn: secondIn, checkOut: secondOut, guests: 2 };

  const first = await test.step('open a Seattle hotel for the first stay', async () => {
    await page.goto('/search');
    await searchHotels(page, firstStay);
    return openHotel(page, page.locator(VIEW_ROOMS).first());
  });

  await test.step('open the other Seattle hotel for a second stay', async () => {
    await page.getByTestId('nav-search').click();
    await expect(page).toHaveURL(/\/search$/);
    await searchHotels(page, secondStay);
    const other = page.locator(`${VIEW_ROOMS}:not([data-testid="view-rooms-${first.id}"])`).first();
    await openHotel(page, other);
  });

  await test.step('go back to the first hotel in one history traversal', async () => {
    const loads: URL[] = [];
    page.on('request', (request) => {
      const requested = new URL(request.url());
      if (requested.pathname.startsWith('/api/hotels/') && request.method() === 'GET') {
        loads.push(requested);
      }
    });

    // The history holds /search, /search?<first stay>, /hotels/<first>,
    // /search, /search?<second stay> and /hotels/<other>. Three entries back
    // is the first hotel: one navigation that reuses the detail page and
    // changes both its id and its query.
    await page.evaluate(() => history.go(-3));
    await expect(page).toHaveURL(new RegExp(`/hotels/${first.id}\\?`));
    await expect(page.getByTestId('hotel-name')).toHaveText(first.name);

    expect(loads.map((u) => u.pathname)).toEqual([`/api/hotels/${first.id}`]);
    const [load] = loads;
    expect(load.searchParams.get('checkIn')).toBe(firstStay.checkIn);
    expect(load.searchParams.get('checkOut')).toBe(firstStay.checkOut);
    expect(load.searchParams.get('guests')).toBe(String(firstStay.guests));
  });
});

test('duplicate registration shows an inline 409', async ({ page }) => {
  const email = await test.step('register a guest', () => registerNewGuest(page));

  await test.step('register the same email again', async () => {
    await page.getByTestId('register-link').click();
    await expect(page).toHaveURL(/\/register$/);

    // The API folds the email to lower case before comparing, so the same
    // address in other letter case is still a duplicate.
    await fillBound(page, 'register-email', email.replace(/^[^@]+/, (local) => local.toUpperCase()));
    await fillBound(page, 'register-password', TEST_PASSWORD);
    await fillBound(page, 'register-full-name', 'States Guest');
    // Any other failing status with the same problem body shows the same
    // message through its `detail`, so only the response itself proves the
    // API answered 409. The method filter keeps a CORS preflight from matching.
    const registration = page.waitForResponse(
      (r) => isRegisterApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await page.getByTestId('register-submit').click();
    expect((await registration).status()).toBe(409);
  });

  await test.step('stay on the register page with the 409 inline', async () => {
    await expect(page.getByTestId('inline-error')).toContainText(
      'An account with this email already exists.',
    );
    await expect(page).toHaveURL(/\/register$/);
  });
});

test('wrong password shows an inline 401', async ({ page }) => {
  const email = await test.step('register a guest', () => registerNewGuest(page));

  await test.step('log in with a wrong password', async () => {
    await fillBound(page, 'login-email', email);
    await fillBound(page, 'login-password', `${TEST_PASSWORD}x`);
    // As for registration, the inline text alone cannot tell a 401 from
    // another failing status with the same problem body.
    const login = page.waitForResponse(
      (r) => isLoginApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await page.getByTestId('login-submit').click();
    expect((await login).status()).toBe(401);
  });

  await test.step('stay on the login page with the 401 inline, signed out', async () => {
    await expect(page.getByTestId('inline-error')).toContainText('Email or password is incorrect.');
    await expect(page).toHaveURL(/\/login(?:\?|$)/);
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });
});

test('opening My bookings while signed out redirects to login', async ({ page }) => {
  await page.goto('/search');
  await page.getByTestId('nav-bookings').click();

  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Log in');
});

test('server 401 on entering My bookings redirects with session-expired', async ({ page }) => {
  await test.step('register and log in', () => registerAndLogIn(page));

  await test.step('open My bookings with a token the API rejects', async () => {
    await rejectBookingsToken(page);
    const list = page.waitForResponse(
      (r) => isBookingsApi(new URL(r.url())) && r.request().method() === 'GET',
    );
    await page.getByTestId('nav-bookings').click();
    expect((await list).status()).toBe(401);
  });

  await test.step('land on login with the session-expired notice, signed out', async () => {
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings&reason=session-expired$/);
    await expect(page.getByTestId('login-notice')).toHaveText('Your session has expired. Log in again.');
    await expect(page.getByTestId('nav-login')).toHaveText('Log in');
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });
});

test('server 401 on Book stays on the page with an inline error', async ({ page }) => {
  await test.step('register and log in', () => registerAndLogIn(page));

  const hotelUrl = await test.step('open a Seattle hotel', async () => {
    const [checkIn, checkOut] = utcDates(25, 27);
    await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 2 });
    await page.locator('[data-testid^="view-rooms-"]').first().click();
    await expect(page.getByTestId('hotel-name')).toBeVisible();
    await expect(page).toHaveURL(/\/hotels\/\d+\?/);
    return page.url();
  });

  await test.step('click Book with a token the API rejects', async () => {
    const book = page.locator('[data-testid^="book-"]:enabled').first();
    await expect(book).toBeVisible();

    await rejectBookingsToken(page);
    // The 401 to the POST itself proves the server refused the booking, so
    // none was created.
    const created = page.waitForResponse(
      (r) => isBookingsApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await book.click();
    expect((await created).status()).toBe(401);
  });

  await test.step('stay on the hotel page with the 401 inline, signed out', async () => {
    const error = page.getByTestId('inline-error');
    await expect(error).toBeVisible();
    await expect(error.getByTestId('login-again')).toHaveText('Log in again');
    // Focus moves from the disabled "Book" to the recovery link.
    await expect(error.getByTestId('login-again')).toBeFocused();
    await expect(page).toHaveURL(hotelUrl);
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });
});

test('token expiry signs the guest out of the nav bar without a request', async ({ page }) => {
  // Installed before the app loads, so the app's Date and timers run on a clock
  // the test can move forward. Until then it keeps pace with real time, so the
  // server's expiresAt lies 60 minutes after the login on both clocks.
  await page.clock.install();

  await test.step('register and log in', () => registerAndLogIn(page));

  const apiRequests: string[] = [];
  page.on('request', (request) => {
    if (isApi(new URL(request.url()))) {
      apiRequests.push(`${request.method()} ${request.url()}`);
    }
  });

  await test.step('59 minutes later the guest is still signed in', async () => {
    await page.clock.fastForward('59:00');
    await expect(page.getByTestId('nav-email')).toBeVisible();
    await expect(page.getByTestId('nav-logout')).toBeVisible();
  });

  await test.step('past the 60 minutes the nav offers "Log in" on the same page', async () => {
    // A keyboard guest left on "Log out" keeps focus in the nav when the
    // button is removed at expiry.
    await page.getByTestId('nav-logout').focus();
    await expect(page.getByTestId('nav-logout')).toBeFocused();

    await page.clock.fastForward('02:00');
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-login')).toBeFocused();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
    await expect(page).toHaveURL(/\/search$/);
  });

  expect(apiRequests).toEqual([]);
});
