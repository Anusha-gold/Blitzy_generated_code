// Criterion 6: every route and component of SmartHotel.Web is reachable from
// the app's navigation. The redirect routes ('' and '**') are checked by
// direct URLs, because they are not navigation targets; every navigable route
// is then reached by clicks alone after one initial load of /search.
import { test, expect } from '@playwright/test';

import { logIn, registerGuest, searchHotels, TEST_PASSWORD, uniqueEmail, utcDate } from './support/helpers';

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
  const checkIn = utcDate(20);
  const checkOut = utcDate(22);
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
  });

  await test.step('nav "Register" opens /register', async () => {
    await page.getByTestId('nav-register').click();
    await expect(page).toHaveURL(/\/register$/);
    await expect(h1('Register')).toBeVisible();
  });

  await test.step('"Already registered? Log in" opens /login', async () => {
    const link = page.getByTestId('login-link');
    await expect(link).toHaveText('Already registered? Log in');
    await link.click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(h1('Log in')).toBeVisible();
  });

  await test.step('"Create an account" opens /register', async () => {
    const link = page.getByTestId('register-link');
    await expect(link).toHaveText('Create an account');
    await link.click();
    await expect(page).toHaveURL(/\/register$/);
    await expect(h1('Register')).toBeVisible();
  });

  await test.step('register, log in, then nav "Search" opens /search', async () => {
    await registerGuest(page, { email, password: TEST_PASSWORD, fullName: 'Navigation Guest' });
    await expect(page).toHaveURL(/\/login\?registered=1$/);
    await expect(h1('Log in')).toBeVisible();

    await logIn(page, email, TEST_PASSWORD);
    await expect(page.getByTestId('nav-email')).toHaveText(email);
    await expect(page.getByTestId('nav-logout')).toHaveText('Log out');

    await page.getByTestId('nav-search').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
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
  });

  await test.step('nav "My bookings" opens /bookings', async () => {
    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/bookings$/);
    await expect(h1('My bookings')).toBeVisible();
    await expect(page.getByTestId('bookings-empty')).toHaveText('You have no bookings yet.');
  });

  await test.step('the brand link opens /search', async () => {
    await page.getByTestId('nav-brand').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(h1('Find a hotel')).toBeVisible();
  });

  await test.step('after "Log out", nav "My bookings" is redirected to /login', async () => {
    await page.getByTestId('nav-logout').click();
    await expect(page).toHaveURL(/\/search$/);
    await expect(page.getByTestId('nav-login')).toBeVisible();

    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings$/);
    await expect(h1('Log in')).toBeVisible();
  });
});
