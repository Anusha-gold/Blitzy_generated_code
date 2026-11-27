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
 * Diagnostics stay value-free: no password, email address or full name may
 * appear in a Playwright step title, call log, or matcher expected/received
 * output. Playwright prints the argument of `fill`, `type`,
 * `pressSequentially` and the keyboard input calls in those places, the
 * expected and received values of text and value matchers, and a preview of
 * the element text a locator resolved to. So {@link fillBound} sets a value
 * inside the page, never through those calls, and every comparison with such
 * a value runs inside the page and is asserted as a boolean under a fixed
 * message ({@link expectControlValue}, {@link expectSignedInAs}).
 *
 * No test credential is written in source: each test guest registers and logs
 * in with its own random password from {@link uniquePassword}, so an account
 * a test leaves on a reused stack cannot be signed into with a known value.
 *
 * Playwright transpiles this file as CommonJS (the workspace `package.json`
 * declares no `"type": "module"`). The workspace declares no `@types/node`, so
 * `process` is an unchecked global, the `node:crypto` import is unchecked and
 * the type annotations are stripped, never checked.
 *
 * Importing this module has no side effects: it performs no I/O, logs nothing
 * and never writes to `process.env`.
 */
import { randomBytes } from 'node:crypto';

import { expect, type Page } from '@playwright/test';

import { resolveStackTarget } from './stack-target';

/**
 * Fills one reactive-form input, but only once Angular has bound it to its
 * form control, and then confirms the value held. It waits for no outcome of
 * the form, so a spec can also use it for a submission that must fail or a
 * response that is held back.
 *
 * The app coalesces change detection to the next animation frame, so after a
 * click navigation a routed page's inputs are in the DOM before the first
 * change-detection pass binds them, and that pass writes each control's
 * initial value into its input, erasing anything filled in between. Angular
 * forms set `ng-pristine` or `ng-dirty` on a bound input in that same pass,
 * after the initial write, so waiting for either class makes the fill land on
 * a bound control rather than one about to be reset.
 *
 * The value never passes through a Playwright input call, whose step title
 * and call log print it. Once the input is visible and editable, the helper
 * focuses it, assigns the value inside the page and dispatches the bubbling
 * `input` and `change` events a user's typing ends with, which Angular's
 * value accessors listen to for text, email, password, date and number
 * inputs alike. Every wait and check names only the input's test id.
 *
 * @param page - The page showing the form.
 * @param testId - The input's `data-testid`, for example `login-email`.
 * @param value - The text to enter; a date input takes `yyyy-MM-dd`.
 * @returns A promise that settles once the input holds `value`.
 */
export async function fillBound(page: Page, testId: string, value: string): Promise<void> {
  const field = page.getByTestId(testId);
  await expect(field).toHaveClass(/\bng-(?:pristine|dirty)\b/);
  await expect(field).toBeVisible();
  await expect(field).toBeEditable();
  await field.focus();
  await field.evaluate((element, text) => {
    const input = element as HTMLInputElement;
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
  await expectControlValue(page, testId, value);
}

/**
 * Asserts that a form input holds an expected value, without the value
 * appearing in any diagnostic.
 *
 * The comparison runs inside the page, and only its boolean result is
 * polled, so a failure reports the input's test id and `true`/`false`, never
 * the expected or the actual value. Like a web-first assertion it retries
 * until the value matches or the expect timeout passes. Resolving the input
 * itself is value-free too: Playwright previews an `<input>` by its
 * attributes only, and Angular holds the value as a property, never as an
 * attribute, so neither the call log nor a strict-mode error can carry it.
 *
 * @param page - The page showing the input.
 * @param testId - The input's `data-testid`, for example `login-email`.
 * @param expected - The value the input must hold.
 * @returns A promise that settles once the input holds `expected`.
 */
export async function expectControlValue(page: Page, testId: string, expected: string): Promise<void> {
  const field = page.getByTestId(testId);
  await expect
    .poll(() => field.evaluate((element, value) => (element as HTMLInputElement).value === value, expected), {
      message: `input "${testId}" holds the expected value`,
    })
    .toBe(true);
}

/**
 * Asserts that the nav bar shows a guest signed in under `email`, without
 * the email appearing in any diagnostic.
 *
 * Playwright previews an element that holds only text with that text, which
 * here is the email. It prints that preview whenever a locator call resolves
 * one element (the call log, and `DEBUG=pw:api` output), and for every match
 * of a strict-mode violation, together with a suggested `getByText` locator.
 * So this helper never resolves a single `nav-email` element. It first waits
 * for exactly one visible match by counting, which logs only counts. It then
 * polls `evaluateAll` on the visibility-filtered locator: `evaluateAll`
 * applies no strict-mode check and logs no resolved element. Inside the page
 * it requires exactly one visible match whose whitespace-normalised text
 * equals `email`, so a hidden duplicate is ignored and two visible ones fail,
 * and it asserts only the boolean under a fixed message.
 *
 * @param page - The page to check, on any screen.
 * @param email - The email address the nav bar must show.
 * @returns A promise that settles once the nav bar shows exactly `email`.
 */
export async function expectSignedInAs(page: Page, email: string): Promise<void> {
  const visibleNavEmail = page.getByTestId('nav-email').filter({ visible: true });
  await expect(visibleNavEmail, 'the nav bar shows a signed-in guest').toHaveCount(1);
  await expect
    .poll(
      () =>
        visibleNavEmail.evaluateAll(
          (elements, expected) =>
            elements.length === 1 && (elements[0].textContent ?? '').replace(/\s+/g, ' ').trim() === expected,
          email,
        ),
      { message: 'the nav bar shows the expected guest email' },
    )
    .toBe(true);
}

/**
 * Returns the API base URL the run targets, for requests a spec sends to the
 * API directly rather than through the web app.
 *
 * It returns `E2E_API_URL` verbatim: `playwright.config.ts` writes it from
 * `resolveStackTarget()`, which validates and canonicalizes the URL once, so
 * the main process, every worker and every consumer read the same target.
 * When the variable is missing or empty it falls back to
 * `resolveStackTarget().apiUrl` itself.
 *
 * @returns The API base URL, whose path ends in `/api`, with no trailing
 *   slash. In a fresh run it is `http://localhost:18080/api`.
 */
export function apiUrl(): string {
  return process.env['E2E_API_URL'] || resolveStackTarget().apiUrl;
}

/**
 * Tells whether a request the browser sends is one endpoint of the API the
 * run targets, for the specs' response waiters and routes.
 *
 * The request must go to the origin of {@link apiUrl} (scheme, host and port,
 * compared in the URL parser's canonical form), and its path must be the
 * API's path, `/api`, followed by `endpoint`. A path-only match would also
 * accept that endpoint of another stack's API, so a web app configured for a
 * different API could pass the browser checks while the specs' direct API
 * requests use the target. The query is not compared, and the caller checks
 * the method, so a CORS preflight `OPTIONS` can be told apart.
 *
 * @param url - The request URL, as `request.url()` returns it or as
 *   `page.route` passes it. A value that is not an absolute URL never matches.
 * @param endpoint - The endpoint's path below the API base, starting with `/`,
 *   for example `/hotels` or `/auth/login`.
 * @param scope - `exact`, the default, matches that path only; `withSubPaths`
 *   also matches every path below it, so `/bookings` covers `/bookings/{id}`.
 * @returns `true` when the request targets that endpoint of the target API.
 * @throws Error when `endpoint` does not start with `/`.
 */
export function isApiEndpoint(
  url: string | URL,
  endpoint: string,
  scope: 'exact' | 'withSubPaths' = 'exact',
): boolean {
  if (!endpoint.startsWith('/')) {
    throw new Error('isApiEndpoint expects an endpoint path that starts with /.');
  }
  let requested: URL;
  try {
    requested = new URL(url);
  } catch {
    return false;
  }
  const api = new URL(apiUrl());
  const path = `${api.pathname}${endpoint}`;
  return (
    requested.origin === api.origin &&
    (requested.pathname === path || (scope === 'withSubPaths' && requested.pathname.startsWith(`${path}/`)))
  );
}

/**
 * Returns the UTC calendar dates the given numbers of days from today, all
 * from one reading of the clock.
 *
 * The API decides "today" by the current UTC date (check-in may not be earlier
 * than it), and it accepts dates only as exactly `yyyy-MM-dd`, so each date
 * can go into a query, a body or a date input unchanged.
 *
 * The clock is read once per call, so every date of the call counts from the
 * same "today". A scenario takes all its dates from one call: separate reads
 * on either side of midnight UTC could otherwise build a check-in and
 * check-out of a different stay length.
 *
 * @param offsetDays - Whole days to add to today's UTC date, one per date
 *   wanted; `0` is today and a negative value is in the past.
 * @returns One date per offset, in the order given, each as `yyyy-MM-dd`; for
 *   example `utcDates(14, 16)` on 2026-11-23 returns
 *   `['2026-12-07', '2026-12-09']`.
 * @throws RangeError when an offset is not a whole number of days.
 */
export function utcDates(...offsetDays: number[]): string[] {
  const now = new Date();
  return offsetDays.map((offset) => {
    if (!Number.isInteger(offset)) {
      throw new RangeError(`utcDates expects whole days, got ${offset}.`);
    }
    const d = new Date(now.getTime());
    d.setUTCDate(d.getUTCDate() + offset);
    return d.toISOString().slice(0, 10);
  });
}

/**
 * Returns a date as the web app displays it in this suite: `MM/dd/yyyy`, the
 * `en-US` form. The app shows dates in the browser's own numeric order, the
 * order its native date inputs use, and `playwright.config.ts` pins the
 * browser's locale to `en-US`, so this is the form of every date the app
 * displays in the suite, whatever the host's language settings. The app keeps
 * `yyyy-MM-dd` in URLs and requests, so a spec compares against this form only
 * where a date is on screen.
 *
 * The conversion only rearranges the digits, so it reads no clock and no time
 * zone, and the calendar day is the one given.
 *
 * @param isoDate - The date as exactly `yyyy-MM-dd`, as {@link utcDates}
 *   returns it.
 * @returns The same date as `MM/dd/yyyy`; for example `'2026-12-07'` becomes
 *   `'12/07/2026'`.
 * @throws RangeError when `isoDate` is not of the form `yyyy-MM-dd`.
 */
export function displayDate(isoDate: string): string {
  const parts = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate);
  if (parts === null) {
    throw new RangeError(`displayDate expects a yyyy-MM-dd date, got "${isoDate}".`);
  }
  const [, year, month, day] = parts;
  return `${month}/${day}/${year}`;
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
 * Returns a new random password for one test guest.
 *
 * Each call draws 18 bytes from Node's cryptographically secure generator
 * (`node:crypto` `randomBytes`), so every guest gets its own unguessable
 * password and none is known from source. The bytes are written as base64url
 * after the fixed prefix `e2e-`, giving 28 characters of `A–Z`, `a–z`, `0–9`,
 * `-` and `_`: within registration's bound of 8 to 128 characters, with no
 * whitespace, so the API stores it exactly as sent.
 *
 * The caller keeps the value for the guest's later logins and never prints
 * it; {@link fillBound} keeps it out of Playwright's diagnostics.
 *
 * @returns A fresh password such as `e2e-` followed by 24 base64url characters.
 */
export function uniquePassword(): string {
  return `e2e-${randomBytes(18).toString('base64url')}`;
}

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
 * @param guest.password - The password, for example from {@link uniquePassword}.
 * @param guest.fullName - The guest's full name.
 * @returns A promise that settles once the login page's URL is shown.
 */
export async function registerGuest(
  page: Page,
  guest: { email: string; password: string; fullName: string },
): Promise<void> {
  await fillBound(page, 'register-email', guest.email);
  await fillBound(page, 'register-password', guest.password);
  await fillBound(page, 'register-full-name', guest.fullName);
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
  await fillBound(page, 'login-email', email);
  await fillBound(page, 'login-password', password);
  await page.getByTestId('login-submit').click();
  await expectSignedInAs(page, email);
}

/**
 * Runs a hotel search through the search page's form and waits for its
 * outcome.
 *
 * Expects the browser to be on the search page (`/search`). It fills the
 * criteria, clicks "Search", waits for the target API's `GET /api/hotels`
 * response, and then waits until the page shows a result, the empty-result
 * message or an inline error. It asserts nothing about which of the three
 * appeared, so the specs can use it for results, empty results, a 400 and a
 * delayed response alike.
 *
 * The response waiter is registered before the click, so a fast response
 * cannot be missed. It matches the `GET` to the target API's `/hotels` only
 * ({@link isApiEndpoint}): never the CORS preflight `OPTIONS` a signed-in,
 * cross-origin request can cause, and never a search the web app sends to
 * another API, which leaves the wait to time out. The search page reruns an
 * identical search too, so repeating the same criteria still sends a request
 * and the wait cannot hang.
 *
 * @param page - The page showing the search page.
 * @param criteria - The search criteria to enter.
 * @param criteria.city - The city, for example `Seattle`.
 * @param criteria.checkIn - The check-in date as `yyyy-MM-dd`, for example
 *   from {@link utcDates}; the date input accepts this form.
 * @param criteria.checkOut - The check-out date as `yyyy-MM-dd`.
 * @param criteria.guests - The party size.
 * @returns A promise that settles once the search outcome is visible.
 */
export async function searchHotels(
  page: Page,
  criteria: { city: string; checkIn: string; checkOut: string; guests: number },
): Promise<void> {
  await fillBound(page, 'search-city', criteria.city);
  await fillBound(page, 'search-check-in', criteria.checkIn);
  await fillBound(page, 'search-check-out', criteria.checkOut);
  await fillBound(page, 'search-guests', String(criteria.guests));
  await Promise.all([
    page.waitForResponse((r) => isApiEndpoint(r.url(), '/hotels') && r.request().method() === 'GET'),
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
 * while an error is set, so a count of zero means no error is on screen. It is
 * a snapshot of the screen on view at that moment only: it cannot see an
 * error that was shown and then removed, for example by the navigation that
 * ended a step. Evidence that no inline error appeared at any point of a flow
 * comes from {@link watchInlineErrors}.
 *
 * @param page - The page to check, on any screen.
 * @returns A promise that settles once the page holds no inline error.
 */
export async function expectNoInlineError(page: Page): Promise<void> {
  await expect(page.getByTestId('inline-error')).toHaveCount(0);
}

/**
 * The record of inline errors that {@link watchInlineErrors} keeps for one
 * page.
 */
export interface InlineErrorWatch {
  /**
   * Returns where an inline error has appeared on the page since the watch
   * was installed, including errors that are no longer on screen.
   *
   * It first has the current document hand over every report it has raised,
   * so an error rendered just before the call is included.
   *
   * @returns One `location.pathname` per inline error element, in the order
   *   they appeared, for example `['/search']`; empty when none has appeared.
   * @throws Error when the current document carries no watch, which happens
   *   when the watch was installed after the page's first navigation.
   */
  appearances(): Promise<string[]>;
}

/** Numbers the page bindings of the watches, so no two names collide. */
let inlineErrorWatchCount = 0;

/**
 * Starts recording every inline error the app renders on `page`, including
 * one that a later navigation removes before a snapshot check such as
 * {@link expectNoInlineError} could see it.
 *
 * Call it before the page's first `page.goto`. It installs, in every document
 * the page loads from then on, a `MutationObserver` that reports each
 * `inline-error` element once, when it is inserted or gains that test id, to
 * a page binding. A report carries only the screen's path, never the error's
 * text or the URL's query, so the record and any failure that prints it stay
 * value-free. A report raised while the binding is not yet reachable waits in
 * the page until the next report or the next
 * {@link InlineErrorWatch.appearances} call delivers it.
 *
 * @param page - A page that has not navigated yet.
 * @returns The watch whose {@link InlineErrorWatch.appearances} reads the
 *   record.
 */
export async function watchInlineErrors(page: Page): Promise<InlineErrorWatch> {
  inlineErrorWatchCount += 1;
  const binding = `__smartHotelInlineErrors${inlineErrorWatchCount}`;
  const seen: string[] = [];

  await page.exposeBinding(binding, (_source, path?: unknown) => {
    if (typeof path === 'string') {
      seen.push(path);
    }
  });

  await page.addInitScript((name: string) => {
    const scope = window as unknown as Record<string, unknown>;
    const selector = '[data-testid="inline-error"]';
    const reported = new WeakSet<Element>();
    const pending: string[] = [];

    const deliver = (): void => {
      const report = scope[name];
      if (typeof report !== 'function') {
        return;
      }
      for (const path of pending.splice(0)) {
        void (report as (path: string) => Promise<void>)(path);
      }
    };

    const record = (element: Element): void => {
      if (reported.has(element)) {
        return;
      }
      reported.add(element);
      pending.push(location.pathname);
      deliver();
    };

    const visit = (node: Node): void => {
      if (!(node instanceof Element)) {
        return;
      }
      if (node.matches(selector)) {
        record(node);
      }
      node.querySelectorAll(selector).forEach(record);
    };

    scope[`${name}Deliver`] = deliver;
    new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === 'attributes') {
          if (mutation.target instanceof Element && mutation.target.matches(selector)) {
            record(mutation.target);
          }
        } else {
          mutation.addedNodes.forEach(visit);
        }
      }
    }).observe(document, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['data-testid'],
    });
  }, binding);

  return {
    async appearances(): Promise<string[]> {
      // Calling the binding once more, and awaiting it, returns only after
      // every report the document raised before has been delivered in order.
      const installed = await page.evaluate(async (name: string) => {
        const scope = window as unknown as Record<string, unknown>;
        const deliver = scope[`${name}Deliver`];
        const report = scope[name];
        if (typeof deliver !== 'function' || typeof report !== 'function') {
          return false;
        }
        (deliver as () => void)();
        await (report as () => Promise<void>)();
        return true;
      }, binding);
      if (!installed) {
        throw new Error(
          'The inline-error watch is not installed in the current document; call watchInlineErrors before the first page.goto.',
        );
      }
      return [...seen];
    },
  };
}
