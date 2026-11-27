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
 * Every test runs in a fresh browser context and therefore starts signed
 * out, because the session lives only in memory.
 */
import { expect, test, type Page } from '@playwright/test';

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

/** Every hotel card on the search page. */
const HOTEL_RESULTS = '[data-testid^="hotel-result-"]';

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
});

test('search with no availability shows the empty message', async ({ page }) => {
  const [checkIn, checkOut] = utcDates(30, 32);
  await page.goto('/search');
  await searchHotels(page, { city: 'Atlantis', checkIn, checkOut, guests: 1 });

  await expect(page.getByTestId('search-empty')).toHaveText(
    'No hotels with available rooms in Atlantis for these dates.',
  );
  await expectNoInlineError(page);
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
    await expect(page).toHaveURL(hotelUrl);
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });
});
