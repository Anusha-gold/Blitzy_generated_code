/**
 * The booking journey: success criteria 1 and 2.
 *
 * Criterion 1: a new guest registers, logs in, searches Seattle for a 2-night
 * stay, opens a hotel, books a room and sees the booking in "My bookings",
 * all through the web app. After the first `page.goto('/')` every transition
 * is a click.
 *
 * Criterion 2: the same search then shows one fewer available room of the
 * booked type, and after cancelling the count returns to its original value.
 * Search results carry only each hotel's lowest price, so the count is read on
 * the hotel detail page reached from the repeated search.
 *
 * On the way the web app calls all seven interface endpoints along the real
 * request path. The journey ends with its booking cancelled; when it fails
 * before that, the `afterEach` hook cancels through the API whatever the guest
 * still holds, so no `Confirmed` booking is left behind on a reused stack.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';

import {
  TEST_PASSWORD,
  apiUrl,
  expectNoInlineError,
  logIn,
  readAvailableCount,
  registerGuest,
  searchHotels,
  uniqueEmail,
  utcDate,
} from './support/helpers';

/** What the cleanup hook needs to know about the run in progress. */
interface JourneyState {
  /** The guest the run registered. */
  email: string;
  /** Set just before "Book" is clicked, so a booking may exist from then on. */
  bookRequested: boolean;
  /** Set once "My bookings" shows the booking `Cancelled`. */
  cancelled: boolean;
}

/**
 * The state of the run in progress, or `null` outside one. A module variable
 * rather than a test local, because the `afterEach` hook must still reach it
 * when the test body times out and never returns; the suite runs one test at
 * a time (`workers: 1`).
 */
let journey: JourneyState | null = null;

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

/**
 * Cancels, through the API, every `Confirmed` booking the journey's guest
 * still holds. The guest is unique to the run, so each one is the journey's
 * own booking, including one created before the page showed its id.
 *
 * It never throws, so the journey's own failure stays the one reported. Each
 * problem is logged as a warning naming the step and the HTTP status or the
 * error type, never the email, the token or a response body.
 */
async function cancelLeftoverBookings(request: APIRequestContext, email: string): Promise<void> {
  const api = apiUrl();
  let step = 'logging in';
  try {
    const login = await request.post(`${api}/auth/login`, {
      data: { email, password: TEST_PASSWORD },
    });
    if (!login.ok()) {
      console.warn(`Booking journey cleanup: login returned HTTP ${login.status()}; a Confirmed booking may remain.`);
      return;
    }
    const session: unknown = await login.json();
    const token = isRecord(session) ? session['token'] : undefined;
    if (typeof token !== 'string') {
      console.warn('Booking journey cleanup: the login response held no token; a Confirmed booking may remain.');
      return;
    }
    const headers = { Authorization: `Bearer ${token}` };

    step = 'listing bookings';
    const list = await request.get(`${api}/bookings`, { headers });
    if (!list.ok()) {
      console.warn(`Booking journey cleanup: listing bookings returned HTTP ${list.status()}; a Confirmed booking may remain.`);
      return;
    }
    const bookings: unknown = await list.json();
    const confirmedIds: number[] = [];
    if (Array.isArray(bookings)) {
      for (const booking of bookings as unknown[]) {
        if (isRecord(booking) && booking['status'] === 'Confirmed' && typeof booking['id'] === 'number') {
          confirmedIds.push(booking['id']);
        }
      }
    }

    for (const id of confirmedIds) {
      step = `cancelling booking ${id}`;
      const response = await request.delete(`${api}/bookings/${id}`, { headers });
      if (response.status() !== 204) {
        console.warn(`Booking journey cleanup: cancelling booking ${id} returned HTTP ${response.status()}.`);
      }
    }
  } catch (err) {
    // Only the error type is printed: a request error's full message carries
    // Playwright's call log, which can include the Authorization header.
    const kind = err instanceof Error ? err.name : typeof err;
    console.warn(`Booking journey cleanup failed while ${step} (${kind}); a Confirmed booking may remain.`);
  }
}

test.afterEach(async ({ request }) => {
  const state = journey;
  journey = null;
  if (state !== null && state.bookRequested && !state.cancelled) {
    await cancelLeftoverBookings(request, state.email);
  }
});

test('guest books a room, sees it, and cancels it', async ({ page }) => {
  test.setTimeout(120_000);

  const city = 'Seattle';
  const checkIn = utcDate(14);
  const checkOut = utcDate(16);
  const guests = 2;
  const criteria = { city, checkIn, checkOut, guests };
  const email = uniqueEmail();
  const fullName = 'Journey Guest';

  const state: JourneyState = { email, bookRequested: false, cancelled: false };
  journey = state;

  /** Asserts the hotel detail URL for `hotelId` carries the searched stay. */
  const expectHotelUrl = async (hotelId: number): Promise<void> => {
    await expect(page).toHaveURL(new RegExp(`/hotels/${hotelId}\\?`));
    const query = new URL(page.url()).searchParams;
    expect(query.get('checkIn')).toBe(checkIn);
    expect(query.get('checkOut')).toBe(checkOut);
    expect(query.get('guests')).toBe(String(guests));
  };

  await test.step('register', async () => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/search$/);
    await page.getByTestId('nav-register').click();
    await expect(page).toHaveURL(/\/register$/);
    await registerGuest(page, { email, password: TEST_PASSWORD, fullName });
    await expectNoInlineError(page);
  });

  await test.step('log in', async () => {
    await expect(page.getByTestId('login-notice')).toHaveText('Account created. Log in to continue.');
    await expect(page.getByTestId('login-email')).toHaveValue(email);
    await logIn(page, email, TEST_PASSWORD);
    await expect(page).toHaveURL(/\/search$/);
    await expect(page.getByTestId('nav-email')).toHaveText(email);
    await expectNoInlineError(page);
  });

  await test.step('search', async () => {
    await searchHotels(page, criteria);
    await expect(page.locator('[data-testid^="hotel-result-"]').first()).toBeVisible();
    await expectNoInlineError(page);
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
        return {
          id,
          available,
          name: (await row.locator('td').first().innerText()).trim(),
          stayTotal: (await page.getByTestId(`stay-total-${id}`).innerText()).trim(),
        };
      }
    }
    throw new Error(
      `${hotelName} has no room type with a free room bookable for ${guests} guests from ${checkIn} to ${checkOut}.`,
    );
  });

  await test.step('book', async () => {
    state.bookRequested = true;
    await page.getByTestId(`book-${roomType.id}`).click();
    await expect(page).toHaveURL(/\/bookings$/);
    const confirmation = page.getByTestId('booking-confirmation');
    await expect(confirmation).toBeVisible();
    await expect(confirmation).toContainText(hotelName);
    await expect(confirmation).toContainText(roomType.name);
    await expect(confirmation).toContainText('No payment is taken online in this version.');
    await expectNoInlineError(page);
  });

  const bookingId = await test.step('verify the booking in My bookings', async () => {
    const rows = page.locator('[data-testid^="booking-row-"]');
    await expect(rows).toHaveCount(1);
    const id = idFromTestId(await rows.first().getAttribute('data-testid'), 'booking-row-');
    const row = page.getByTestId(`booking-row-${id}`);
    for (const text of [hotelName, roomType.name, checkIn, checkOut, '2 nights', `${guests} guests`]) {
      await expect(row).toContainText(text);
    }
    await expect(page.getByTestId(`booking-status-${id}`)).toHaveText('Confirmed');
    // The server computes the booking total; it must equal the stay total the
    // detail page showed for the same stay.
    await expect(page.getByTestId(`booking-total-${id}`)).toHaveText(roomType.stayTotal);
    await expectNoInlineError(page);
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
  };

  await test.step('recount after booking', async () => {
    await expectCountAfterRepeatedSearch(roomType.available - 1);
  });

  await test.step('cancel the booking', async () => {
    await page.getByTestId('nav-bookings').click();
    await expect(page).toHaveURL(/\/bookings$/);
    const cancel = page.getByTestId(`cancel-${bookingId}`);
    await cancel.click();
    await expect(page.getByTestId(`booking-status-${bookingId}`)).toHaveText('Cancelled');
    state.cancelled = true;
    await expect(cancel).toHaveCount(0);
    await expectNoInlineError(page);
  });

  await test.step('recount after cancelling', async () => {
    await expectCountAfterRepeatedSearch(roomType.available);
  });
});
