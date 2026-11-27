// Criterion 6: every route and component of SmartHotel.Web is reachable from
// the app's navigation. The redirect routes ('' and '**') are checked by
// direct URLs, because they are not navigation targets; every navigable route
// is then reached by clicks alone after one initial load of /search.
//
// The session lifecycle is checked on the same routes. The reachability test
// ends by logging out from /bookings, so only "Log out" itself can bring the
// guest to /search, and it must send no API request. A separate test reloads
// /bookings while signed in: the session lives only in memory, so the reload
// starts signed out, authGuard redirects to /login?returnUrl=%2Fbookings
// without the session-expired notice, and no API request leaves the page until
// the guest logs in again, after which the login returns them to /bookings and
// only then loads the list.
import { test, expect, type Page, type Request } from '@playwright/test';

import { apiUrl, expectSignedInAs, logIn, registerGuest, searchHotels, TEST_PASSWORD, uniqueEmail, utcDates } from './support/helpers';

/**
 * Starts recording, in order, the requests the page sends to the API the run
 * targets, each as `METHOD /path` with no query and no headers. A request
 * counts only when its origin is the API's and its path is the API's base path
 * or lies below it, so a call to any other backend is never mistaken for one
 * to this API. With `ignorePreflight` set, CORS preflight `OPTIONS` requests
 * are left out, leaving the calls the app itself makes.
 */
function recordApiRequests(page: Page, options: { ignorePreflight: boolean }): { requests: string[]; stop: () => void } {
  const api = new URL(apiUrl());
  const requests: string[] = [];
  const record = (request: Request): void => {
    const url = new URL(request.url());
    const toApi =
      url.origin === api.origin && (url.pathname === api.pathname || url.pathname.startsWith(`${api.pathname}/`));
    if (toApi && !(options.ignorePreflight && request.method() === 'OPTIONS')) {
      requests.push(`${request.method()} ${url.pathname}`);
    }
  };
  page.on('request', record);
  return { requests, stop: () => page.off('request', record) };
}

test('entry and unknown URLs redirect to search', async ({ page }) => {
  const h1 = (name: string) => page.getByRole('heading', { level: 1, name, exact: true });

  await test.step("'' redirects to /search", async () => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
  });

  await test.step("'**' redirects to /search", async () => {
    await page.goto('/no-such-page');
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
  });
});

test('every navigable route is reachable by clicks', async ({ page }) => {
  test.setTimeout(90_000);

  const email = uniqueEmail();
  const [checkIn, checkOut] = utcDates(20, 22);
  const h1 = (name: string) => page.getByRole('heading', { level: 1, name, exact: true });

  // The only direct load: the in-memory session must survive every later step,
  // and a second page.goto would reload the app and sign the guest out.
  await test.step('shell renders on /search', async () => {
    await page.goto('/search');
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();

    await expect(page.locator('app-root')).toBeAttached();
    await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
    await expect(page.locator('main')).toBeVisible();
    await expect(page.getByTestId('nav-brand')).toHaveText('SmartHotel');
    await expect(page.getByTestId('nav-search')).toHaveText('Search');
    await expect(page.getByTestId('nav-bookings')).toHaveText('My bookings');
    await expect(page.getByTestId('nav-login')).toHaveText('Log in');
    await expect(page.getByTestId('nav-register')).toHaveText('Register');

    // The nav marks the current page; of the two links to /search, only "Search" does.
    await expect(page.getByTestId('nav-search')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-brand')).not.toHaveAttribute('aria-current');
    await expect(page.getByTestId('nav-bookings')).not.toHaveAttribute('aria-current');
  });

  await test.step('nav "Register" opens /register', async () => {
    await page.getByTestId('nav-register').click();
    await expect(page).toHaveURL(/\/register$/);
    await expect(h1('Register')).toBeVisible();
    await expect(page.getByTestId('nav-register')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-search')).not.toHaveAttribute('aria-current');
  });

  await test.step('"Already registered? Log in" opens /login', async () => {
    const link = page.getByTestId('login-link');
    await expect(link).toHaveText('Already registered? Log in');
    await link.click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(h1('Log in')).toBeVisible();
    await expect(page.getByTestId('nav-login')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-register')).not.toHaveAttribute('aria-current');
  });

  await test.step('"Create an account" opens /register', async () => {
    const link = page.getByTestId('register-link');
    await expect(link).toHaveText('Create an account');
    await link.click();
    await expect(page).toHaveURL(/\/register$/);
    await expect(h1('Register')).toBeVisible();
    await expect(page.getByTestId('nav-register')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-login')).not.toHaveAttribute('aria-current');
  });

  await test.step('register, log in, then nav "Search" opens /search', async () => {
    await registerGuest(page, { email, password: TEST_PASSWORD, fullName: 'Navigation Guest' });
    await expect(page).toHaveURL(/\/login\?registered=1$/);
    await expect(h1('Log in')).toBeVisible();
    // The query string does not stop "Log in" marking its page.
    await expect(page.getByTestId('nav-login')).toHaveAttribute('aria-current', 'page');

    await logIn(page, email, TEST_PASSWORD);
    await expectSignedInAs(page, email);
    await expect(page.getByTestId('nav-logout')).toHaveText('Log out');
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();

    // Login with no returnUrl already lands on bare /search, so the click must
    // start from a search with criteria in the URL: only a working "Search"
    // link clears that query and its results back to the idle page.
    await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 1 });
    await expect(page).toHaveURL(/\/search\?.*city=Seattle/);
    await expect(page.locator('[data-testid^="hotel-result-"]').first()).toBeVisible();
    // Search criteria in the query string keep "Search", not the brand, current.
    await expect(page.getByTestId('nav-search')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-brand')).not.toHaveAttribute('aria-current');
    await expect(page.getByTestId('nav-bookings')).not.toHaveAttribute('aria-current');

    await page.getByTestId('nav-search').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
    await expect(page.locator('[data-testid^="hotel-result-"]')).toHaveCount(0);
    await expect(page.getByTestId('nav-search')).toHaveAttribute('aria-current', 'page');
  });

  await test.step('search, then "View rooms" opens /hotels/:id', async () => {
    await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 1 });
    const viewRooms = page.locator('[data-testid^="view-rooms-"]').first();
    await expect(viewRooms).toHaveText('View rooms');
    await viewRooms.click();
    await expect(page).toHaveURL(/\/hotels\/\d+\?/);
    const hotelName = page.getByTestId('hotel-name');
    await expect(hotelName).toBeVisible();
    await expect(hotelName).toHaveText(/\S/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveAttribute('data-testid', 'hotel-name');
    // No nav link leads to a hotel detail page, so none is current.
    await expect(page.getByTestId('nav-brand')).not.toHaveAttribute('aria-current');
    await expect(page.getByTestId('nav-search')).not.toHaveAttribute('aria-current');
    await expect(page.getByTestId('nav-bookings')).not.toHaveAttribute('aria-current');
  });

  await test.step('nav "My bookings" opens /bookings', async () => {
    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/bookings$/);
    await expect(h1('My bookings')).toBeVisible();
    await expect(page.getByTestId('bookings-empty')).toHaveText('You have no bookings yet.');
    await expect(page.getByTestId('nav-bookings')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-search')).not.toHaveAttribute('aria-current');
  });

  await test.step('the brand link opens /search', async () => {
    await page.getByTestId('nav-brand').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
    await expect(page.getByTestId('nav-search')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-brand')).not.toHaveAttribute('aria-current');
    await expect(page.getByTestId('nav-bookings')).not.toHaveAttribute('aria-current');
  });

  await test.step('after "Log out", nav "My bookings" is redirected to /login and back after logging in', async () => {
    await page.getByTestId('nav-logout').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(page.getByTestId('nav-login')).toBeVisible();
    // The removed "Log out" button hands keyboard focus to its replacement.
    await expect(page.getByTestId('nav-login')).toBeFocused();

    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings$/);
    await expect(h1('Log in')).toBeVisible();
    // The guard's redirect lands on /login, so "Log in", not "My bookings", is current.
    await expect(page.getByTestId('nav-login')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-bookings')).not.toHaveAttribute('aria-current');

    await logIn(page, email, TEST_PASSWORD);
    await expect(page).toHaveURL(/\/bookings$/);
    await expect(h1('My bookings')).toBeVisible();
    await expect(page.getByTestId('bookings-empty')).toHaveText('You have no bookings yet.');
    await expect(page.getByTestId('nav-bookings')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('nav-search')).not.toHaveAttribute('aria-current');
  });

  // The earlier "Log out" ran on bare /search, where the page would stay even
  // if logging out did not navigate. From /bookings only "Log out" itself can
  // reach /search, and it calls no endpoint.
  await test.step('"Log out" from My bookings opens /search without an API request', async () => {
    const recorder = recordApiRequests(page, { ignorePreflight: false });
    try {
      await page.getByTestId('nav-logout').click();
      await expect(page).toHaveURL(/\/search$/);
      await expect(h1('Find a hotel')).toBeVisible();
      await expect(page.getByTestId('nav-login')).toBeVisible();
      await expect(page.getByTestId('nav-email')).toHaveCount(0);
      expect(recorder.requests, 'API requests sent by "Log out" and the bare search page').toEqual([]);
    } finally {
      recorder.stop();
    }
  });
});

test('reloading My bookings signs the guest out and returns there after logging in again', async ({ page }) => {
  const email = uniqueEmail();
  const api = new URL(apiUrl());
  const h1 = (name: string) => page.getByRole('heading', { level: 1, name, exact: true });

  await test.step('a signed-in guest opens My bookings', async () => {
    await page.goto('/register');
    await expect(h1('Register')).toBeVisible();
    await registerGuest(page, { email, password: TEST_PASSWORD, fullName: 'Reload Guest' });
    await logIn(page, email, TEST_PASSWORD);

    const [list] = await Promise.all([
      page.waitForResponse((r) => {
        const url = new URL(r.url());
        return (
          url.origin === api.origin && url.pathname === `${api.pathname}/bookings` && r.request().method() === 'GET'
        );
      }),
      page.getByTestId('nav-bookings').click(),
    ]);
    expect(list.status(), 'status of GET /api/bookings before the reload').toBe(200);
    await expect(page).toHaveURL(/\/bookings$/);
    await expect(h1('My bookings')).toBeVisible();
  });

  const recorder = recordApiRequests(page, { ignorePreflight: true });
  try {
    await test.step('the reload starts signed out on /login, returning to /bookings, before any API request', async () => {
      await page.reload();
      await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings$/);
      await expect(h1('Log in')).toBeVisible();
      await expect(page.getByTestId('nav-login')).toBeVisible();
      await expect(page.getByTestId('nav-email')).toHaveCount(0);
      // The notice region is always mounted; the guard's redirect leaves it
      // empty, because only a server 401 adds the session-expired reason.
      await expect(page.getByTestId('login-notice')).toHaveText('');
      expect(recorder.requests, 'API requests between the reload and logging in again').toEqual([]);
    });

    await test.step('logging in again opens /bookings, which only then loads the list', async () => {
      await logIn(page, email, TEST_PASSWORD);
      await expect(page).toHaveURL(/\/bookings$/);
      await expect(h1('My bookings')).toBeVisible();
      await expect(page.getByTestId('bookings-empty')).toHaveText('You have no bookings yet.');
      expect(recorder.requests, 'API requests from the reload through the bookings list').toEqual([
        'POST /api/auth/login',
        'GET /api/bookings',
      ]);
    });
  } finally {
    recorder.stop();
  }
});
