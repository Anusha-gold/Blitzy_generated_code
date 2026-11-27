/**
 * Shared data and UI helpers for the web end-to-end specs: the booking
 * journey, navigation reachability, the stack and latency checks and the UI
 * states.
 *
 * Every UI helper acts on the page the browser is **currently** showing and
 * never calls `page.goto`. The specs own all navigation, so
 * `navigation.spec.ts` can prove that each route is reachable by clicks alone.
 * Each UI helper finds its elements through their `data-testid` hooks, fills a
 * form field only once Angular has bound it, and ends with an auto-waiting
 * assertion on the outcome it is for, so a spec can act on the next screen as
 * soon as the helper returns, including straight after a click navigation.
 *
 * Playwright transpiles this file as CommonJS (the workspace `package.json`
 * declares no `"type": "module"`). The workspace declares no `@types/node`, so
 * `process` is an unchecked global and the type annotations are stripped,
 * never checked.
 *
 * Importing this module has no side effects: it performs no I/O, logs nothing
 * and never writes to `process.env`.
 */
import { expect, type Page } from '@playwright/test';

import { resolveStackTarget } from './stack-target';

/** Removes exactly one trailing `/`, if present. */
function withoutTrailingSlash(value: string): string {
  return value.endsWith('/') ? value.slice(0, -1) : value;
}

/**
 * Fills one reactive-form input, but only once Angular has bound it to its
 * form control, and then confirms the value held.
 *
 * The app coalesces change detection to the next animation frame, so after a
 * click navigation a routed page's inputs are in the DOM before the first
 * change-detection pass binds them, and that pass writes each control's
 * initial value into its input, erasing anything filled in between. Angular
 * forms set `ng-pristine` or `ng-dirty` on a bound input in that same pass,
 * after the initial write, so waiting for either class makes the fill land on
 * a bound control rather than one about to be reset.
 */
async function fillControl(page: Page, testId: string, value: string): Promise<void> {
  const field = page.getByTestId(testId);
  await expect(field).toHaveClass(/\bng-(?:pristine|dirty)\b/);
  await field.fill(value);
  await expect(field).toHaveValue(value);
}

/**
 * Returns the API base URL the run targets, for requests a spec sends to the
 * API directly rather than through the web app.
 *
 * It reads `E2E_API_URL`, which `playwright.config.ts` writes from
 * `resolveStackTarget()` so the main process and every worker read the same
 * target. When the variable is missing or empty it falls back to
 * `resolveStackTarget().apiUrl` itself.
 *
 * @returns The API base URL, ending in `/api` with no trailing slash. In a
 *   fresh run it is `http://localhost:18080/api`.
 */
export function apiUrl(): string {
  const fromEnvironment = process.env['E2E_API_URL'];
  return withoutTrailingSlash(fromEnvironment || resolveStackTarget().apiUrl);
}

/**
 * Returns the UTC calendar date `offsetDays` days from now.
 *
 * The API decides "today" by the current UTC date (check-in may not be earlier
 * than it), and it accepts dates only as exactly `yyyy-MM-dd`, so this form
 * can go into a query, a body or a date input unchanged.
 *
 * @param offsetDays - Whole days to add to today's UTC date; `0` is today and
 *   a negative value is in the past.
 * @returns The date as `yyyy-MM-dd`, for example `2026-12-07`.
 */
export function utcDate(offsetDays: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() + offsetDays);
  return d.toISOString().slice(0, 10);
}

/**
 * Returns an email address that no earlier test has registered.
 *
 * It follows the `guest+<timestamp>@example.test` shape, with a random suffix
 * after the timestamp so two guests created in the same millisecond stay
 * distinct. It is lower-case printable ASCII, so registration accepts it and
 * the server's email normalization leaves it unchanged, and the nav bar shows
 * exactly this string after login.
 *
 * @returns A fresh address such as `guest+1795435200000123456@example.test`.
 */
export function uniqueEmail(): string {
  return `guest+${Date.now()}${Math.floor(Math.random() * 1_000_000)}@example.test`;
}

/**
 * The password every test guest registers and logs in with. It lies within the
 * registration bound of 8 to 128 characters and is used only against the
 * throwaway test stack, whose guests are created by the tests themselves.
 */
export const TEST_PASSWORD = 'SmartHotel-e2e-2026';

/**
 * Registers a new guest through the register page's form.
 *
 * Expects the browser to be on the register page (`/register`). It fills the
 * email, password and full name, clicks "Create account", and waits until the
 * app has navigated to `/login?registered=1`. The register page hands the
 * email to the login page in memory, never in the URL.
 *
 * @param page - The page showing the register page.
 * @param guest - The account to create.
 * @param guest.email - The email address, for example from {@link uniqueEmail}.
 * @param guest.password - The password, for example {@link TEST_PASSWORD}.
 * @param guest.fullName - The guest's full name.
 * @returns A promise that settles once the login page's URL is shown.
 */
export async function registerGuest(
  page: Page,
  guest: { email: string; password: string; fullName: string },
): Promise<void> {
  await fillControl(page, 'register-email', guest.email);
  await fillControl(page, 'register-password', guest.password);
  await fillControl(page, 'register-full-name', guest.fullName);
  await page.getByTestId('register-submit').click();
  await expect(page).toHaveURL(/\/login\?registered=1$/);
}

/**
 * Logs a guest in through the login page's form.
 *
 * Expects the browser to be on the login page (`/login`, with or without its
 * query). It fills the email, overwriting any pre-fill handed over by the
 * register page, and the password, clicks "Log in", and waits until the nav
 * bar shows the signed-in guest's email. It asserts no URL: where the login
 * page navigates next depends on its `returnUrl`, which the specs check.
 *
 * @param page - The page showing the login page.
 * @param email - The registered email address, as the nav bar will show it.
 * @param password - The account's password.
 * @returns A promise that settles once the nav bar shows `email`.
 */
export async function logIn(page: Page, email: string, password: string): Promise<void> {
  await fillControl(page, 'login-email', email);
  await fillControl(page, 'login-password', password);
  await page.getByTestId('login-submit').click();
  await expect(page.getByTestId('nav-email')).toHaveText(email);
}

/**
 * Runs a hotel search through the search page's form and waits for its
 * outcome.
 *
 * Expects the browser to be on the search page (`/search`). It fills the
 * criteria, clicks "Search", waits for the `GET /api/hotels` response, and then
 * waits until the page shows a result, the empty-result message or an inline
 * error. It asserts nothing about which of the three appeared, so the specs
 * can use it for results, empty results, a 400 and a delayed response alike.
 *
 * The response waiter is registered before the click, so a fast response
 * cannot be missed. It matches the `GET` only, never the CORS preflight
 * `OPTIONS` a signed-in, cross-origin request can cause. The search page
 * reruns an identical search too, so repeating the same criteria still sends
 * a request and the wait cannot hang.
 *
 * @param page - The page showing the search page.
 * @param criteria - The search criteria to enter.
 * @param criteria.city - The city, for example `Seattle`.
 * @param criteria.checkIn - The check-in date as `yyyy-MM-dd`, for example
 *   from {@link utcDate}; the date input accepts this form.
 * @param criteria.checkOut - The check-out date as `yyyy-MM-dd`.
 * @param criteria.guests - The party size.
 * @returns A promise that settles once the search outcome is visible.
 */
export async function searchHotels(
  page: Page,
  criteria: { city: string; checkIn: string; checkOut: string; guests: number },
): Promise<void> {
  await fillControl(page, 'search-city', criteria.city);
  await fillControl(page, 'search-check-in', criteria.checkIn);
  await fillControl(page, 'search-check-out', criteria.checkOut);
  await fillControl(page, 'search-guests', String(criteria.guests));
  await Promise.all([
    page.waitForResponse(
      (r) => new URL(r.url()).pathname === '/api/hotels' && r.request().method() === 'GET',
    ),
    page.getByTestId('search-submit').click(),
  ]);
  await expect(
    page
      .locator(
        '[data-testid^="hotel-result-"], [data-testid="search-empty"], [data-testid="inline-error"]',
      )
      .first(),
  ).toBeVisible();
}

/**
 * Reads the number of available rooms the hotel detail page shows for one
 * room type.
 *
 * Expects the browser to be on a hotel detail page (`/hotels/:id`). It waits
 * until the room type's "Available" cell is visible and holds a whole number,
 * so text that has not rendered yet is never read as `0`.
 *
 * @param page - The page showing the hotel detail page.
 * @param roomTypeId - The id of the room type whose row to read.
 * @returns The available-room count shown for that room type.
 */
export async function readAvailableCount(page: Page, roomTypeId: number): Promise<number> {
  const cell = page.getByTestId(`available-count-${roomTypeId}`);
  await expect(cell).toBeVisible();
  await expect(cell).toHaveText(/^\s*\d+\s*$/);
  const n = Number((await cell.innerText()).trim());
  expect(Number.isInteger(n)).toBe(true);
  return n;
}

/**
 * Asserts that the current page shows no inline error.
 *
 * The shared error-message component renders its `inline-error` element only
 * while an error is set, so a count of zero means no error is on screen. The
 * booking journey calls this after each step to show that no inline error
 * appeared at any step.
 *
 * @param page - The page to check, on any screen.
 * @returns A promise that settles once the page holds no inline error.
 */
export async function expectNoInlineError(page: Page): Promise<void> {
  await expect(page.getByTestId('inline-error')).toHaveCount(0);
}
