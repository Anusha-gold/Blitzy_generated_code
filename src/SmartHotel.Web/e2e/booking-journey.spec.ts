/**
 * The booking journey: success criteria 1 and 2, and the UTC date boundary of
 * "Cancel" on My bookings.
 *
 * Criterion 1: a new guest registers, logs in, searches Seattle for a 2-night
 * stay, opens a hotel, books a room and sees the booking in "My bookings",
 * all through the web app. After the first `page.goto('/')` every transition
 * is a click.
 *
 * Criterion 1 also requires that no inline error appeared at any step. Each
 * step's `expectNoInlineError` sees only the screen on view when it runs, so
 * it cannot see an error shown during registration, login or booking and
 * then removed by the navigation that ends the step. The journey therefore
 * installs `watchInlineErrors` before its first `page.goto`, and every step
 * ends, like the journey itself, by asserting that the watch has recorded no
 * inline error anywhere so far.
 *
 * The journey binds what the page shows to the API's own answers, matching
 * each request on the origin and path of the API the run targets and on its
 * method. "Book" must send a `POST /api/bookings` answered 201, and the one
 * row My bookings then lists must carry that booking's id and show, cell by
 * cell, its hotel, room type, assigned room, dates, nights, guests, the stay
 * total the detail page showed, and its status. The row's whole text would
 * not do: the "Cancel" button's visually hidden name repeats most of those
 * values. "Cancel" must send a `DELETE` answered 204. The page marks the row
 * `Cancelled` from the 204 alone, so the row counts only once the list the
 * page reloads next, answered 200, itself shows the booking `Cancelled`, and
 * the page's network events show that reload sent only after the 204 arrived.
 *
 * Criterion 2: the same search then shows one fewer available room of the
 * booked type, and after cancelling the count returns to its original value.
 * Search results carry only each hotel's lowest price, so the count is read on
 * the hotel detail page reached from the repeated search.
 *
 * On the way the web app calls all seven interface endpoints along the real
 * request path. The journey ends with its booking cancelled; when it fails
 * before that, the `afterEach` hook cancels through the API whatever the guest
 * still holds, so no `Confirmed` booking is left behind on a reused stack. The
 * create is tracked from before the "Book" click, and the hook first waits, for
 * a bounded time, until it settles, so a booking whose create was still in
 * flight when the journey failed is listed and cancelled too.
 *
 * The cancel boundary: "Cancel" is offered on a `Confirmed` booking whose
 * check-in is the current UTC date or later. A second test arranges, through
 * the API, booking A checking in tomorrow (UTC) and booking B the day after,
 * opens My bookings in a browser set to Pacific/Kiritimati (UTC+14), and sets
 * the page clock to noon UTC on B's check-in day, when the local date is
 * already the next day. B must still offer "Cancel" and A must not, so a page
 * that reads the local date, treats today's check-in as passed, or offers
 * "Cancel" on yesterday's check-in fails it. Its own `afterEach` hook cancels
 * both bookings the same way.
 */
import {
  test,
  expect,
  type APIRequestContext,
  type APIResponse,
  type Page,
  type Request,
  type Response,
} from '@playwright/test';

import {
  TEST_PASSWORD,
  apiUrl,
  displayDate,
  expectControlValue,
  expectNoInlineError,
  expectSignedInAs,
  logIn,
  readAvailableCount,
  registerGuest,
  searchHotels,
  uniqueEmail,
  utcDates,
  watchInlineErrors,
} from './support/helpers';

/** A response to `POST /api/bookings`, sent by the page or by the API client. */
type CreateResponse = Response | APIResponse;

/** What a cleanup hook needs to know about the guest a run registered. */
interface GuestRun {
  /** The guest's email address. */
  email: string;
  /** The guest's password. */
  password: string;
  /**
   * The run's latest `POST /api/bookings`, tracked from just before it was
   * sent: it settles with the response, or with `null` when none can arrive
   * any more (the page closed, or the request failed), and it never rejects.
   * `null` while the run has sent no create.
   */
  create: Promise<CreateResponse | null> | null;
}

/** What the journey's cleanup hook needs to know about the run in progress. */
interface JourneyState extends GuestRun {
  /**
   * Set once the list the page reloads after the 204 shows the booking
   * `Cancelled`, so the server holds no `Confirmed` booking of the run.
   */
  cancelled: boolean;
}

/**
 * The state of the run in progress, or `null` outside one. A module variable
 * rather than a test local, because the `afterEach` hook must still reach it
 * when the test body times out and never returns; the suite runs one test at
 * a time (`workers: 1`).
 */
let journey: JourneyState | null = null;

/** What the cancel-boundary test's cleanup needs to know about its run. */
interface BoundaryState extends GuestRun {
  /** The ids of the bookings the API has created for the guest, in order. */
  bookingIds: number[];
}

/**
 * The state of the cancel-boundary run in progress, or `null` outside one; a
 * module variable for the same reason as {@link journey}.
 */
let boundary: BoundaryState | null = null;

/**
 * Reads the numeric id from a `data-testid` such as `view-rooms-3`.
 *
 * @throws Error when the attribute is missing or does not have the form
 *   `<prefix><digits>`.
 */
function idFromTestId(testId: string | null, prefix: string): number {
  if (testId !== null && testId.startsWith(prefix)) {
    const id = testId.slice(prefix.length);
    if (/^\d+$/.test(id)) {
      return Number(id);
    }
  }
  const found = testId === null ? 'no data-testid' : `"${testId}"`;
  throw new Error(`Expected a data-testid of the form "${prefix}<id>", found ${found}.`);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** How long a step waits for the API response that one of its clicks causes. */
const RESPONSE_TIMEOUT_MS = 30_000;

/**
 * Whether `request` is a `method` request to `path` under the API the run
 * targets. Origin and path both come from {@link apiUrl}, so a request to
 * another stack's API, the SPA's own `/bookings` route or a CORS preflight
 * (`OPTIONS`) never matches.
 *
 * @param path - The path below the API base, for example `/bookings/7`.
 */
function isApiRequest(request: Request, method: string, path: string): boolean {
  const api = new URL(apiUrl());
  const url = new URL(request.url());
  return (
    request.method() === method &&
    url.origin === api.origin &&
    url.pathname === `${api.pathname.replace(/\/$/, '')}${path}`
  );
}

/**
 * Whether `response` answers a `method` request to `path` under the API the
 * run targets, matched as {@link isApiRequest} matches its request.
 *
 * @param path - The path below the API base, for example `/bookings/7`.
 */
function isApiResponse(response: Response, method: string, path: string): boolean {
  return isApiRequest(response.request(), method, path);
}

/**
 * Waits up to {@link RESPONSE_TIMEOUT_MS} for the response to the next
 * `method` request the page sends to `path` under the API. Call it before the
 * action that sends the request, so a fast response cannot be missed.
 *
 * A step can fail before it awaits the waiter, for example when a cancel
 * answered 409 fails the 204 assertion and the page never sends the reload
 * another waiter expects. When the test then ends, the abandoned waiter
 * rejects, and Playwright would report that rejection as a second error
 * beside the step's own. A rejection handler is therefore attached as the
 * waiter is registered; it handles that rejection only, and the returned
 * promise, the waiter itself, still rejects for a step that awaits it.
 */
function waitForApiResponse(page: Page, method: string, path: string): Promise<Response> {
  const waiting = page.waitForResponse((r) => isApiResponse(r, method, path), { timeout: RESPONSE_TIMEOUT_MS });
  void waiting.catch(() => undefined);
  return waiting;
}

/**
 * Settles with the value `promise` resolves to, or with `null` where it would
 * reject. It never rejects, so a tracked request leaves no unhandled
 * rejection behind when the run fails before the request settles.
 */
function settledOrNull<T>(promise: Promise<T>): Promise<T | null> {
  return promise.then(
    (value) => value,
    () => null,
  );
}

/**
 * Starts tracking the response to the next `method` request the page sends
 * to `path` under the API. Unlike {@link waitForApiResponse} it sets no time
 * limit of its own, so each wait on it sets its own ({@link settleWithin}),
 * and it never rejects ({@link settledOrNull}). Call it before the action that
 * sends the request.
 *
 * @returns A promise of that response, or of `null` when the page closes
 *   before it arrives.
 */
function trackApiResponse(page: Page, method: string, path: string): Promise<Response | null> {
  return settledOrNull(page.waitForResponse((r) => isApiResponse(r, method, path), { timeout: 0 }));
}

/** What {@link settleWithin} returns when the deadline came first. */
const DEADLINE_PASSED = Symbol('deadline passed');

/**
 * Waits for `promise` for at most `ms` milliseconds, then clears its timer.
 *
 * @returns What `promise` resolved to, or {@link DEADLINE_PASSED} when it was
 *   still pending at the deadline.
 */
async function settleWithin<T>(promise: Promise<T>, ms: number): Promise<T | typeof DEADLINE_PASSED> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<typeof DEADLINE_PASSED>((resolve) => {
    timer = setTimeout(() => resolve(DEADLINE_PASSED), ms);
  });
  try {
    return await Promise.race([promise, deadline]);
  } finally {
    clearTimeout(timer);
  }
}

/** A booking as `POST /api/bookings` and `GET /api/bookings` return it. */
interface Booking {
  id: number;
  hotelId: number;
  hotelName: string;
  roomTypeId: number;
  roomTypeName: string;
  roomNumber: string;
  checkIn: string;
  checkOut: string;
  nights: number;
  guests: number;
  totalPrice: number;
  status: string;
  createdAt: string;
}

/** The {@link Booking} fields that hold a positive whole number. */
const BOOKING_COUNT_FIELDS = ['id', 'hotelId', 'roomTypeId', 'nights', 'guests'] as const;

/** The {@link Booking} fields that hold a string. */
const BOOKING_TEXT_FIELDS = [
  'hotelName',
  'roomTypeName',
  'roomNumber',
  'checkIn',
  'checkOut',
  'status',
  'createdAt',
] as const;

/**
 * Reads one booking from a parsed response body.
 *
 * @param value - The parsed JSON.
 * @param source - What `value` is, for the error message.
 * @throws Error naming the first missing or mistyped field, never a value,
 *   when `value` is not a booking.
 */
function readBooking(value: unknown, source: string): Booking {
  if (!isRecord(value)) {
    throw new Error(`${source} is not a booking object.`);
  }
  for (const field of BOOKING_COUNT_FIELDS) {
    const n = value[field];
    if (typeof n !== 'number' || !Number.isInteger(n) || n < 1) {
      throw new Error(`${source} has no positive whole-number "${field}".`);
    }
  }
  if (typeof value['totalPrice'] !== 'number' || !Number.isFinite(value['totalPrice'])) {
    throw new Error(`${source} has no numeric "totalPrice".`);
  }
  for (const field of BOOKING_TEXT_FIELDS) {
    if (typeof value[field] !== 'string') {
      throw new Error(`${source} has no string "${field}".`);
    }
  }
  return value as unknown as Booking;
}

/**
 * Reads a booking list from a parsed `GET /api/bookings` body.
 *
 * @param value - The parsed JSON.
 * @param source - What `value` is, for the error message.
 * @throws Error when `value` is not an array, or an entry is not a booking.
 */
function readBookingList(value: unknown, source: string): Booking[] {
  if (!Array.isArray(value)) {
    throw new Error(`${source} is not an array.`);
  }
  return (value as unknown[]).map((entry, i) => readBooking(entry, `${source} entry ${i}`));
}

/** The visible columns of a My bookings row, in order; a last cell holds only "Cancel". */
const BOOKING_COLUMNS: readonly string[] = [
  'hotel',
  'room type',
  'room',
  'check-in',
  'check-out',
  'nights',
  'guests',
  'total',
  'status',
];

/**
 * Asserts that each cell of booking `id`'s row on My bookings that
 * {@link BOOKING_COLUMNS} names is visible and has its exact text, column by
 * column. A match on the row's whole text would not do: the "Cancel" button's
 * visually hidden name repeats the hotel, room type, room and dates, so it
 * would pass with those cells broken. A cell's text alone would not do
 * either, because a text match also reads a cell the page hides.
 *
 * @param texts - The text each of {@link BOOKING_COLUMNS} must show, in order.
 * @throws Error when `texts` does not hold one text per column.
 */
async function expectBookingRow(page: Page, id: number, texts: readonly string[]): Promise<void> {
  if (texts.length !== BOOKING_COLUMNS.length) {
    throw new Error(`expectBookingRow needs ${BOOKING_COLUMNS.length} cell texts, got ${texts.length}.`);
  }
  const cells = page.getByTestId(`booking-row-${id}`).locator('td');
  await expect(cells, `booking ${id}: cells in the row`).toHaveCount(BOOKING_COLUMNS.length + 1);
  for (const [i, column] of BOOKING_COLUMNS.entries()) {
    await expect(cells.nth(i), `booking ${id}: ${column}`).toBeVisible();
    await expect(cells.nth(i), `booking ${id}: ${column}`).toHaveText(texts[i]);
  }
  await expect(cells.nth(BOOKING_COLUMNS.indexOf('total')), `booking ${id}: total cell`).toHaveAttribute(
    'data-testid',
    `booking-total-${id}`,
  );
  await expect(cells.nth(BOOKING_COLUMNS.indexOf('status')), `booking ${id}: status cell`).toHaveAttribute(
    'data-testid',
    `booking-status-${id}`,
  );
}

/**
 * Names an error by its type only: the message of a request error carries
 * Playwright's call log, which can include the Authorization header.
 */
function errorKind(err: unknown): string {
  return err instanceof Error ? err.name : typeof err;
}

/**
 * Sends one API request of a test's arrangement and parses its JSON body.
 * Any error of the request or the parsing is rethrown naming only `step` and
 * the error type, for the reason {@link errorKind} gives.
 *
 * @param step - What the request does, for the error message.
 * @param send - Sends the request.
 * @returns The response's status and parsed body.
 */
async function arrange(
  step: string,
  send: () => Promise<APIResponse>,
): Promise<{ status: number; body: unknown }> {
  let response: APIResponse;
  try {
    response = await send();
  } catch (err) {
    throw new Error(`The API request for ${step} failed (${errorKind(err)}).`);
  }
  try {
    return { status: response.status(), body: (await response.json()) as unknown };
  } catch (err) {
    throw new Error(`The API response for ${step} (HTTP ${response.status()}) had no JSON body (${errorKind(err)}).`);
  }
}

/** How long a cleanup hook waits for a create still in flight to settle. */
const CREATE_SETTLE_DEADLINE_MS = 15_000;

/** The prefix of the journey's cleanup warnings. */
const JOURNEY_CLEANUP = 'Booking journey cleanup';

/** The prefix of the cancel-boundary test's cleanup warnings. */
const BOUNDARY_CLEANUP = 'Cancel boundary cleanup';

/**
 * Waits for a tracked create ({@link GuestRun.create}) to settle and reads the
 * id of the booking it created, both within one deadline of
 * {@link CREATE_SETTLE_DEADLINE_MS}: the body read gets only the time the
 * settle left, so a 201 whose body never arrives cannot hold the cleanup.
 *
 * It never throws. Each uncertain or unexpected outcome is logged as a
 * warning prefixed with `label` and naming the HTTP status or the error type,
 * never a response body: the deadline passed before the create was answered
 * or before its 201 body was read, no response can arrive any more, the
 * create did not answer 201, or its 201 body could not be read or held no
 * booking id.
 *
 * @returns The created booking's id, or `null` when there is none to report.
 */
async function settledCreateId(create: Promise<CreateResponse | null>, label: string): Promise<number | null> {
  const deadline = Date.now() + CREATE_SETTLE_DEADLINE_MS;
  const response = await settleWithin(create, CREATE_SETTLE_DEADLINE_MS);
  if (response === DEADLINE_PASSED) {
    console.warn(
      `${label}: the create request was not answered within ${CREATE_SETTLE_DEADLINE_MS / 1000} s; ` +
        "a booking it commits after the cleanup lists the guest's bookings stays Confirmed.",
    );
    return null;
  }
  if (response === null) {
    console.warn(`${label}: the create request's outcome is unknown, because no response can arrive any more.`);
    return null;
  }
  if (response.status() !== 201) {
    console.warn(`${label}: the create request returned HTTP ${response.status()}.`);
    return null;
  }
  const reading: Promise<unknown> = response.json();
  // A read still pending at the deadline is abandoned; it may reject later,
  // when the page closes, and that rejection must not surface as unhandled.
  void reading.catch(() => undefined);
  try {
    const body = await settleWithin(reading, Math.max(0, deadline - Date.now()));
    if (body === DEADLINE_PASSED) {
      console.warn(
        `${label}: the create request's 201 body was not read within ${CREATE_SETTLE_DEADLINE_MS / 1000} s; ` +
          "the booking's id is unknown, so the guest's list is not checked for it.",
      );
      return null;
    }
    const id = isRecord(body) ? body['id'] : undefined;
    if (typeof id === 'number' && Number.isInteger(id) && id > 0) {
      return id;
    }
    console.warn(`${label}: the create request's 201 body held no booking id.`);
  } catch (err) {
    console.warn(`${label}: the create request's 201 body could not be read (${errorKind(err)}).`);
  }
  return null;
}

/**
 * Cancels, through the API, every `Confirmed` booking a run's guest still
 * holds. The guest is unique to the run, so each one is the run's own
 * booking, including one created before the page showed its id. The run's
 * tracked create is awaited first ({@link settledCreateId}), so the list is
 * taken only once that create has committed or failed, or once the wait for
 * it has ended with a warning; a list taken earlier would miss a booking the
 * create commits later.
 *
 * It never throws, so the run's own failure stays the one reported. Each
 * problem is logged as a warning prefixed with `label` and naming the step,
 * the HTTP status, a booking id or the error type, never the email, the
 * password, the token or a response body: a failed login, a list that is not
 * an array (it says nothing about what the guest holds), a listed entry
 * without a numeric id, a listed booking whose status is neither `Confirmed`
 * nor `Cancelled` (it is left as it is), a booking the run created that the
 * list does not show, and a cancel not answered 204.
 *
 * @param guest - The run's guest and its tracked create.
 * @param knownIds - The bookings the run already knows it created.
 * @param label - The prefix of every warning.
 */
async function cancelLeftoverBookings(
  request: APIRequestContext,
  guest: GuestRun,
  knownIds: readonly number[],
  label: string,
): Promise<void> {
  const expectedIds = new Set(knownIds);
  if (guest.create !== null) {
    const createdId = await settledCreateId(guest.create, label);
    if (createdId !== null) {
      expectedIds.add(createdId);
    }
  }

  const api = apiUrl();
  let step = 'logging in';
  try {
    const login = await request.post(`${api}/auth/login`, {
      data: { email: guest.email, password: guest.password },
    });
    if (!login.ok()) {
      console.warn(`${label}: login returned HTTP ${login.status()}; a Confirmed booking may remain.`);
      return;
    }
    const session: unknown = await login.json();
    const token = isRecord(session) ? session['token'] : undefined;
    if (typeof token !== 'string') {
      console.warn(`${label}: the login response held no token; a Confirmed booking may remain.`);
      return;
    }
    const headers = { Authorization: `Bearer ${token}` };

    step = 'listing bookings';
    const list = await request.get(`${api}/bookings`, { headers });
    if (!list.ok()) {
      console.warn(`${label}: listing bookings returned HTTP ${list.status()}; a Confirmed booking may remain.`);
      return;
    }
    const bookings: unknown = await list.json();
    if (!Array.isArray(bookings)) {
      console.warn(`${label}: listing bookings returned a body that is not an array; a Confirmed booking may remain.`);
      return;
    }
    const listedIds = new Set<number>();
    const confirmedIds: number[] = [];
    let unreadable = 0;
    for (const booking of bookings as unknown[]) {
      if (!isRecord(booking) || typeof booking['id'] !== 'number') {
        unreadable += 1;
        continue;
      }
      listedIds.add(booking['id']);
      if (booking['status'] === 'Confirmed') {
        confirmedIds.push(booking['id']);
      } else if (booking['status'] !== 'Cancelled') {
        console.warn(`${label}: booking ${booking['id']} has no recognised status; it may remain Confirmed.`);
      }
    }
    if (unreadable > 0) {
      console.warn(`${label}: ${unreadable} listed booking(s) had no numeric id; a Confirmed booking may remain.`);
    }
    for (const id of expectedIds) {
      if (!listedIds.has(id)) {
        console.warn(`${label}: booking ${id}, which the run created, is not in the guest's list; it may remain Confirmed.`);
      }
    }

    for (const id of confirmedIds) {
      step = `cancelling booking ${id}`;
      const response = await request.delete(`${api}/bookings/${id}`, { headers });
      if (response.status() !== 204) {
        console.warn(`${label}: cancelling booking ${id} returned HTTP ${response.status()}.`);
      }
    }
  } catch (err) {
    console.warn(`${label} failed while ${step} (${errorKind(err)}); a Confirmed booking may remain.`);
  }
}

test.afterEach(async ({ request }) => {
  const state = journey;
  journey = null;
  if (state !== null && state.create !== null && !state.cancelled) {
    await cancelLeftoverBookings(request, state, [], JOURNEY_CLEANUP);
  }
});

test('guest books a room, sees it, and cancels it', async ({ page }) => {
  test.setTimeout(120_000);

  const city = 'Seattle';
  const [checkIn, checkOut] = utcDates(14, 16);
  const guests = 2;
  const criteria = { city, checkIn, checkOut, guests };
  const email = uniqueEmail();
  const fullName = 'Journey Guest';

  const state: JourneyState = { email, password: TEST_PASSWORD, create: null, cancelled: false };
  journey = state;

  /** Asserts the hotel detail URL for `hotelId` carries the searched stay. */
  const expectHotelUrl = async (hotelId: number): Promise<void> => {
    await expect(page).toHaveURL(new RegExp(`/hotels/${hotelId}\\?`));
    const query = new URL(page.url()).searchParams;
    expect(query.get('checkIn')).toBe(checkIn);
    expect(query.get('checkOut')).toBe(checkOut);
    expect(query.get('guests')).toBe(String(guests));
  };

  // Installed before the first page.goto, so it records every inline error
  // of the journey, including one a navigation removes before a step's
  // expectNoInlineError runs.
  const inlineErrors = await watchInlineErrors(page);

  /** Asserts that no inline error has appeared anywhere in the journey so far. */
  const expectNoInlineErrorSoFar = async (): Promise<void> => {
    expect(
      await inlineErrors.appearances(),
      'paths where an inline error appeared during the journey',
    ).toEqual([]);
  };

  await test.step('register', async () => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/search$/);
    await page.getByTestId('nav-register').click();
    await expect(page).toHaveURL(/\/register$/);
    await registerGuest(page, { email, password: TEST_PASSWORD, fullName });
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
  });

  await test.step('log in', async () => {
    await expect(page.getByTestId('login-notice')).toHaveText('Account created. Log in to continue.');
    await expectControlValue(page, 'login-email', email);
    await logIn(page, email, TEST_PASSWORD);
    await expect(page).toHaveURL(/\/search$/);
    await expectSignedInAs(page, email);
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
  });

  await test.step('search', async () => {
    await searchHotels(page, criteria);
    await expect(page.locator('[data-testid^="hotel-result-"]').first()).toBeVisible();
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
  });

  const { hotelId, hotelName } = await test.step('open a hotel', async () => {
    const link = page.locator('[data-testid^="view-rooms-"]').first();
    const id = idFromTestId(await link.getAttribute('data-testid'), 'view-rooms-');
    await link.click();
    await expectHotelUrl(id);
    const heading = page.getByTestId('hotel-name');
    await expect(heading).toBeVisible();
    const name = (await heading.innerText()).trim();
    expect(name, 'hotel name').not.toBe('');
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
    return { hotelId: id, hotelName: name };
  });

  const roomType = await test.step('pick a bookable room type', async () => {
    const rows = page.locator('[data-testid^="room-type-row-"]');
    await expect(rows.first()).toBeVisible();
    const rowCount = await rows.count();
    for (let i = 0; i < rowCount; i += 1) {
      const row = rows.nth(i);
      const id = idFromTestId(await row.getAttribute('data-testid'), 'room-type-row-');
      const available = await readAvailableCount(page, id);
      if (available > 0 && (await page.getByTestId(`book-${id}`).isEnabled())) {
        const picked = {
          id,
          available,
          name: (await row.locator('td').first().innerText()).trim(),
          stayTotal: (await page.getByTestId(`stay-total-${id}`).innerText()).trim(),
        };
        await expectNoInlineErrorSoFar();
        return picked;
      }
    }
    throw new Error(
      `${hotelName} has no room type with a free room bookable for ${guests} guests from ${checkIn} to ${checkOut}.`,
    );
  });

  const created = await test.step('book', async () => {
    // Tracked before the click, so a fast response cannot be missed and the
    // cleanup hook can wait for a create this step leaves in flight.
    const create = trackApiResponse(page, 'POST', '/bookings');
    state.create = create;
    await page.getByTestId(`book-${roomType.id}`).click();
    const response = await settleWithin(create, RESPONSE_TIMEOUT_MS);
    if (response === DEADLINE_PASSED || response === null) {
      throw new Error(`"Book" got no response to POST /api/bookings within ${RESPONSE_TIMEOUT_MS / 1000} s.`);
    }
    expect(response.status(), 'POST /api/bookings status').toBe(201);
    const booking = readBooking(await response.json(), 'The POST /api/bookings body');
    expect(booking, 'the booking the 201 created').toMatchObject({
      hotelId,
      hotelName,
      roomTypeId: roomType.id,
      roomTypeName: roomType.name,
      checkIn,
      checkOut,
      nights: 2,
      guests,
      status: 'Confirmed',
    });
    expect(booking.roomNumber.trim(), 'the room number the server assigned').not.toBe('');

    await expect(page).toHaveURL(/\/bookings$/);
    const confirmation = page.getByTestId('booking-confirmation');
    await expect(confirmation).toBeVisible();
    await expect(confirmation).toContainText(hotelName);
    await expect(confirmation).toContainText(roomType.name);
    await expect(confirmation.locator('p').first(), 'the confirmation names the booked room').toHaveText(
      `${hotelName}, ${roomType.name}, room ${booking.roomNumber}`,
    );
    await expect(confirmation).toContainText('No payment is taken online in this version.');
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
    return booking;
  });

  /** The text each visible cell of the booking's row must show, with `status` last. */
  const bookingRowTexts = (status: string): string[] => [
    hotelName,
    roomType.name,
    created.roomNumber,
    // The row shows the searched dates in the app's display format.
    displayDate(checkIn),
    displayDate(checkOut),
    '2 nights',
    `${guests} guests`,
    roomType.stayTotal,
    status,
  ];

  const bookingId = await test.step('verify the booking in My bookings', async () => {
    const rows = page.locator('[data-testid^="booking-row-"]');
    await expect(rows).toHaveCount(1);
    // The one row must be the booking the 201 created, not merely the row
    // the page happens to list first.
    const id = created.id;
    await expect(rows.first(), 'the only booking row').toHaveAttribute('data-testid', `booking-row-${id}`);
    await expectBookingRow(page, id, bookingRowTexts('Confirmed'));
    await expect(page.getByTestId(`booking-status-${id}`)).toHaveText('Confirmed');
    // The server computes the booking total; it must equal the stay total the
    // detail page showed for the same stay.
    await expect(page.getByTestId(`booking-total-${id}`)).toHaveText(roomType.stayTotal);
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
    return id;
  });

  /**
   * Repeats the search through the nav bar, opens the same hotel and expects
   * the booked room type's available count to equal `expected`.
   */
  const expectCountAfterRepeatedSearch = async (expected: number): Promise<void> => {
    await page.getByTestId('nav-search').click();
    await expect(page).toHaveURL(/\/search$/);
    await searchHotels(page, criteria);
    await page.getByTestId(`view-rooms-${hotelId}`).click();
    await expectHotelUrl(hotelId);
    await expect(page.getByTestId('hotel-name')).toHaveText(hotelName);
    await expect
      .poll(() => readAvailableCount(page, roomType.id), {
        message: `available ${roomType.name} rooms at ${hotelName}`,
      })
      .toBe(expected);
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
  };

  await test.step('recount after booking', async () => {
    await expectCountAfterRepeatedSearch(roomType.available - 1);
  });

  await test.step('cancel the booking', async () => {
    const entryList = waitForApiResponse(page, 'GET', '/bookings');
    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/bookings$/);
    expect((await entryList).status(), 'GET /api/bookings status on entry').toBe(200);
    const cancel = page.getByTestId(`cancel-${bookingId}`);
    await expect(cancel).toBeEnabled();

    // The page's DELETE /api/bookings/{id} and GET /api/bookings traffic from
    // the click on, in order. Playwright delivers a page's network events in
    // the order the browser reports them, so a GET logged after "DELETE
    // answered" was sent after the page received the DELETE's response. The
    // listeners are attached before the waiters, so every response a waiter
    // returns is logged too.
    const deletePath = `/bookings/${bookingId}`;
    const traffic: string[] = [];
    const logRequest = (request: Request): void => {
      if (isApiRequest(request, 'DELETE', deletePath)) {
        traffic.push('DELETE sent');
      } else if (isApiRequest(request, 'GET', '/bookings')) {
        traffic.push('GET sent');
      }
    };
    const logResponse = (response: Response): void => {
      if (isApiResponse(response, 'DELETE', deletePath)) {
        traffic.push(`DELETE answered ${response.status()}`);
      } else if (isApiResponse(response, 'GET', '/bookings')) {
        traffic.push(`GET answered ${response.status()}`);
      }
    };
    page.on('request', logRequest);
    page.on('response', logResponse);
    try {
      // Both waiters are registered before the click. The entry list has been
      // answered already, so the next GET /api/bookings answered is meant to
      // be the reload the 204 starts; the traffic log proves it is.
      const deleted = waitForApiResponse(page, 'DELETE', deletePath);
      const reloaded = waitForApiResponse(page, 'GET', '/bookings');
      await cancel.click();
      expect((await deleted).status(), 'DELETE /api/bookings/{id} status').toBe(204);

      // The page marks the row Cancelled from the 204 alone, before its reload
      // answers, so the row proves the reload only once the reload itself has
      // listed the booking Cancelled.
      const reload = await reloaded;
      expect(reload.status(), 'GET /api/bookings status after the cancel').toBe(200);
      // One GET so far, sent only after the 204 arrived, so its answer is the
      // response `reloaded` returned. A GET sent before the DELETE was
      // answered, whoever sent it, fails here.
      expect(traffic, "the cancel's API requests and responses, in order").toEqual([
        'DELETE sent',
        'DELETE answered 204',
        'GET sent',
        'GET answered 200',
      ]);
      const listed = readBookingList(await reload.json(), 'The GET /api/bookings body after the cancel');
      expect(
        listed.find((b) => b.id === bookingId)?.status,
        'the status the reloaded list gives the cancelled booking',
      ).toBe('Cancelled');
      state.cancelled = true;
    } finally {
      page.off('request', logRequest);
      page.off('response', logResponse);
    }

    await expect(page.getByTestId(`booking-status-${bookingId}`)).toHaveText('Cancelled');
    await expectBookingRow(page, bookingId, bookingRowTexts('Cancelled'));
    await expect(cancel).toHaveCount(0);
    await expectNoInlineError(page);
    await expectNoInlineErrorSoFar();
  });

  await test.step('recount after cancelling', async () => {
    await expectCountAfterRepeatedSearch(roomType.available);
  });

  // Criterion 1: no inline error appeared at any step of the journey.
  await expectNoInlineErrorSoFar();
});

test.describe('cancel eligibility at the UTC date boundary', () => {
  // UTC+14 all year, with no daylight saving: from 10:00 UTC on, its local
  // date is already the next UTC date.
  test.use({ timezoneId: 'Pacific/Kiritimati' });

  test.afterEach(async ({ request }) => {
    const state = boundary;
    boundary = null;
    if (state !== null) {
      // Both stays lie ahead of the server's real UTC date, so both bookings
      // can always be cancelled.
      await cancelLeftoverBookings(request, state, state.bookingIds, BOUNDARY_CLEANUP);
    }
  });

  test("Cancel is offered from today's UTC check-in, not yesterday's, in a zone whose local date differs", async ({
    page,
    request,
  }) => {
    test.setTimeout(60_000);

    // One reading of the clock: booking A stays d1 to d2 and booking B d2 to
    // d3. The page's clock is later set to noon UTC on d2, when the UTC date
    // is d2 (A's check-in yesterday, B's today) and the local date is d3.
    const [d1, d2, d3] = utcDates(1, 2, 3);
    const reference = new Date(`${d2}T12:00:00Z`);
    const email = uniqueEmail();
    const password = TEST_PASSWORD;
    const api = apiUrl();

    const [bookingA, bookingB] = await test.step('arrange bookings A and B through the API', async () => {
      const registered = await arrange('registering the guest', () =>
        request.post(`${api}/auth/register`, { data: { email, password, fullName: 'Boundary Guest' } }),
      );
      expect(registered.status, 'POST /api/auth/register status').toBe(201);

      const login = await arrange('logging in', () =>
        request.post(`${api}/auth/login`, { data: { email, password } }),
      );
      expect(login.status, 'POST /api/auth/login status').toBe(200);
      const token = isRecord(login.body) ? login.body['token'] : undefined;
      if (typeof token !== 'string' || token === '') {
        throw new Error('The login response held no token.');
      }
      const headers = { Authorization: `Bearer ${token}` };

      const stay = { checkIn: d1, checkOut: d3, guests: 1 };
      const search = await arrange('searching Seattle', () =>
        request.get(`${api}/hotels`, { params: { city: 'Seattle', ...stay } }),
      );
      expect(search.status, 'GET /api/hotels status').toBe(200);
      const first: unknown = Array.isArray(search.body) ? (search.body as unknown[])[0] : undefined;
      const hotelId = isRecord(first) ? first['id'] : undefined;
      if (typeof hotelId !== 'number') {
        throw new Error('The Seattle search listed no hotel with an id.');
      }

      const detail = await arrange('reading the hotel', () =>
        request.get(`${api}/hotels/${hotelId}`, { params: stay }),
      );
      expect(detail.status, 'GET /api/hotels/{id} status').toBe(200);
      const roomTypes: unknown[] =
        isRecord(detail.body) && Array.isArray(detail.body['roomTypes']) ? detail.body['roomTypes'] : [];
      const roomType = roomTypes.find(
        (t): t is Record<string, unknown> =>
          isRecord(t) &&
          typeof t['id'] === 'number' &&
          typeof t['availableRooms'] === 'number' &&
          t['availableRooms'] >= 1,
      );
      if (roomType === undefined) {
        throw new Error(`Hotel ${hotelId} has no room type with a free room from ${d1} to ${d3}.`);
      }

      const state: BoundaryState = { email, password, create: null, bookingIds: [] };
      boundary = state;
      const book = async (label: string, checkIn: string, checkOut: string): Promise<number> => {
        const sending = request.post(`${api}/bookings`, {
          headers,
          data: { hotelId, roomTypeId: roomType['id'], checkIn, checkOut, guests: 1 },
        });
        // Tracked while in flight, so the cleanup hook waits for a create the
        // test leaves pending.
        state.create = settledOrNull(sending);
        const created = await arrange(`creating booking ${label}`, () => sending);
        expect(created.status, `POST /api/bookings status for booking ${label}`).toBe(201);
        const booking = readBooking(created.body, `The POST /api/bookings body for booking ${label}`);
        state.bookingIds.push(booking.id);
        expect(booking, `booking ${label}`).toMatchObject({ checkIn, checkOut, status: 'Confirmed' });
        return booking.id;
      };
      return [await book('A', d1, d2), await book('B', d2, d3)];
    });

    // Installed before the app loads, so the app's Date and timers run on a
    // clock the test can set. Until it is set it keeps pace with real time.
    await page.clock.install();

    await test.step('log in and open My bookings: both bookings offer Cancel', async () => {
      await page.goto('/login');
      await logIn(page, email, password);
      await expect(page).toHaveURL(/\/search$/);
      const entryList = waitForApiResponse(page, 'GET', '/bookings');
      await page.getByTestId('nav-bookings').click();
      await expect(page).toHaveURL(/\/bookings$/);
      expect((await entryList).status(), 'GET /api/bookings status on entry').toBe(200);
      await expect(page.locator('[data-testid^="booking-row-"]')).toHaveCount(2);
      for (const id of [bookingA, bookingB]) {
        await expect(page.getByTestId(`booking-row-${id}`)).toBeVisible();
        await expect(page.getByTestId(`cancel-${id}`)).toBeVisible();
      }
    });

    await test.step("set the page clock to noon UTC on booking B's check-in day", async () => {
      // setSystemTime moves the wall clock only and fires no timer, so the
      // session's expiry timer and the page's UTC-midnight timer stay pending.
      // A window focus is one of the page's own triggers to re-read the date.
      await page.clock.setSystemTime(reference);
      await page.evaluate(() => window.dispatchEvent(new Event('focus')));
      const pageDates = await page.evaluate(() => {
        const now = new Date();
        const pad = (n: number): string => String(n).padStart(2, '0');
        return {
          utc: now.toISOString().slice(0, 10),
          local: `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`,
        };
      });
      // The precondition: the page's UTC date is d2 while its local date is d3.
      expect(pageDates, "the page clock's UTC and local dates").toEqual({ utc: d2, local: d3 });
    });

    await test.step('only booking B, checking in today (UTC), offers Cancel', async () => {
      // Waits for the refreshed date to render: A's check-in is yesterday (UTC).
      await expect(page.getByTestId(`cancel-${bookingA}`)).toHaveCount(0);
      // A stay starting today can be cancelled until the end of today (UTC).
      await expect(page.getByTestId(`cancel-${bookingB}`)).toBeVisible();
      await expect(page.getByTestId(`cancel-${bookingB}`)).toBeEnabled();
      await expect(page.getByTestId(`booking-status-${bookingA}`)).toHaveText('Confirmed');
      await expect(page.getByTestId(`booking-status-${bookingB}`)).toHaveText('Confirmed');
    });
  });
});
