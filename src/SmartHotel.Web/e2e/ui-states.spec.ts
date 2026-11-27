/**
 * The web app's request states (R6) and both branches of the 401 rule,
 * exercised in Chromium against the stack at `baseURL`.
 *
 * R6: the loading indicator while a request is pending, the empty-result
 * message, inline errors for 400, 409 and 401, and the redirect to login when
 * a protected page is opened without a valid session.
 *
 * Loading: a search is held at the network until the test releases it, with
 * no timer, so the indicator and the disabled Search button are asserted while
 * it is pending and gone once its results or its real 400 arrive. A hotel
 * page reused by a history traversal while its "Book" POST is held keeps the
 * indicator up and every "Book" disabled until that POST, and the
 * revalidation it starts, have answered, so the shared counter cannot fall
 * early. Every request still held when a test ends is aborted, so a held
 * booking never reaches the API afterwards.
 *
 * The 401 rule: a token the server rejects before its local expiry cannot be
 * caught by the route guard. These tests make the real API reject one by
 * rewriting the `Authorization` header of the app's own bookings requests,
 * so the 401 comes from the API's JwtBearer validation, not from a stub.
 * Entering My bookings must then redirect to login with the session-expired
 * notice, and "Book" must stay on the hotel page with the 401 shown inline
 * and a "Log in again" link. "Cancel" is an action too: with only its DELETE
 * rejected, after a successful entry load, My bookings stays put and shows
 * the 401 inline with a focused "Log in again" back to
 * `/login?returnUrl=%2Fbookings`, the guest's bookings leave the screen with
 * the session while the booking stays `Confirmed` on the server, no reload
 * follows, and logging in again through that link returns to the list with
 * "Cancel" still offered. In every branch the auth interceptor has cleared
 * the session, so the nav bar offers "Log in" again.
 *
 * Auth and network errors: a password too short for the server's 8 to 128
 * rule shows the register page's 400 inline on the password field alone,
 * without leaving the page. The login form always sends a bindable body, so
 * a contract test rewrites the page's own POST to give `email` a JSON number:
 * the real API's 400, with its `$.email` field error, must show inline and
 * never as wrong credentials. A search the network refuses shows only "The
 * service is unreachable.", and Search again recovers with results.
 *
 * URL criteria: the search and hotel detail pages send the text their URL
 * holds, so the server's own binding judges a deep link such as a guests-only
 * search, `guests=2.0`, `guests=2e0` or an empty `guests`, and its 400 shows
 * inline. A hotel id that is not an integer literal, such as `1.0`, `abc` or a
 * NUL reached through the login page's return URL, matches no API route, so
 * the detail page sends no request and shows the client's local 404 inline,
 * which carries the API's unknown-hotel problem. These cases prove that local
 * answer, not the API's routing of such an id. A detail page reused by one
 * history traversal that changes both its hotel and its stay loads once, for
 * the hotel and stay it lands on.
 *
 * Search form state: the guest count is text, so a typed `2.0`, `2e0`, `1e`
 * or white space reaches the server as typed, on submit and on an unchanged
 * re-submit. A deep link's `guests=2.0` or explicit empty `guests=` stays in
 * the form, and its unchanged re-submit sends that text again: an unedited
 * empty from the URL is the one empty guest count sent. A field the guest
 * leaves empty or clears is left out, and the server uses 1. Leaving a
 * results page for bare `/search` through the brand, nav "Search" or
 * "Log out" resets the form to its initial criteria along with the results.
 *
 * Late bookings: a "Book" answer that arrives after a history traversal has
 * moved the reused hotel page to another stay of the same hotel leaves that
 * stay on screen. A 201 shows a confirmed notice with a link to My bookings
 * and revalidates the counts only when the stays overlap; a 401 shows inside
 * that notice with its own "Log in again", never as the screen's own error.
 *
 * "Book" states: signed out, "Book" sends no request and opens
 * `/login?returnUrl=` carrying the exact hotel path and query, and logging in
 * returns to that hotel and stay. It is disabled on room types too small for
 * the party and on a room type an isolated fixture guest has sold out. When
 * that guest takes the last free room after the page has shown it, "Book"
 * gets the API's real 409, shown above the room table on the same URL, and
 * the revalidation shows no room left and "Book" disabled.
 *
 * "Cancel" conflict: a booking the guest has cancelled elsewhere, through the
 * API, still shows `Confirmed` with "Cancel" on the open My bookings page.
 * Clicking it gets the API's real 409, shown above the list; the list is
 * reloaded, with no other request, so the row reads `Cancelled` without
 * "Cancel" and its status takes the focus the removed button held, the
 * guest's other booking keeps its "Cancel", and the guest stays signed in.
 *
 * Session expiry: with the browser's clock faked and moved past the token's
 * 60 minutes, the nav bar must show the guest signed out on the public page
 * they stayed on, without the app sending any API request, and focus left on
 * "Log out" must move to the "Log in" link that replaces it.
 *
 * Request deadline: a login or registration the API never answers must end,
 * once the faked clock passes the 30-second deadline, in the inline "The
 * service is unreachable." with the loading indicator hidden and the submit
 * button enabled again.
 *
 * Unusable success bodies: a 200 the page cannot read (a login body whose
 * token is not a compact signed JWT or whose expiry is not a real UTC
 * instant, a search body that is not a list of results or holds an id beyond
 * the API's 32-bit integers, a detail body missing its fields or holding a
 * date that is not a real calendar date) must show "The service sent an
 * unexpected response." inline. The guest stays signed out on the login page,
 * and no hotel renders.
 *
 * Fixtures: bookings a test arranges or reads go straight to the API under
 * a guest's own token, which no diagnostic prints, and every booking a test
 * creates, through the API or the web app, is cancelled after it, also when
 * it fails, so no `Confirmed` booking is left on a reused stack.
 *
 * Every test runs in a fresh browser context and therefore starts signed
 * out, because the session lives only in memory.
 */
import {
  expect,
  test,
  type APIRequestContext,
  type APIResponse,
  type Locator,
  type Page,
  type Response,
  type Route,
} from '@playwright/test';

import {
  apiUrl,
  displayDate,
  expectControlValue,
  expectNoInlineError,
  // Some tests fill forms field by field: the form helpers also wait for
  // their success outcome, which these tests must not wait for (a delayed
  // search) or expect never to happen (a rejected registration or login).
  fillBound,
  isApiEndpoint,
  logIn,
  readAvailableCount,
  registerGuest,
  searchHotels,
  uniqueEmail,
  uniquePassword,
  utcDates,
  watchInlineErrors,
} from './support/helpers';

// The positive predicates below match a request only on the target API's
// origin and endpoint path (`isApiEndpoint`), so a web app that calls another
// stack's API fails the waiters and routes built on them; each waiter matches
// its endpoint's exact path and checks the method as well. The recorders that
// assert no request was sent match any origin instead, so a request to
// another API cannot slip past them.

/**
 * The target API's hotel search, `GET /api/hotels?…`, and never the detail
 * `/api/hotels/{id}`. A predicate rather than a glob: Playwright globs match
 * `?` literally and never let `*` cross a `/`.
 */
const isHotelSearch = (url: URL): boolean => isApiEndpoint(url, '/hotels');

/**
 * The target API's bookings endpoints: the list, "Book" and "Cancel". The
 * SPA's own `/bookings` page never matches, because it is on the web origin
 * and its path has no `/api` prefix.
 */
const isBookingsApi = (url: URL): boolean => isApiEndpoint(url, '/bookings', 'withSubPaths');

/**
 * The target API's bookings collection, `/api/bookings` exactly: the list
 * (`GET`) and "Book" (`POST`), never a booking's own `/api/bookings/{id}`.
 */
const isBookingsCollection = (url: URL): boolean => isApiEndpoint(url, '/bookings');

/** The target API's registration endpoint, `/api/auth/register`, never the SPA's `/register` page. */
const isRegisterApi = (url: URL): boolean => isApiEndpoint(url, '/auth/register');

/** The target API's login endpoint, `/api/auth/login`, never the SPA's `/login` page. */
const isLoginApi = (url: URL): boolean => isApiEndpoint(url, '/auth/login');

/**
 * Any API endpoint, on any origin, for asserting that the app sent no API
 * request: matching the target API's origin only would let a request to
 * another API go unseen. The SPA's own routes never match, because none has
 * an `/api` prefix.
 */
const isApi = (url: URL): boolean => url.pathname === '/api' || url.pathname.startsWith('/api/');

/** Every hotel card on the search page. */
const HOTEL_RESULTS = '[data-testid^="hotel-result-"]';

/** Every "View rooms" link on the search page. */
const VIEW_ROOMS = '[data-testid^="view-rooms-"]';

/** Every "Book" button of the hotel detail page's room table. */
const BOOK_BUTTONS = '[data-testid^="book-"]';

/**
 * A deep link whose criteria only the server can judge: the page must send
 * the URL's text unchanged and show the server's answer inline.
 */
interface RawCriteriaCase {
  /** What the URL holds, for the test title. */
  title: string;
  /** The page URL; `{stay}` stands for a valid `checkIn=…&checkOut=…` query. */
  url: string;
  /**
   * The target API endpoint the page's `GET` must request, below the API base
   * and with the hotel id included, as {@link isApiEndpoint} takes it.
   */
  endpoint: string;
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
    endpoint: '/hotels',
    guests: '2',
    status: 400,
  },
  {
    title: 'a search URL with guests=2.0',
    url: '/search?city=Seattle&{stay}&guests=2.0',
    endpoint: '/hotels',
    guests: '2.0',
    status: 400,
  },
  {
    title: 'a search URL with an empty guests',
    url: '/search?city=Seattle&{stay}&guests=',
    endpoint: '/hotels',
    guests: '',
    status: 400,
  },
  {
    title: 'a detail URL with guests=2e0',
    url: '/hotels/1?{stay}&guests=2e0',
    endpoint: '/hotels/1',
    guests: '2e0',
    status: 400,
  },
  {
    title: 'a detail URL with an empty guests',
    url: '/hotels/1?{stay}&guests=',
    endpoint: '/hotels/1',
    guests: '',
    status: 400,
  },
];

/**
 * Hotel ids a deep link can carry that are not integer literals, one test
 * each. The API's `{id:int}` route matches none of them.
 */
const NON_INTEGER_HOTEL_IDS: readonly string[] = ['1.0', 'abc'];

/** The inline title for a request that got no response, its deadline included. */
const UNREACHABLE_TITLE = 'The service is unreachable.';

/** The inline title for a 2xx response whose body the page cannot use. */
const UNEXPECTED_RESPONSE_TITLE = 'The service sent an unexpected response.';

/** A form whose POST the API never answers, so only the request deadline ends the wait. */
interface DeadlineCase {
  /** The page's route and the prefix of its form's test ids. */
  form: 'login' | 'register';
  /** The endpoint the form posts to. */
  isEndpoint: (url: URL) => boolean;
  /** Fills every field of the form, waiting for no outcome. */
  fill: (page: Page) => Promise<void>;
}

/** The forms of the request-deadline test, one test each. */
const DEADLINE_CASES: readonly DeadlineCase[] = [
  {
    form: 'login',
    isEndpoint: isLoginApi,
    fill: async (page) => {
      await fillBound(page, 'login-email', uniqueEmail());
      await fillBound(page, 'login-password', uniquePassword());
    },
  },
  {
    form: 'register',
    isEndpoint: isRegisterApi,
    fill: async (page) => {
      await fillBound(page, 'register-email', uniqueEmail());
      await fillBound(page, 'register-password', uniquePassword());
      await fillBound(page, 'register-full-name', 'States Guest');
    },
  },
];

/** A 200 body the page cannot use, stubbed in place of the API's answer. */
interface UnusableBodyCase {
  /** What the body holds, for the test title. */
  title: string;
  /** The JSON text of the 200 body. */
  body: string;
}

/** Login 200 bodies that carry no usable session, one test each. */
const UNUSABLE_LOGIN_BODIES: readonly UnusableBodyCase[] = [
  { title: 'a null body', body: 'null' },
  { title: 'a blank token and an unparseable expiry', body: '{"token":"","expiresAt":"x"}' },
  {
    title: 'a token that is not a JWT',
    body: '{"token":"not-a-jwt","expiresAt":"2099-01-01T00:00:00Z"}',
  },
  {
    title: 'a JWT-shaped token and an expiry on 30 February',
    body: '{"token":"header.payload.signature","expiresAt":"2099-02-30T00:00:00Z"}',
  },
];

/** A deep link whose page receives a 200 body it cannot render. */
interface UnusableReadCase extends UnusableBodyCase {
  /** The page URL; `{stay}` stands for a valid `checkIn=…&checkOut=…` query. */
  url: string;
  /**
   * The target API endpoint of the page's `GET`, below the API base and with
   * the hotel id included, as {@link isApiEndpoint} takes it.
   */
  endpoint: string;
}

/** The deep links of the unusable-read test, one test each. */
const UNUSABLE_READ_CASES: readonly UnusableReadCase[] = [
  {
    title: 'a search answered with {}',
    url: '/search?city=Seattle&{stay}',
    endpoint: '/hotels',
    body: '{}',
  },
  {
    title: 'a hotel detail answered with {"id":1}',
    url: '/hotels/1?{stay}',
    endpoint: '/hotels/1',
    body: '{"id":1}',
  },
  {
    title: 'a search answered with a result id beyond the 32-bit range',
    url: '/search?city=Seattle&{stay}',
    endpoint: '/hotels',
    body: '[{"id":2147483648,"name":"X","city":"Seattle","address":"Y","lowestNightlyPrice":100}]',
  },
  {
    title: 'a hotel detail answered with checkIn 2099-02-30',
    url: '/hotels/1?{stay}',
    endpoint: '/hotels/1',
    body:
      '{"id":1,"name":"X","city":"Seattle","address":"Y","description":"Z",' +
      '"checkIn":"2099-02-30","checkOut":"2099-03-02","nights":2,"guests":1,"roomTypes":[]}',
  },
];

/**
 * Guest counts typed into the search form that only the server can judge, one
 * test each: a decimal and an exponent spelling of a whole number, an
 * incomplete exponent, and white space, which is not the empty field. A number
 * input would read the first two as 2 and the last two as an empty field.
 */
const TYPED_GUESTS: readonly string[] = ['2.0', '2e0', '1e', ' '];

/**
 * Guest counts a search deep link can carry that only the server can judge,
 * one test each: a decimal spelling of a whole number, and an explicit empty
 * `guests=`, which the API rejects while it gives an absent count 1. The form
 * must hold each unedited and send it again on an unchanged re-submit.
 */
const DEEP_LINK_GUESTS: readonly string[] = ['2.0', ''];

/** The API's message for a supplied `guests` it cannot bind as an integer, an empty one included. */
const GUESTS_INTEGER_MESSAGE = 'The value must be a whole number of at least 1.';

/** The nav bar links to bare `/search`, one test each. */
const BARE_SEARCH_LINKS: readonly string[] = ['nav-brand', 'nav-search'];

/**
 * Records, from now on, every request the page sends to a hotel detail path,
 * `/api/hotels/{id}`, on any origin and whatever its method, and returns the
 * list it fills. It is for asserting that no such request was sent, so it is
 * not limited to the target API's origin.
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
 * Asserts the hotel detail page's answer to a non-integer id: the client's
 * local 404, given without a request and equivalent to the API's answer for an
 * unknown hotel id, is shown inline in place of the content with that answer's
 * title and detail, the page does not report the service as unreachable, and
 * `detailRequests` stayed empty. It checks the client's answer only; the API's
 * own routing of such an id is never exercised.
 */
async function expectLocal404(page: Page, detailRequests: readonly URL[]): Promise<void> {
  const error = page.getByTestId('inline-error');
  await expect(error).toBeVisible();
  await expect(error).toContainText('Hotel not found');
  await expect(error).toContainText('No hotel matches the requested id.');
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
 * A test guest's credentials: a fresh email and the guest's own random
 * password, which no diagnostic prints.
 */
interface GuestCredentials {
  /** The guest's email, from {@link uniqueEmail}. */
  email: string;
  /** The guest's password, from {@link uniquePassword}. */
  password: string;
}

/**
 * Opens the register page, registers a fresh guest with its own generated
 * password and returns both credentials, which the test never prints.
 * The browser is left on `/login?registered=1`.
 */
async function registerNewGuest(page: Page): Promise<GuestCredentials> {
  const email = uniqueEmail();
  const password = uniquePassword();
  await page.goto('/register');
  await registerGuest(page, { email, password, fullName: 'States Guest' });
  return { email, password };
}

/**
 * Registers a fresh guest and logs in. With no `returnUrl`, the login page
 * continues to `/search`, so the browser is left there, signed in. Returns the
 * guest's credentials, which sign the same guest in through the API with
 * {@link apiSignIn}.
 */
async function registerAndLogIn(page: Page): Promise<GuestCredentials> {
  const guest = await registerNewGuest(page);
  await logIn(page, guest.email, guest.password);
  await expect(page).toHaveURL(/\/search$/);
  return guest;
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

/**
 * Fills the search form, without submitting it, with Seattle, a valid stay
 * and `guests` exactly as given. Returns the stay's dates.
 */
async function fillSearchForm(page: Page, guests: string): Promise<{ checkIn: string; checkOut: string }> {
  const [checkIn, checkOut] = utcDates(30, 32);
  await fillBound(page, 'search-city', 'Seattle');
  await fillBound(page, 'search-check-in', checkIn);
  await fillBound(page, 'search-check-out', checkOut);
  await fillBound(page, 'search-guests', guests);
  return { checkIn, checkOut };
}

/**
 * Clicks Search and returns the response to the hotel search it sends. The
 * method filter keeps a CORS preflight from matching.
 */
async function submitSearch(page: Page): Promise<Response> {
  const response = page.waitForResponse(
    (r) => isHotelSearch(new URL(r.url())) && r.request().method() === 'GET',
  );
  await page.getByTestId('search-submit').click();
  return response;
}

/**
 * Asserts the idle search page: bare `/search`, the form holding its initial
 * criteria (no city, no dates, 1 guest), and no result, empty-result message
 * or inline error.
 */
async function expectIdleSearch(page: Page): Promise<void> {
  await expect(page).toHaveURL(/\/search$/);
  await expectControlValue(page, 'search-city', '');
  await expectControlValue(page, 'search-check-in', '');
  await expectControlValue(page, 'search-check-out', '');
  await expectControlValue(page, 'search-guests', '1');
  await expect(page.locator(`${HOTEL_RESULTS}, [data-testid="search-empty"]`)).toHaveCount(0);
  await expectNoInlineError(page);
}

/**
 * A guest's API session, for the fixture requests a test sends to the API
 * directly rather than through the web app: arranging bookings, reading their
 * server state and cancelling them afterwards. The token is held only in
 * `headers` and never appears in a diagnostic.
 */
interface ApiSession {
  /** The `Authorization` header carrying the guest's bearer token. */
  readonly headers: Readonly<Record<string, string>>;
}

/**
 * A stay: its dates as exact `yyyy-MM-dd` text and its party size, as the
 * hotel detail page shows it and both hotel endpoints and
 * `POST /api/bookings` take it.
 */
interface Stay {
  checkIn: string;
  checkOut: string;
  guests: number;
}

/** A booking to create through the API, as `POST /api/bookings` takes it. */
interface FixtureBookingRequest extends Stay {
  hotelId: number;
  roomTypeId: number;
}

/** One room type of a hotel for one stay, as `GET /api/hotels/{id}` reports it. */
interface FixtureRoomType {
  /** The id its `book-<id>` and `available-count-<id>` hooks carry. */
  id: number;
  /** Its name, such as `Standard`, as the 409's detail names it. */
  name: string;
  /** How many guests one of its rooms takes. */
  maxGuests: number;
  /** How many of its rooms are free for the stay, whatever the party size. */
  availableRooms: number;
}

/**
 * The bookings the fixtures created through the API, each with the session
 * that may cancel it. The `afterEach` hook cancels every one still
 * `Confirmed`, also after a failed or timed-out test, so no booking is left
 * behind on a reused stack. A module variable, because the hook must reach it
 * when a test body never returns; the suite runs one test at a time
 * (`workers: 1`). Bookings the web app makes are found by
 * {@link cancelGuestBookingsAfterTest} instead.
 */
const bookingsToCancel: { session: ApiSession; id: number }[] = [];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Sends one fixture request and returns its response. A request that fails
 * without a response is rethrown as an error naming only `step` and the error
 * type: the original message carries Playwright's call log, which can include
 * the `Authorization` header.
 */
async function fixtureRequest(step: string, send: () => Promise<APIResponse>): Promise<APIResponse> {
  try {
    return await send();
  } catch (err) {
    const kind = err instanceof Error ? err.name : typeof err;
    throw new Error(`API fixture: ${step} sent no response (${kind}).`);
  }
}

/**
 * Registers a fresh guest, with its own random password, through the API,
 * without the web app, for a fixture that must hold bookings under an
 * account of its own.
 *
 * @returns The new guest's credentials.
 */
async function apiRegisterGuest(request: APIRequestContext): Promise<GuestCredentials> {
  const guest: GuestCredentials = { email: uniqueEmail(), password: uniquePassword() };
  const response = await fixtureRequest('registering a fixture guest', () =>
    request.post(`${apiUrl()}/auth/register`, {
      data: { email: guest.email, password: guest.password, fullName: 'Fixture Guest' },
    }),
  );
  expect(response.status(), 'the fixture registration returns 201').toBe(201);
  return guest;
}

/**
 * Logs a guest in through the API and returns the session that authorizes the
 * fixture requests. The web app's own session is not affected.
 */
async function apiSignIn(request: APIRequestContext, guest: GuestCredentials): Promise<ApiSession> {
  const response = await fixtureRequest('logging in', () =>
    request.post(`${apiUrl()}/auth/login`, { data: { email: guest.email, password: guest.password } }),
  );
  expect(response.status(), 'the fixture login returns 200').toBe(200);
  const body: unknown = await response.json();
  const token = isRecord(body) ? body['token'] : undefined;
  expect(typeof token === 'string' && token !== '', 'the fixture login returns a token').toBe(true);
  return { headers: { authorization: `Bearer ${String(token)}` } };
}

/**
 * Records a booking for the `afterEach` cancellation, as soon as its id is
 * known and before anything else is asserted about it.
 */
function cancelAfterTest(session: ApiSession, id: number): void {
  bookingsToCancel.push({ session, id });
}

/** A guest {@link cancelGuestBookingsAfterTest} recorded. */
interface GuestToReconcile {
  /** The guest's API session, which lists and cancels its bookings. */
  readonly session: ApiSession;
  /**
   * One promise per `POST /api/bookings` the guest's page sent after the
   * guest was recorded. Each settles once its request has a response or has
   * failed, and none rejects.
   */
  readonly creates: Promise<unknown>[];
}

/**
 * The guests the web app books for, each unique to its test, so every booking
 * one holds is that test's own. A hook lists each guest's bookings through the
 * API after the test and cancels every one still `Confirmed`, also after a
 * failed or timed-out test. The API commits a booking before it answers, so
 * this finds the booking even when the test never decoded its 201. A module
 * variable for the same reason as {@link bookingsToCancel}.
 */
const guestsToReconcile: GuestToReconcile[] = [];

/**
 * Records the guest signed in on `page` for the reconciliation after the test.
 * From now on it follows every `POST /api/bookings` the page sends, so that the
 * reconciliation lists the guest's bookings only once each POST has settled.
 * Call it before the page can send one, that is before "Book" is clicked.
 */
function cancelGuestBookingsAfterTest(page: Page, session: ApiSession): void {
  const guest: GuestToReconcile = { session, creates: [] };
  page.on('request', (sent) => {
    if (sent.method() === 'POST' && isBookingsApi(new URL(sent.url()))) {
      // `response()` resolves with null once the request fails, and rejects
      // when the page closes first; either way the request has settled.
      guest.creates.push(sent.response().catch(() => null));
    }
  });
  guestsToReconcile.push(guest);
}

/**
 * Creates a booking through the API, requires the 201, and records it for the
 * `afterEach` cancellation.
 *
 * @returns The new booking's id.
 */
async function apiBook(request: APIRequestContext, session: ApiSession, booking: FixtureBookingRequest): Promise<number> {
  const response = await fixtureRequest('creating a booking', () =>
    request.post(`${apiUrl()}/bookings`, { headers: session.headers, data: booking }),
  );
  expect(response.status(), 'the fixture booking returns 201').toBe(201);
  const body: unknown = await response.json();
  const id = isRecord(body) ? body['id'] : undefined;
  expect(typeof id === 'number' && Number.isInteger(id), 'the fixture booking returns an id').toBe(true);
  cancelAfterTest(session, Number(id));
  return Number(id);
}

/**
 * Reads, through the API, the server's status of one of the session's
 * bookings.
 *
 * @returns The booking's `status`, such as `Confirmed`, or `null` when the
 *   session's list does not hold it.
 */
async function apiBookingStatus(request: APIRequestContext, session: ApiSession, id: number): Promise<string | null> {
  const response = await fixtureRequest('listing bookings', () =>
    request.get(`${apiUrl()}/bookings`, { headers: session.headers }),
  );
  expect(response.status(), 'the fixture booking list returns 200').toBe(200);
  const list: unknown = await response.json();
  expect(Array.isArray(list), 'the fixture booking list is an array').toBe(true);
  const found = (list as unknown[]).find((b) => isRecord(b) && b['id'] === id);
  return isRecord(found) && typeof found['status'] === 'string' ? found['status'] : null;
}

/**
 * Reads one hotel's room types for a stay through the API, in the server's
 * order. The dates are exact `yyyy-MM-dd` text, so they need no escaping.
 */
async function apiRoomTypes(request: APIRequestContext, hotelId: number, stay: Stay): Promise<FixtureRoomType[]> {
  const query = `checkIn=${stay.checkIn}&checkOut=${stay.checkOut}&guests=${stay.guests}`;
  const response = await fixtureRequest('reading a hotel', () => request.get(`${apiUrl()}/hotels/${hotelId}?${query}`));
  expect(response.status(), 'the fixture hotel read returns 200').toBe(200);
  const body: unknown = await response.json();
  const roomTypes = isRecord(body) ? body['roomTypes'] : undefined;
  expect(Array.isArray(roomTypes), 'the fixture hotel read lists room types').toBe(true);
  return (roomTypes as unknown[]).map((rt) => {
    const valid =
      isRecord(rt) &&
      Number.isInteger(rt['id']) &&
      typeof rt['name'] === 'string' &&
      Number.isInteger(rt['maxGuests']) &&
      Number.isInteger(rt['availableRooms']);
    expect(valid, 'each fixture room type has an id, a name, maxGuests and availableRooms').toBe(true);
    const r = rt as Record<string, unknown>;
    return {
      id: Number(r['id']),
      name: String(r['name']),
      maxGuests: Number(r['maxGuests']),
      availableRooms: Number(r['availableRooms']),
    };
  });
}

/**
 * Cancels one booking after a test, under the session that may cancel it. It
 * never throws. A 204, or a 409 for a booking the test itself already
 * cancelled, ends it; any other status, or a request that fails, is printed as
 * a warning naming only the booking and the HTTP status or the error type.
 */
async function cancelInCleanup(request: APIRequestContext, session: ApiSession, id: number): Promise<void> {
  try {
    const response = await request.delete(`${apiUrl()}/bookings/${id}`, { headers: session.headers });
    // 409 is a booking the test itself already cancelled.
    if (response.status() !== 204 && response.status() !== 409) {
      console.warn(`ui-states cleanup: cancelling booking ${id} returned HTTP ${response.status()}.`);
    }
  } catch (err) {
    // Only the error type is printed: a request error's message carries
    // Playwright's call log, which can include the Authorization header.
    const kind = err instanceof Error ? err.name : typeof err;
    console.warn(`ui-states cleanup: cancelling booking ${id} failed (${kind}); it may remain Confirmed.`);
  }
}

test.afterEach(async ({ request }) => {
  for (const { session, id } of bookingsToCancel.splice(0)) {
    await cancelInCleanup(request, session, id);
  }
});

/**
 * One request the app sends, held in the browser until the test lets it go,
 * so a pending state lasts exactly as long as the test needs to observe it,
 * with no timer.
 */
interface RequestGate {
  /**
   * Waits until the app has sent the request and the gate holds it. When the
   * request never comes, it fails within the expect timeout under `message`,
   * so a missing request is reported as such rather than as a test timeout.
   */
  expectArrival(message: string): Promise<void>;
  /**
   * Sends the held request on to the real API; call it once
   * {@link RequestGate.expectArrival} has passed. Each of `headers` replaces
   * the request header of the same lower-case name.
   */
  release(headers?: Readonly<Record<string, string>>): Promise<void>;
  /**
   * Fails the held request as a network error, so it never reaches the API.
   * Before the request arrives it disarms the gate instead; once the request
   * has been released or aborted it does nothing.
   */
  abort(): Promise<void>;
}

/**
 * The gates of the running test. The `afterEach` hook below aborts any
 * request one still holds, also after a failed or timed-out test, so a held
 * `POST /api/bookings` can never commit a booking once its test has ended. A
 * module variable for the same reason as {@link bookingsToCancel}.
 */
const heldGates: RequestGate[] = [];

/**
 * Holds the first request of `method` whose URL `matches`, from now on. Every
 * other request, a CORS preflight `OPTIONS` to the same URL included, and
 * every later match go on as if the gate were not installed.
 */
async function holdRequest(page: Page, matches: (url: URL) => boolean, method: string): Promise<RequestGate> {
  let held: Route | null = null;
  let arrived = false;
  let done = false;

  await page.route(matches, async (route) => {
    if (done || held !== null || route.request().method() !== method) {
      await route.fallback();
      return;
    }
    held = route;
    arrived = true;
  });

  const take = (): Route | null => {
    const route = held;
    held = null;
    done = true;
    return route;
  };

  const gate: RequestGate = {
    async expectArrival(message) {
      await expect.poll(() => arrived, { message }).toBe(true);
    },
    async release(headers) {
      if (done) {
        throw new Error('This gate has already released or aborted its request.');
      }
      const route = take();
      if (route === null) {
        throw new Error('This gate holds no request yet; release it only once expectArrival has passed.');
      }
      await route.continue(headers === undefined ? undefined : { headers: { ...route.request().headers(), ...headers } });
    },
    async abort() {
      const route = take();
      if (route !== null) {
        await route.abort();
      }
    },
  };
  heldGates.push(gate);
  return gate;
}

test.afterEach(async () => {
  for (const gate of heldGates.splice(0)) {
    try {
      await gate.abort();
    } catch (err) {
      // A route that cannot be aborted belongs to a closed page, whose held
      // request was dropped unsent. Only the error type is printed, because
      // the message's call log can carry the request's headers.
      const kind = err instanceof Error ? err.name : typeof err;
      console.warn(`ui-states cleanup: aborting a held request failed (${kind}).`);
    }
  }
});

/**
 * How long the reconciliation after a test waits for the booking requests a
 * guest's page sent to settle. The API answers one well within it; the bound
 * only keeps a request that never settles from holding the hook.
 */
const CREATES_SETTLE_MS = 10_000;

/**
 * Waits until every one of `settlements` has settled, or `ms` have passed.
 *
 * @returns Whether all of them settled in time.
 */
async function settleWithin(settlements: readonly Promise<unknown>[], ms: number): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<boolean>((resolve) => {
    timer = setTimeout(() => resolve(false), ms);
  });
  try {
    return await Promise.race([Promise.all(settlements).then(() => true), deadline]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Lists a reconciled guest's bookings through the API after a test and
 * returns the ids of those still `Confirmed`. It never throws: a request that
 * fails, a status other than 200, a body that is not a JSON array, or a
 * `Confirmed` entry without an integer id is printed as a warning naming only
 * that step and the HTTP status or the error type, never the token, the email
 * or the body, and leaves out what it could not read.
 */
async function confirmedBookingIds(request: APIRequestContext, session: ApiSession): Promise<number[]> {
  const remain = 'its Confirmed bookings may remain.';
  let list: unknown;
  try {
    const response = await request.get(`${apiUrl()}/bookings`, { headers: session.headers });
    if (response.status() !== 200) {
      console.warn(`ui-states cleanup: listing a guest's bookings returned HTTP ${response.status()}; ${remain}`);
      return [];
    }
    list = await response.json();
  } catch (err) {
    // Only the error type is printed, for the same reason as in cancelInCleanup.
    const kind = err instanceof Error ? err.name : typeof err;
    console.warn(`ui-states cleanup: listing a guest's bookings failed (${kind}); ${remain}`);
    return [];
  }
  if (!Array.isArray(list)) {
    console.warn(`ui-states cleanup: a guest's booking list is not an array; ${remain}`);
    return [];
  }
  const confirmed = (list as unknown[]).filter((b) => isRecord(b) && b['status'] === 'Confirmed');
  const ids = confirmed
    .map((b) => (b as Record<string, unknown>)['id'])
    .filter((id): id is number => typeof id === 'number' && Number.isInteger(id));
  if (ids.length !== confirmed.length) {
    console.warn(
      `ui-states cleanup: ${confirmed.length - ids.length} Confirmed booking(s) in a guest's list carry no ` +
        'integer id and may remain Confirmed.',
    );
  }
  return ids;
}

// Declared after the hook that aborts held requests, so it runs after that
// one: a booking request still held by then never reaches the API, and every
// other is awaited until it settles, so the list already shows any booking it
// committed.
test.afterEach(async ({ request }) => {
  for (const guest of guestsToReconcile.splice(0)) {
    if (!(await settleWithin(guest.creates, CREATES_SETTLE_MS))) {
      console.warn(
        "ui-states cleanup: a guest's booking request had not settled in time; a booking it commits later may remain Confirmed.",
      );
    }
    for (const id of await confirmedBookingIds(request, guest.session)) {
      await cancelInCleanup(request, guest.session, id);
    }
  }
});

/**
 * A search held by a {@link RequestGate}: a valid stay answered with hotels,
 * or a check-out on the check-in day answered with a 400. Either way the
 * indicator and the disabled Search must last until the answer, and neither
 * may stay behind once it is shown.
 */
interface HeldSearchCase {
  /** The answer, for the test title. */
  title: string;
  /** Days from today of check-in and check-out, for one {@link utcDates} call. */
  offsets: readonly [number, number];
  /** The status the API answers. */
  status: number;
  /** The outcome the page shows once the answer arrives. */
  outcome: string;
}

/** The held searches, one test each. */
const HELD_SEARCH_CASES: readonly HeldSearchCase[] = [
  { title: 'results', offsets: [100, 102], status: 200, outcome: HOTEL_RESULTS },
  { title: 'inline 400', offsets: [100, 100], status: 400, outcome: '[data-testid="inline-error"]' },
];

for (const c of HELD_SEARCH_CASES) {
  test(`loading indicator shows and Search is disabled while a held search is pending, until its ${c.title}`, async ({ page }) => {
    const [checkIn, checkOut] = utcDates(...c.offsets);
    const indicator = page.getByTestId('loading-indicator');
    const submit = page.getByTestId('search-submit');

    const search = await test.step('submit a Seattle search and hold its GET', async () => {
      await page.goto('/search');
      await fillBound(page, 'search-city', 'Seattle');
      await fillBound(page, 'search-check-in', checkIn);
      await fillBound(page, 'search-check-out', checkOut);
      await fillBound(page, 'search-guests', '2');
      await expect(indicator).toHaveCount(0);
      // While idle, the slot reserves the banner's height.
      const slotHeight = await page
        .locator('app-loading-indicator')
        .evaluate((slot) => slot.getBoundingClientRect().height);

      const gate = await holdRequest(page, isHotelSearch, 'GET');
      await submit.click();
      await gate.expectArrival('Search sends the GET');
      return { gate, slotHeight };
    });

    await test.step('while the GET is held the indicator shows and Search is disabled', async () => {
      await expect(indicator).toBeVisible();
      await expect(indicator).toHaveAttribute('role', 'progressbar');
      await expect(submit).toBeDisabled();

      // Forced colours drop fills and gradients. The banner keeps a solid edge
      // that takes no space, so it still fills exactly the idle slot, and the
      // sweep keeps its gradient.
      await page.emulateMedia({ forcedColors: 'active' });
      await expect(indicator).toHaveCSS('outline-style', 'solid');
      await expect(indicator.locator('.loading-indicator__bar')).toHaveCSS('background-image', /gradient/);
      expect(await indicator.evaluate((banner) => banner.getBoundingClientRect().height)).toBe(search.slotHeight);
    });

    await test.step(`once released the GET answers ${c.status} and the pending state ends`, async () => {
      const answered = page.waitForResponse(
        (r) => isHotelSearch(new URL(r.url())) && r.request().method() === 'GET',
      );
      await search.gate.release();
      expect((await answered).status()).toBe(c.status);

      await expect(page.locator(c.outcome).first()).toBeVisible();
      await expect(indicator).toHaveCount(0);
      await expect(submit).toBeEnabled();
      // The pending state disabled the focused button; focus returns to it.
      await expect(submit).toBeFocused();
    });
  });
}

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
  // The field error names its field by the visible label, never by the API's key.
  await expect(error).toContainText('Check-out: The date must be after the check-in date.');
  await expect(error).not.toContainText('checkOut');
  await expect(page.locator(HOTEL_RESULTS)).toHaveCount(0);

  // Only the rejected input is marked invalid, described by its own message.
  const checkOut = page.getByTestId('search-check-out');
  await expect(checkOut).toHaveAttribute('aria-invalid', 'true');
  await expect(checkOut).toHaveAccessibleDescription('The date must be after the check-in date.');
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

test('search while the service is unreachable shows the status-0 message, and Search again recovers', async ({ page }) => {
  // Refuses the first search GET at the network level, so it fails with
  // status 0 and no response at all; every later request, a CORS preflight
  // included, goes on to the real API unchanged.
  let refused = false;
  await page.route(isHotelSearch, (route) => {
    if (!refused && route.request().method() === 'GET') {
      refused = true;
      return route.abort('connectionrefused');
    }
    return route.fallback();
  });

  await test.step('fill a valid Seattle search', async () => {
    const [checkIn, checkOut] = utcDates(30, 32);
    await page.goto('/search');
    await fillBound(page, 'search-city', 'Seattle');
    await fillBound(page, 'search-check-in', checkIn);
    await fillBound(page, 'search-check-out', checkOut);
    await fillBound(page, 'search-guests', '2');
  });

  const error = page.getByTestId('inline-error');

  await test.step('a refused search shows only "The service is unreachable."', async () => {
    const failed = page.waitForEvent(
      'requestfailed',
      (r) => isHotelSearch(new URL(r.url())) && r.method() === 'GET',
    );
    await page.getByTestId('search-submit').click();
    expect((await failed).failure()?.errorText).toBe('net::ERR_CONNECTION_REFUSED');

    // Status 0 carries no problem body: the title alone, with no detail
    // paragraph and no field list.
    const alert = error.getByRole('alert');
    await expect(alert.locator('strong')).toHaveText(UNREACHABLE_TITLE);
    await expect(alert).toHaveText(UNREACHABLE_TITLE);
    await expect(alert.locator('p, ul, li')).toHaveCount(0);

    // The failed request has settled: the indicator is gone and the form can
    // be submitted again, with no result and no empty message shown.
    await expect(page.getByTestId('loading-indicator')).toHaveCount(0);
    await expect(page.getByTestId('search-submit')).toBeEnabled();
    await expect(page.locator(HOTEL_RESULTS)).toHaveCount(0);
    await expect(page.getByTestId('search-empty')).toHaveCount(0);
  });

  await test.step('Search again reaches the API and replaces the error with results', async () => {
    // The criteria are already in the URL, so this resubmission is the
    // page's rerun of an identical search.
    const retried = page.waitForResponse(
      (r) => isHotelSearch(new URL(r.url())) && r.request().method() === 'GET',
    );
    await page.getByTestId('search-submit').click();
    expect((await retried).status()).toBe(200);
    await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
    await expect(error).toHaveCount(0);
  });
});

for (const c of RAW_CRITERIA_CASES) {
  test(`${c.title} reaches the server as written and its ${c.status} shows inline`, async ({ page }) => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const url = c.url.replace('{stay}', `checkIn=${checkIn}&checkOut=${checkOut}`);

    const res = await test.step('open the deep link', async () => {
      const response = page.waitForResponse(
        (r) => isApiEndpoint(r.url(), c.endpoint) && r.request().method() === 'GET',
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

for (const text of TYPED_GUESTS) {
  test(`typed guests ${JSON.stringify(text)} reaches the server as written, also on an unchanged re-submit`, async ({
    page,
  }) => {
    await page.goto('/search');
    await fillSearchForm(page, text);

    for (const attempt of ['submit', 'unchanged re-submit']) {
      await test.step(`the ${attempt} sends the typed text and shows its 400 inline`, async () => {
        const res = await submitSearch(page);
        expect(new URL(res.url()).searchParams.get('guests')).toBe(text);
        expect(res.status()).toBe(400);
        await expect(page).toHaveURL((url) => url.searchParams.get('guests') === text);

        const guests = page.getByTestId('search-guests');
        await expect(page.getByTestId('inline-error')).toBeVisible();
        await expect(guests).toHaveAttribute('aria-invalid', 'true');
        await expect(guests).toHaveAccessibleDescription(GUESTS_INTEGER_MESSAGE);
        await expectControlValue(page, 'search-guests', text);
      });
    }
  });
}

for (const text of DEEP_LINK_GUESTS) {
  test(`a search deep link's guests ${JSON.stringify(text)} stays in the form for an unchanged re-submit`, async ({
    page,
  }) => {
    const [checkIn, checkOut] = utcDates(30, 32);

    await test.step(`open a deep link with guests=${text}`, async () => {
      const response = page.waitForResponse(
        (r) => isHotelSearch(new URL(r.url())) && r.request().method() === 'GET',
      );
      await page.goto(`/search?city=Seattle&checkIn=${checkIn}&checkOut=${checkOut}&guests=${text}`);
      const res = await response;
      expect(new URL(res.url()).searchParams.get('guests')).toBe(text);
      expect(res.status()).toBe(400);
      await expectControlValue(page, 'search-guests', text);
    });

    await test.step('the unchanged re-submit sends the same text again and shows its 400 inline', async () => {
      const res = await submitSearch(page);
      expect(new URL(res.url()).searchParams.get('guests')).toBe(text);
      expect(res.status()).toBe(400);
      await expect(page.getByTestId('inline-error')).toContainText(GUESTS_INTEGER_MESSAGE);
      // `get`, not `has`: `''` is the explicit empty, `null` an absent guests.
      await expect(page).toHaveURL((url) => url.searchParams.get('guests') === text);
      await expectControlValue(page, 'search-guests', text);
    });

    await test.step('clearing the field leaves guests out', async () => {
      await fillBound(page, 'search-guests', '3');
      await fillBound(page, 'search-guests', '');
      const res = await submitSearch(page);
      expect(new URL(res.url()).searchParams.get('guests')).toBe('1');
      expect(res.status()).toBe(200);
      await expect(page).toHaveURL((url) => !url.searchParams.has('guests'));
      await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
    });
  });
}

test('a guests field left empty is left out of the URL and the server uses 1', async ({ page }) => {
  await page.goto('/search');
  const { checkIn, checkOut } = await fillSearchForm(page, '');

  const res = await submitSearch(page);
  expect(new URL(res.url()).searchParams.get('guests')).toBe('1');
  expect(res.status()).toBe(200);
  await expect(page).toHaveURL(new RegExp(`/search\\?city=Seattle&checkIn=${checkIn}&checkOut=${checkOut}$`));
  await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
  await expectNoInlineError(page);
});

for (const link of BARE_SEARCH_LINKS) {
  test(`${link} from a results page returns to the idle search with the initial criteria`, async ({ page }) => {
    await test.step('search Seattle for 2 guests, then edit the city without searching', async () => {
      const [checkIn, checkOut] = utcDates(30, 32);
      await page.goto('/search');
      await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 2 });
      await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
      await fillBound(page, 'search-city', 'Portland');
    });

    await test.step(`click ${link}`, async () => {
      await page.getByTestId(link).click();
      await expectIdleSearch(page);
    });
  });
}

test('Log out from a results page returns to the idle search with the initial criteria', async ({ page }) => {
  await test.step('register and log in', () => registerAndLogIn(page));

  await test.step('search Seattle for 2 guests', async () => {
    const [checkIn, checkOut] = utcDates(30, 32);
    await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 2 });
    await expect(page.locator(HOTEL_RESULTS).first()).toBeVisible();
  });

  await test.step('log out', async () => {
    await page.getByTestId('nav-logout').click();
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expectIdleSearch(page);
  });
});

for (const id of NON_INTEGER_HOTEL_IDS) {
  test(`the hotel id ${id} shows an inline 404 without a request`, async ({ page }) => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const detailRequests = recordDetailRequests(page);
    await page.goto(`/hotels/${id}?checkIn=${checkIn}&checkOut=${checkOut}`);
    await expectLocal404(page, detailRequests);
  });
}

test('the hotel id NUL from the login return URL shows an inline 404 without a request', async ({ page }) => {
  const { email, password } = await test.step('register a guest', () => registerNewGuest(page));

  // nginx rejects /hotels/%00 on a direct load, so the in-app navigation
  // after login, which accepts any same-app path, is the way to reach it.
  const detailRequests = recordDetailRequests(page);
  await test.step('log in with a return URL to the hotel id %00', async () => {
    const [checkIn, checkOut] = utcDates(30, 32);
    const returnUrl = `/hotels/%00?checkIn=${checkIn}&checkOut=${checkOut}`;
    await page.goto(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
    await logIn(page, email, password);
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

    // Loads are recorded on any origin, so a load sent to another API counts
    // too and fails here: exactly one, from the target API's detail endpoint.
    expect(loads.map((u) => `${u.origin}${u.pathname}`)).toEqual([`${apiUrl()}/hotels/${first.id}`]);
    const [load] = loads;
    expect(load.searchParams.get('checkIn')).toBe(firstStay.checkIn);
    expect(load.searchParams.get('checkOut')).toBe(firstStay.checkOut);
    expect(load.searchParams.get('guests')).toBe(String(firstStay.guests));
  });
});

/**
 * Asserts that the room table shows "Book" buttons and that every one is
 * disabled, as they are while the page's booking, or the revalidation after
 * its answer, is in flight.
 */
async function expectEveryBookDisabled(page: Page): Promise<void> {
  await expect(page.locator(BOOK_BUTTONS)).not.toHaveCount(0);
  await expect(page.locator(`${BOOK_BUTTONS}:enabled`)).toHaveCount(0);
}

/** An API request the page sent, as {@link recordApiRequests} keeps it. */
interface SentApiRequest {
  method: string;
  url: URL;
}

/**
 * Records, from now on, every API request the page sends except a CORS
 * preflight, in the order sent, and returns the list it fills.
 */
function recordApiRequests(page: Page): SentApiRequest[] {
  const sent: SentApiRequest[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (isApi(url) && request.method() !== 'OPTIONS') {
      sent.push({ method: request.method(), url });
    }
  });
  return sent;
}

/**
 * Writes a recorded request's target as its path alone when it went to the
 * target API's origin, and as origin and path otherwise, so a request that
 * another stack's API received never compares equal to a target API path.
 */
function apiRelativeTarget(url: URL): string {
  return url.origin === new URL(apiUrl()).origin ? url.pathname : `${url.origin}${url.pathname}`;
}

/**
 * Describes a recorded request by its method, path and stay query alone, so a
 * failed comparison prints no other part of it; a request to another origin
 * than the target API's also shows that origin ({@link apiRelativeTarget}).
 */
function describeStayRequest({ method, url }: SentApiRequest): string {
  const query = url.searchParams;
  return `${method} ${apiRelativeTarget(url)} checkIn=${query.get('checkIn')} checkOut=${query.get('checkOut')} guests=${query.get('guests')}`;
}

/**
 * Reads, through the API, how many rooms of one room type the server holds
 * free for a stay at a hotel: the figure the detail page shows in
 * `available-count-<id>`.
 */
async function apiAvailableCount(
  request: APIRequestContext,
  hotelId: string,
  stay: Stay,
  roomTypeId: number,
): Promise<number> {
  const roomType = (await apiRoomTypes(request, Number(hotelId), stay)).find((rt) => rt.id === roomTypeId);
  expect(roomType !== undefined, 'the hotel lists the room type').toBe(true);
  return Number(roomType?.availableRooms);
}

/** What {@link bookThenReturnToShownStay} leaves on screen. */
interface PendingLateBooking {
  /** The hotel of both stays, with the name its page shows. */
  hotel: { id: string; name: string };
  /** The room type whose "Book" was clicked, with the name its row shows. */
  roomType: { id: number; name: string };
  /** The held `POST /api/bookings` of that click. */
  post: RequestGate;
  /** The detail page URL of the shown stay, now on screen. */
  url: string;
  /** The room type's available count for the shown stay, as the page shows it. */
  shownCount: number;
}

/**
 * Clicks "Book" on a Seattle hotel's page for the `booked` stay, holds its
 * `POST /api/bookings`, and goes back to the same hotel's page for the
 * `shown` stay in one history traversal, which reuses the page component with
 * the booking still in flight. On the way it asserts that the indicator and
 * every disabled "Book" last as long as the POST, also once the traversal's
 * own GET has answered. The browser is left there, with the POST held.
 *
 * Expects the browser on `/search`, signed in, with nothing searched yet.
 */
async function bookThenReturnToShownStay(page: Page, shown: Stay, booked: Stay): Promise<PendingLateBooking> {
  const indicator = page.getByTestId('loading-indicator');
  const stayLine = page.locator('.hotel-stay');

  const hotel = await test.step('open a Seattle hotel for the stay to come back to', async () => {
    await searchHotels(page, { city: 'Seattle', ...shown });
    return openHotel(page, page.locator(VIEW_ROOMS).first());
  });

  await test.step('open the same hotel for the stay to book', async () => {
    await page.getByTestId('nav-search').click();
    await expect(page).toHaveURL(/\/search$/);
    await searchHotels(page, { city: 'Seattle', ...booked });
    await openHotel(page, page.getByTestId(`view-rooms-${hotel.id}`));
    await expect(stayLine).toContainText(`${displayDate(booked.checkIn)} to ${displayDate(booked.checkOut)}`);
  });

  const { roomType, post } = await test.step('click Book and hold its POST', async () => {
    const testId = (await page.locator(`${BOOK_BUTTONS}:enabled`).first().getAttribute('data-testid')) ?? '';
    expect(testId).toMatch(/^book-\d+$/);
    const id = Number(testId.slice('book-'.length));
    const name = (await page.getByTestId(`room-type-row-${id}`).locator('td').first().innerText()).trim();

    const gate = await holdRequest(page, isBookingsCollection, 'POST');
    await page.getByTestId(testId).click();
    await gate.expectArrival('Book sends the POST');
    await expect(indicator).toBeVisible();
    await expectEveryBookDisabled(page);
    return { roomType: { id, name }, post: gate };
  });

  const shownCount = await test.step('go back to the stay to come back to in one history traversal', async () => {
    const loads = recordApiRequests(page);
    const loaded = page.waitForResponse((r) => {
      const url = new URL(r.url());
      return (
        isApiEndpoint(url, `/hotels/${hotel.id}`) &&
        url.searchParams.get('checkIn') === shown.checkIn &&
        r.request().method() === 'GET'
      );
    });

    // The history holds /search, /search?<shown stay>, /hotels/<hotel>?<shown
    // stay>, /search, /search?<booked stay> and /hotels/<hotel>?<booked stay>.
    // Three entries back is the shown stay: one navigation that reuses the
    // detail page, with the POST still held, and changes only its query.
    await page.evaluate(() => history.go(-3));
    expect((await loaded).status()).toBe(200);
    await expect(page).toHaveURL(new RegExp(`/hotels/${hotel.id}\\?.*checkIn=${shown.checkIn}`));
    // The render that shows the shown stay is the one that applies the GET's
    // answer, so an indicator that answer lowered would already be gone below.
    await expect(stayLine).toContainText(`${displayDate(shown.checkIn)} to ${displayDate(shown.checkOut)}`);
    await expect(page.getByTestId('hotel-name')).toBeVisible();
    expect(loads.map(describeStayRequest)).toEqual([
      `GET /api/hotels/${hotel.id} checkIn=${shown.checkIn} checkOut=${shown.checkOut} guests=${shown.guests}`,
    ]);

    // The GET has answered but the POST is still pending: the shared counter
    // keeps the indicator up and every "Book" stays disabled.
    await expect(indicator).toBeVisible();
    await expectEveryBookDisabled(page);
    return readAvailableCount(page, roomType.id);
  });

  return { hotel, roomType, post, url: page.url(), shownCount };
}

/**
 * A booked stay whose 201 arrives while the same hotel's page shows another
 * stay: one that overlaps the shown stay, so the new booking takes a room the
 * table counts, or one that starts on the shown stay's check-out day, which
 * is free, so it takes none.
 */
interface LateCreatedCase {
  /** The booked stay, for the test title. */
  title: string;
  /** Days from today of the shown check-in and check-out, then of the booked ones. */
  offsets: readonly [number, number, number, number];
  /** Whether the booked stay overlaps the shown one as half-open ranges. */
  overlaps: boolean;
}

/** The late 201s, one test each. */
const LATE_CREATED_CASES: readonly LateCreatedCase[] = [
  { title: 'an overlapping stay', offsets: [100, 103, 101, 104], overlaps: true },
  { title: 'a stay from the shown check-out day', offsets: [105, 107, 107, 109], overlaps: false },
];

for (const c of LATE_CREATED_CASES) {
  test(`a late 201 for ${c.title} leaves the reused detail page on the stay it shows${c.overlaps ? ' and revalidates it' : ', unreloaded'}`, async ({ page, request }) => {
    const [shownIn, shownOut, bookedIn, bookedOut] = utcDates(...c.offsets);
    const shown: Stay = { checkIn: shownIn, checkOut: shownOut, guests: 1 };
    const booked: Stay = { checkIn: bookedIn, checkOut: bookedOut, guests: 2 };
    const indicator = page.getByTestId('loading-indicator');
    const inlineErrors = await watchInlineErrors(page);

    await test.step('register and log in', async () => {
      // Recorded before "Book" is clicked: once released, the POST can commit
      // a booking whose 201 this test never decodes, and the guest's
      // reconciliation after the test cancels it all the same.
      cancelGuestBookingsAfterTest(page, await apiSignIn(request, await registerAndLogIn(page)));
    });
    const pending = await bookThenReturnToShownStay(page, shown, booked);
    const detailPath = `/api/hotels/${pending.hotel.id}`;
    const countAfter = pending.shownCount - (c.overlaps ? 1 : 0);

    // The revalidation is held too, so the indicator and the disabled "Book"
    // can be seen to outlast the POST until the revalidation has answered.
    const revalidation = c.overlaps
      ? await holdRequest(page, (url) => isApiEndpoint(url, `/hotels/${pending.hotel.id}`), 'GET')
      : null;
    const sent = recordApiRequests(page);

    await test.step('release the POST: the API creates the booking', async () => {
      const created = page.waitForResponse(
        (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'POST',
      );
      await pending.post.release();
      const response = await created;
      expect(response.status()).toBe(201);
      const body: unknown = await response.json();
      const id = isRecord(body) ? body['id'] : undefined;
      expect(typeof id === 'number' && Number.isInteger(id), 'the 201 carries the booking id').toBe(true);
    });

    await test.step('a notice names the booked room type, hotel and stay as confirmed', async () => {
      await expect(page.getByTestId('late-booking')).toBeVisible();
      await expect(page.getByTestId('late-booking-message')).toHaveText(
        `Your booking of ${pending.roomType.name} at ${pending.hotel.name} ` +
          `from ${displayDate(bookedIn)} to ${displayDate(bookedOut)} ` +
          'is confirmed. No payment is taken online in this version.',
      );
      await expect(page.getByTestId('late-booking-bookings')).toBeVisible();
      await expect(page.getByTestId('late-booking-bookings')).toHaveText('View it in My bookings');
    });

    if (revalidation !== null) {
      await test.step('the indicator and the disabled Book last until the revalidation answers', async () => {
        await revalidation.expectArrival('the late 201 revalidates the stay on screen');
        await expect(indicator).toBeVisible();
        await expectEveryBookDisabled(page);
        const reread = page.waitForResponse(
          (r) => isApiEndpoint(r.url(), `/hotels/${pending.hotel.id}`) && r.request().method() === 'GET',
        );
        await revalidation.release();
        expect((await reread).status()).toBe(200);
      });
    }

    await test.step('the page stays on the shown stay, with no error and the count the API holds', async () => {
      // The notice above was rendered after the 201's own work had started,
      // so with the indicator gone nothing it started is still in flight: the
      // checks below see the settled page, and no request can join `sent`.
      await expect(indicator).toHaveCount(0);
      await expect(page.getByTestId(`available-count-${pending.roomType.id}`)).toHaveText(String(countAfter));
      await expect(page.getByTestId(`book-${pending.roomType.id}`)).toBeEnabled();
      await expect(page).toHaveURL(pending.url);
      await expect(page.getByTestId('hotel-name')).toHaveText(pending.hotel.name);
      await expect(page.locator('.hotel-stay')).toContainText(`${displayDate(shownIn)} to ${displayDate(shownOut)}`);
      expect(await apiAvailableCount(request, pending.hotel.id, shown, pending.roomType.id)).toBe(countAfter);
      // Every API request since the release.
      expect(sent.map(describeStayRequest)).toEqual(
        c.overlaps ? [`GET ${detailPath} checkIn=${shownIn} checkOut=${shownOut} guests=${shown.guests}`] : [],
      );
      await expectNoInlineError(page);
      expect(await inlineErrors.appearances()).toEqual([]);
    });
  });
}

test('a late 401 on a reused detail page shows in its notice, never as an error of the stay on screen', async ({ page }) => {
  // The stays overlap, so only the status, not the dates, rules out a
  // revalidation.
  const [shownIn, shownOut, bookedIn, bookedOut] = utcDates(111, 114, 112, 115);
  const shown: Stay = { checkIn: shownIn, checkOut: shownOut, guests: 1 };
  const booked: Stay = { checkIn: bookedIn, checkOut: bookedOut, guests: 2 };
  const indicator = page.getByTestId('loading-indicator');
  const inlineErrors = await watchInlineErrors(page);

  await test.step('register and log in', () => registerAndLogIn(page));
  const pending = await bookThenReturnToShownStay(page, shown, booked);
  const sent = recordApiRequests(page);

  await test.step('release the POST with a token the API rejects', async () => {
    const rejected = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'POST',
    );
    await pending.post.release({ authorization: 'Bearer not-a-valid-token' });
    expect((await rejected).status()).toBe(401);
  });

  await test.step('the notice says the booking could not be confirmed, with the 401 and its own link', async () => {
    const late = page.getByTestId('late-booking');
    await expect(late).toBeVisible();
    await expect(page.getByTestId('late-booking-message')).toHaveText(
      `Your booking of ${pending.roomType.name} at ${pending.hotel.name} ` +
        `from ${displayDate(bookedIn)} to ${displayDate(bookedOut)} ` +
        'could not be confirmed.',
    );
    const error = late.getByTestId('inline-error');
    await expect(error).toBeVisible();
    const loginAgain = error.getByTestId('late-booking-login-again');
    await expect(loginAgain).toHaveText('Log in again');
    // The link comes back to the stay on screen, not the booked one.
    const target = new URL((await loginAgain.getAttribute('href')) ?? '', page.url());
    const current = new URL(pending.url);
    expect(target.pathname).toBe('/login');
    expect(target.searchParams.get('returnUrl')).toBe(`${current.pathname}${current.search}`);
    await expect(late.getByTestId('late-booking-bookings')).toHaveCount(0);
  });

  await test.step('the stay on screen shows no error of its own, signed out, with nothing reloaded', async () => {
    await expect(indicator).toHaveCount(0);
    await expect(page.getByTestId('login-again')).toHaveCount(0);
    await expect(page.getByTestId('inline-error')).toHaveCount(1);
    await expect(page.getByTestId(`book-${pending.roomType.id}`)).toBeEnabled();
    await expect(page).toHaveURL(pending.url);
    await expect(page.getByTestId('hotel-name')).toHaveText(pending.hotel.name);
    await expect(page.locator('.hotel-stay')).toContainText(`${displayDate(shownIn)} to ${displayDate(shownOut)}`);
    await expect(page.getByTestId(`available-count-${pending.roomType.id}`)).toHaveText(String(pending.shownCount));
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
    // As after the 201: with the indicator gone, the list is complete.
    expect(sent.map(describeStayRequest)).toEqual([]);
    expect(await inlineErrors.appearances()).toEqual([new URL(pending.url).pathname]);
  });
});

test('duplicate registration shows an inline 409', async ({ page }) => {
  const { email } = await test.step('register a guest', () => registerNewGuest(page));

  await test.step('register the same email again', async () => {
    await page.getByTestId('register-link').click();
    await expect(page).toHaveURL(/\/register$/);

    // The API folds the email to lower case before comparing, so the same
    // address in other letter case is still a duplicate.
    await fillBound(page, 'register-email', email.replace(/^[^@]+/, (local) => local.toUpperCase()));
    // A different valid password: the email alone makes the account a duplicate.
    await fillBound(page, 'register-password', uniquePassword());
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
  const { email, password } = await test.step('register a guest', () => registerNewGuest(page));

  await test.step('log in with a wrong password', async () => {
    await fillBound(page, 'login-email', email);
    // One character longer than the guest's own password, so never equal to it.
    await fillBound(page, 'login-password', `${password}x`);
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

/**
 * Returns the `title` and `detail` of a ProblemDetails body, requiring each to
 * be a string with text, so a test can require the page to show the server's
 * own words rather than a client fallback such as "Request failed (400).".
 */
function problemTitleAndDetail(body: unknown): { title: string; detail: string } {
  const title = isRecord(body) ? body['title'] : undefined;
  const detail = isRecord(body) ? body['detail'] : undefined;
  if (typeof title !== 'string' || title.trim() === '' || typeof detail !== 'string' || detail.trim() === '') {
    throw new Error('The response body carries no ProblemDetails title and detail.');
  }
  return { title, detail };
}

test('register with a too-short password shows the 400 field error inline and stays put', async ({ page }) => {
  await page.goto('/register');

  const response = await test.step('register with a 7-character password', async () => {
    // The form sends the password unchanged, so only the server's 8 to 128
    // rule can reject it; the rejection creates no account.
    await fillBound(page, 'register-email', uniqueEmail());
    await fillBound(page, 'register-password', 'x'.repeat(7));
    await fillBound(page, 'register-full-name', 'States Guest');
    // The method filter keeps a CORS preflight from matching.
    const registration = page.waitForResponse(
      (r) => isRegisterApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await page.getByTestId('register-submit').click();
    const res = await registration;
    expect(res.status()).toBe(400);
    return res;
  });

  await test.step('the 400 shows inline and marks only the password', async () => {
    const { title, detail } = problemTitleAndDetail(await response.json());
    const alert = page.getByTestId('inline-error').getByRole('alert');
    await expect(alert.locator('strong')).toHaveText(title);
    await expect(alert.locator('p')).toHaveText(detail);
    // The field error names its field by the visible label, never by the API's key.
    await expect(alert.locator('li')).toHaveText(['Password: The value must be 8 to 128 characters long.']);

    const password = page.getByTestId('register-password');
    await expect(password).toHaveAttribute('aria-invalid', 'true');
    await expect(password).toHaveAccessibleDescription('The value must be 8 to 128 characters long.');
    await expect(page.getByTestId('register-email')).not.toHaveAttribute('aria-invalid');
    await expect(page.getByTestId('register-full-name')).not.toHaveAttribute('aria-invalid');
  });

  await test.step('stay on the register page, ready to submit again', async () => {
    await expect(page).toHaveURL(/\/register$/);
    await expect(page.getByTestId('register-submit')).toBeEnabled();
  });
});

// A contract test of the login page's 400 renderer. The form always sends an
// object of two strings, which the API binds and answers with 200 or 401, so
// the login 400 (a JSON body that fails binding) cannot come from the form's
// normal requests. The route rewrites the body of the page's own POST so that
// the real API rejects it, and the test checks how the unchanged page renders
// that genuine 400.
test('login page shows an API 400 inline (contract test: rewritten request body)', async ({ page }) => {
  const { email, password } = await test.step('register a guest', () => registerNewGuest(page));

  const response = await test.step('log in with the request body rewritten to an unbindable email', async () => {
    // Only the POST is rewritten; a CORS preflight goes on unchanged. The
    // replacement holds no credential: a JSON number for `email`, which the
    // API's binding rejects before any credential check.
    await page.route(isLoginApi, (route) =>
      route.request().method() === 'POST'
        ? route.continue({ postData: JSON.stringify({ email: 5, password: 'x' }) })
        : route.continue(),
    );
    // The form's own credentials are valid, so without the rewrite this login
    // would succeed.
    await fillBound(page, 'login-email', email);
    await fillBound(page, 'login-password', password);
    const login = page.waitForResponse(
      (r) => isLoginApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await page.getByTestId('login-submit').click();
    const res = await login;
    expect(res.status()).toBe(400);
    return res;
  });

  await test.step('the 400 shows inline with its field error on the email', async () => {
    const { title, detail } = problemTitleAndDetail(await response.json());
    const alert = page.getByTestId('inline-error').getByRole('alert');
    await expect(alert.locator('strong')).toHaveText(title);
    await expect(alert.locator('p')).toHaveText(detail);
    // The JSON path `$.email` is labelled with the email input's visible name.
    await expect(alert.locator('li')).toHaveText(['Email: The JSON value is not valid.']);

    // The JSON path `$.email` names the email input, which is marked and
    // described by the message; the password input is not.
    const emailInput = page.getByTestId('login-email');
    await expect(emailInput).toHaveAttribute('aria-invalid', 'true');
    await expect(emailInput).toHaveAccessibleDescription('The JSON value is not valid.');
    await expect(page.getByTestId('login-password')).not.toHaveAttribute('aria-invalid');

    // Only a 401 reads as wrong credentials.
    await expect(page.getByText('Email or password is incorrect.')).toHaveCount(0);
  });

  await test.step('stay on the login page, signed out', async () => {
    await expect(page).toHaveURL(/\/login(?:\?|$)/);
    await expect(page.getByTestId('nav-login')).toBeVisible();
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
    await expect(page.getByTestId('login-submit')).toBeEnabled();
  });
});

for (const c of DEADLINE_CASES) {
  test(`a ${c.form} the API never answers ends at the deadline as unreachable`, async ({ page }) => {
    // Installed before the app loads, so the request deadline's timer runs on
    // a clock the test can move past the 30 seconds.
    await page.clock.install();
    await page.route(c.isEndpoint, () => {
      // Never fulfilled, continued or aborted: the POST stays pending as if
      // the API had stopped answering.
    });

    await page.goto(`/${c.form}`);
    await c.fill(page);
    const submit = page.getByTestId(`${c.form}-submit`);
    const indicator = page.getByTestId('loading-indicator');

    await test.step('submit, and the request stays pending', async () => {
      const sent = page.waitForRequest((r) => c.isEndpoint(new URL(r.url())) && r.method() === 'POST');
      await submit.click();
      await sent;
      await expect(indicator).toBeVisible();
      await expect(submit).toBeDisabled();
    });

    await test.step('past the deadline the wait ends with the error inline', async () => {
      await page.clock.fastForward('00:31');
      await expect(page.getByTestId('inline-error')).toContainText(UNREACHABLE_TITLE);
      await expect(indicator).toBeHidden();
      await expect(submit).toBeEnabled();
      await expect(page).toHaveURL(new RegExp(`/${c.form}$`));
    });
  });
}

for (const c of UNUSABLE_LOGIN_BODIES) {
  test(`a login 200 with ${c.title} shows inline and signs nobody in`, async ({ page }) => {
    await page.route(isLoginApi, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: c.body }),
    );

    await page.goto('/login');
    await fillBound(page, 'login-email', uniqueEmail());
    await fillBound(page, 'login-password', uniquePassword());
    const login = page.waitForResponse(
      (r) => isLoginApi(new URL(r.url())) && r.request().method() === 'POST',
    );
    await page.getByTestId('login-submit').click();
    expect((await login).status()).toBe(200);

    await expect(page.getByTestId('inline-error')).toContainText(UNEXPECTED_RESPONSE_TITLE);
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByTestId('nav-login')).toHaveText('Log in');
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });
}

for (const c of UNUSABLE_READ_CASES) {
  test(`${c.title} shows inline and renders no hotel`, async ({ page }) => {
    await page.route(
      (url) => isApiEndpoint(url, c.endpoint),
      (route) => route.fulfill({ status: 200, contentType: 'application/json', body: c.body }),
    );

    const [checkIn, checkOut] = utcDates(30, 32);
    const read = page.waitForResponse(
      (r) => isApiEndpoint(r.url(), c.endpoint) && r.request().method() === 'GET',
    );
    await page.goto(c.url.replace('{stay}', `checkIn=${checkIn}&checkOut=${checkOut}`));
    expect((await read).status()).toBe(200);

    await expect(page.getByTestId('inline-error')).toContainText(UNEXPECTED_RESPONSE_TITLE);
    await expect(page.locator(`${HOTEL_RESULTS}, [data-testid="hotel-name"]`)).toHaveCount(0);
    await expect(page.getByTestId('search-empty')).toHaveCount(0);
  });
}

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
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'GET',
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
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'POST',
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

test('server 401 on Cancel stays on My bookings with an inline error', async ({ page, request }) => {
  const credentials = await test.step('register and log in', () => registerAndLogIn(page));

  const { session, id } = await test.step('book a later Seattle stay for the guest through the API', async () => {
    const [checkIn, checkOut] = utcDates(120, 122);
    const stay: Stay = { checkIn, checkOut, guests: 1 };

    const search = await fixtureRequest('searching hotels', () =>
      request.get(`${apiUrl()}/hotels`, { params: { city: 'Seattle', ...stay } }),
    );
    expect(search.status(), 'the fixture search returns 200').toBe(200);
    const hotels: unknown = await search.json();
    const hotel: unknown = Array.isArray(hotels) ? (hotels as unknown[])[0] : undefined;
    const hotelId = isRecord(hotel) ? hotel['id'] : undefined;
    expect(typeof hotelId === 'number' && Number.isInteger(hotelId), 'the fixture search returns a hotel').toBe(true);

    const free = (await apiRoomTypes(request, Number(hotelId), stay)).find((rt) => rt.availableRooms > 0);
    expect(free !== undefined, 'the hotel has a free room type').toBe(true);

    const guest = await apiSignIn(request, credentials);
    const bookingId = await apiBook(request, guest, {
      hotelId: Number(hotelId),
      roomTypeId: Number(free?.id),
      ...stay,
    });
    return { session: guest, id: bookingId };
  });

  const bookingsUrl = await test.step('open My bookings with a token the API accepts', async () => {
    // The entry GET must succeed: its own 401 is the redirect branch, which
    // the entry case covers.
    const list = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'GET',
    );
    await page.getByTestId('nav-bookings').click();
    expect((await list).status(), 'the entry GET returns 200').toBe(200);
    await expect(page).toHaveURL(/\/bookings$/);
    await expect(page.getByTestId(`booking-row-${id}`)).toBeVisible();
    await expect(page.getByTestId(`booking-status-${id}`)).toHaveText('Confirmed');
    await expect(page.getByTestId(`cancel-${id}`)).toBeEnabled();
    return page.url();
  });

  const sent = await test.step('click Cancel with a token the API rejects', async () => {
    // Only the DELETE carries a token the API rejects; every other bookings
    // request keeps the app's own, so the 401 can only be Cancel's.
    await page.route(isBookingsApi, (route) => {
      const routed = route.request();
      return routed.method() === 'DELETE'
        ? route.continue({ headers: { ...routed.headers(), authorization: 'Bearer not-a-valid-token' } })
        : route.continue();
    });
    const recorded = recordApiRequests(page);
    const cancelled = page.waitForResponse(
      (r) => isApiEndpoint(r.url(), `/bookings/${id}`) && r.request().method() === 'DELETE',
    );
    await page.getByTestId(`cancel-${id}`).click();
    expect((await cancelled).status(), 'the API rejects the DELETE').toBe(401);
    return recorded;
  });

  await test.step('stay on My bookings with the 401 inline, signed out', async () => {
    const error = page.getByTestId('inline-error');
    await expect(error).toBeVisible();
    await expect(error.getByRole('alert').locator('strong')).toHaveText('Unauthorized');
    await expect(error.getByRole('alert').locator('p')).toHaveText('A valid bearer token is required.');
    // The 401 signed the guest out, so none of their bookings stay on screen;
    // the inline error holds the only recovery link.
    await expect(page.getByTestId('bookings-table')).toHaveCount(0);
    await expect(page.getByTestId(`booking-row-${id}`)).toHaveCount(0);
    await expect(page.getByTestId(`cancel-${id}`)).toHaveCount(0);
    await expect(page.getByTestId('bookings-signed-out')).toHaveCount(0);
    await expect(page.getByTestId('login-again')).toHaveCount(1);
    const loginAgain = error.getByTestId('login-again');
    await expect(loginAgain).toHaveText('Log in again');
    await expect(loginAgain).toHaveAttribute('href', '/login?returnUrl=%2Fbookings');
    // The clicked "Cancel" held focus, so the settled cancel hands it to the
    // recovery link.
    await expect(loginAgain).toBeFocused();
    await expect(page).toHaveURL(bookingsUrl);
    await expect(page.getByTestId('nav-login')).toHaveText('Log in');
    await expect(page.getByTestId('nav-email')).toHaveCount(0);
  });

  await test.step('the booking stays Confirmed on the server', async () => {
    // A rejected cancel reloads nothing: the DELETE is the only API request.
    expect(
      sent.map(({ method, url }) => `${method} ${apiRelativeTarget(url)}`),
      'the API requests after the click',
    ).toEqual([`DELETE /api/bookings/${id}`]);
    expect(await apiBookingStatus(request, session, id), 'the server status of the booking').toBe('Confirmed');
    // Still there after the server round trip, so no late redirect followed.
    await expect(page).toHaveURL(bookingsUrl);
  });

  await test.step('"Log in again" returns to My bookings with Cancel still offered', async () => {
    await page.getByTestId('login-again').click();
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings$/);
    const list = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'GET',
    );
    await logIn(page, credentials.email, credentials.password);
    expect((await list).status(), 'the list GET after logging in again returns 200').toBe(200);
    await expect(page).toHaveURL(bookingsUrl);
    await expect(page.getByTestId(`booking-status-${id}`)).toHaveText('Confirmed');
    await expect(page.getByTestId(`cancel-${id}`)).toBeEnabled();
  });
});

test('Cancel answering a real 409 shows it above the list and reloads the row as Cancelled', async ({ page, request }) => {
  const credentials = await test.step('register and log in', () => registerAndLogIn(page));

  const { session, stale, other } = await test.step('book two later Seattle stays for the guest through the API', async () => {
    const [staleIn, staleOut, otherIn, otherOut] = utcDates(124, 126, 128, 130);
    const staleStay: Stay = { checkIn: staleIn, checkOut: staleOut, guests: 1 };
    const otherStay: Stay = { checkIn: otherIn, checkOut: otherOut, guests: 1 };

    const search = await fixtureRequest('searching hotels', () =>
      request.get(`${apiUrl()}/hotels`, { params: { city: 'Seattle', ...staleStay } }),
    );
    expect(search.status(), 'the fixture search returns 200').toBe(200);
    const hotels: unknown = await search.json();
    const hotel: unknown = Array.isArray(hotels) ? (hotels as unknown[])[0] : undefined;
    const hotelId = isRecord(hotel) ? hotel['id'] : undefined;
    expect(typeof hotelId === 'number' && Number.isInteger(hotelId), 'the fixture search returns a hotel').toBe(true);

    // One room type with a free room on both stays, so both bookings succeed.
    const freeLater = new Set(
      (await apiRoomTypes(request, Number(hotelId), otherStay)).filter((rt) => rt.availableRooms > 0).map((rt) => rt.id),
    );
    const free = (await apiRoomTypes(request, Number(hotelId), staleStay)).find(
      (rt) => rt.availableRooms > 0 && freeLater.has(rt.id),
    );
    expect(free !== undefined, 'the hotel has a room type free on both stays').toBe(true);

    const guest = await apiSignIn(request, credentials);
    const book = (stay: Stay): Promise<number> =>
      apiBook(request, guest, { hotelId: Number(hotelId), roomTypeId: Number(free?.id), ...stay });
    const staleId = await book(staleStay);
    return { session: guest, stale: staleId, other: await book(otherStay) };
  });

  const bookingsUrl = await test.step('open My bookings: both bookings are Confirmed and offer Cancel', async () => {
    const list = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'GET',
    );
    await page.getByTestId('nav-bookings').click();
    expect((await list).status(), 'the entry GET returns 200').toBe(200);
    await expect(page).toHaveURL(/\/bookings$/);
    for (const id of [stale, other]) {
      await expect(page.getByTestId(`booking-status-${id}`)).toHaveText('Confirmed');
      await expect(page.getByTestId(`cancel-${id}`)).toBeEnabled();
    }
    return page.url();
  });

  await test.step('cancel the booking elsewhere: the open page still offers it', async () => {
    // The guest's other tab or device; nothing tells this page about it.
    const cancelled = await fixtureRequest('cancelling a booking', () =>
      request.delete(`${apiUrl()}/bookings/${stale}`, { headers: session.headers }),
    );
    expect(cancelled.status(), 'the cancel elsewhere returns 204').toBe(204);
    await expect(page.getByTestId(`booking-status-${stale}`)).toHaveText('Confirmed');
    await expect(page.getByTestId(`cancel-${stale}`)).toBeEnabled();
  });

  const sent = await test.step('click Cancel: the API answers 409 and the list reloads', async () => {
    const recorded = recordApiRequests(page);
    const refused = page.waitForResponse(
      (r) => isApiEndpoint(r.url(), `/bookings/${stale}`) && r.request().method() === 'DELETE',
    );
    const reloaded = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'GET',
    );
    await page.getByTestId(`cancel-${stale}`).click();
    // Awaited together, so a waiter that fails after the other is still handled.
    const [refusal, reload] = await Promise.all([refused, reloaded]);
    expect(refusal.status(), 'the API refuses the DELETE').toBe(409);
    expect(reload.status(), 'the reload GET returns 200').toBe(200);
    return recorded;
  });

  await test.step('the 409 stays inline above the list, which shows the server status', async () => {
    const error = page.getByTestId('inline-error');
    await expect(error).toBeVisible();
    await expect(error.getByRole('alert').locator('strong')).toHaveText('Booking already cancelled');
    await expect(error.getByRole('alert').locator('p')).toHaveText('This booking has already been cancelled.');
    // Cancel errors show at the top of the list: the alert precedes the table.
    const aboveTable = await error.evaluate((el) => {
      const table = el.ownerDocument.querySelector('[data-testid="bookings-table"]');
      return table !== null && (el.compareDocumentPosition(table) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    });
    expect(aboveTable, 'the inline error precedes the bookings table').toBe(true);
    // The reloaded row reads the server's status, and the refused "Cancel" is
    // gone; the other booking keeps its own.
    await expect(page.getByTestId(`booking-status-${stale}`)).toHaveText('Cancelled');
    await expect(page.getByTestId(`cancel-${stale}`)).toHaveCount(0);
    await expect(page.getByTestId(`booking-status-${other}`)).toHaveText('Confirmed');
    await expect(page.getByTestId(`cancel-${other}`)).toBeEnabled();
    // The clicked "Cancel" held focus and is gone, so focus moves to its row's
    // status.
    await expect(page.getByTestId(`booking-status-${stale}`)).toBeFocused();
    await expect(page).toHaveURL(bookingsUrl);
    // A 409 ends no session: the guest stays signed in with the rows shown.
    await expect(page.getByTestId('nav-email')).toBeVisible();
    await expect(page.getByTestId('bookings-signed-out')).toHaveCount(0);
  });

  await test.step('the reload is the only request after the DELETE', async () => {
    expect(
      sent.map(({ method, url }) => `${method} ${apiRelativeTarget(url)}`),
      'the API requests after the click',
    ).toEqual([`DELETE /api/bookings/${stale}`, 'GET /api/bookings']);
    expect(await apiBookingStatus(request, session, stale), 'the server status of the refused booking').toBe('Cancelled');
    expect(await apiBookingStatus(request, session, other), 'the server status of the other booking').toBe('Confirmed');
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

/** The inventory {@link apiLeaveFreeRooms} arranged. */
interface HeldRoomType {
  /** The fixture guest holding the bookings, which can book the rooms left too. */
  session: ApiSession;
  /** The room type whose free rooms the fixture guest booked, as read before. */
  roomType: FixtureRoomType;
  /** One room of that room type for the stay, as the fixture guest booked it. */
  booking: FixtureBookingRequest;
  /** The hotel's other room types, as the API counted them afterwards. */
  others: FixtureRoomType[];
}

/**
 * Has a fixture guest of its own book rooms of the hotel's first room type for
 * the stay through the API until exactly `left` of them are free, and confirms
 * that count with the API. Every booking is cancelled after the test.
 */
async function apiLeaveFreeRooms(
  request: APIRequestContext,
  hotelId: number,
  stay: Stay,
  left: number,
): Promise<HeldRoomType> {
  const [roomType, ...others] = await apiRoomTypes(request, hotelId, stay);
  expect(others.length, 'the hotel has more than one room type').toBeGreaterThan(0);
  expect(roomType.availableRooms, 'the room type has the rooms to leave free').toBeGreaterThanOrEqual(left);

  const session = await apiSignIn(request, await apiRegisterGuest(request));
  const booking: FixtureBookingRequest = { hotelId, roomTypeId: roomType.id, ...stay };
  for (let free = roomType.availableRooms; free > left; free -= 1) {
    await apiBook(request, session, booking);
  }

  const after = await apiRoomTypes(request, hotelId, stay);
  expect(after.find((rt) => rt.id === roomType.id)?.availableRooms, 'the fixture left the planned rooms free').toBe(left);
  return { session, roomType, booking, others: after.filter((rt) => rt.id !== roomType.id) };
}

/**
 * Runs a hotel search through the search page's form and returns the id of
 * the first hotel it lists, read from its `view-rooms-<id>` hook.
 */
async function searchFirstHotelId(page: Page, criteria: Stay & { city: string }): Promise<number> {
  await searchHotels(page, criteria);
  const testId = (await page.locator(VIEW_ROOMS).first().getAttribute('data-testid')) ?? '';
  expect(testId).toMatch(/^view-rooms-\d+$/);
  return Number(testId.slice('view-rooms-'.length));
}

test('Book while signed out returns to the same stay after login', async ({ page, request }) => {
  const [checkIn, checkOut] = utcDates(130, 132);
  // The account exists, but the browser never signs in before "Book".
  const { email, password } = await test.step('register a guest through the API', () => apiRegisterGuest(request));

  const hotel = await test.step('open a Seattle hotel for the stay, signed out', async () => {
    await page.goto('/search');
    await searchHotels(page, { city: 'Seattle', checkIn, checkOut, guests: 2 });
    const opened = await openHotel(page, page.locator(VIEW_ROOMS).first());
    await expect(page.getByTestId('nav-login')).toBeVisible();
    return opened;
  });
  const shown = new URL(page.url());
  const stayPath = `${shown.pathname}${shown.search}`;
  expect(shown.searchParams.get('checkIn')).toBe(checkIn);
  expect(shown.searchParams.get('checkOut')).toBe(checkOut);
  expect(shown.searchParams.get('guests')).toBe('2');

  // Every method, so a preflight would count too: signed out, nothing goes
  // there. Any origin, so a request to another stack's API counts as well.
  const bookingsRequests: string[] = [];
  page.on('request', (sent) => {
    const url = new URL(sent.url());
    if (url.pathname === '/api/bookings' || url.pathname.startsWith('/api/bookings/')) {
      bookingsRequests.push(`${sent.method()} ${url.origin}${url.pathname}`);
    }
  });

  const bookId = await test.step('click an enabled Book: login opens with this stay as its return URL', async () => {
    const testId = (await page.locator(`${BOOK_BUTTONS}:enabled`).first().getAttribute('data-testid')) ?? '';
    expect(testId).toMatch(/^book-\d+$/);
    await page.getByTestId(testId).click();
    await expect(page).toHaveURL(`/login?returnUrl=${encodeURIComponent(stayPath)}`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Log in');
    expect(bookingsRequests, 'Book while signed out sends no bookings request').toEqual([]);
    return testId;
  });

  await test.step('log in: the same hotel and stay come back, with Book enabled', async () => {
    await logIn(page, email, password);
    await expect(page).toHaveURL(stayPath);
    await expect(page.getByTestId('hotel-name')).toHaveText(hotel.name);
    await expect(page.locator('.hotel-stay')).toHaveText(
      `${displayDate(checkIn)} to ${displayDate(checkOut)}, 2 nights, 2 guests`,
    );
    await expect(page.getByTestId(bookId)).toBeEnabled();
  });

  expect(bookingsRequests, 'no booking was requested').toEqual([]);
});

test('Book is disabled on room types too small for the party', async ({ page }) => {
  const [checkIn, checkOut] = utcDates(135, 137);
  // Signed in, so a disabled "Book" cannot be about the session.
  await test.step('register and log in', () => registerAndLogIn(page));

  await test.step('open a New York hotel for three guests', async () => {
    await searchHotels(page, { city: 'New York', checkIn, checkOut, guests: 3 });
    await openHotel(page, page.locator(VIEW_ROOMS).first());
    await expect(page.locator('.hotel-stay')).toHaveText(
      `${displayDate(checkIn)} to ${displayDate(checkOut)}, 2 nights, 3 guests`,
    );
  });

  await test.step('Book is disabled exactly where max guests is below 3', async () => {
    const rows = page.locator('[data-testid^="room-type-row-"]');
    await expect(rows).not.toHaveCount(0);
    const tooSmall: string[] = [];
    const largeEnough: string[] = [];
    for (const row of await rows.all()) {
      const id = ((await row.getAttribute('data-testid')) ?? '').slice('room-type-row-'.length);
      expect(id).toMatch(/^\d+$/);
      const maxGuests = Number((await row.locator('td').nth(1).innerText()).trim());
      expect(Number.isInteger(maxGuests), `room type ${id} shows a whole max guests`).toBe(true);
      // Every room type has free rooms, so only capacity can disable its "Book".
      await expect(page.getByTestId(`available-count-${id}`)).toHaveText(/^\s*[1-9]\d*\s*$/);
      const book = page.getByTestId(`book-${id}`);
      if (maxGuests < 3) {
        await expect(book).toBeDisabled();
        tooSmall.push(id);
      } else {
        await expect(book).toBeEnabled();
        largeEnough.push(id);
      }
    }
    expect(tooSmall.length, 'a room type is too small for three').toBeGreaterThan(0);
    expect(largeEnough.length, 'a room type takes three').toBeGreaterThan(0);
  });
});

test('Book is disabled on a sold-out room type', async ({ page, request }) => {
  const [checkIn, checkOut] = utcDates(140, 142);
  const stay: Stay = { checkIn, checkOut, guests: 1 };
  // Signed in, so a disabled "Book" cannot be about the session.
  await test.step('register and log in', () => registerAndLogIn(page));
  const hotelId = await test.step('search Chicago for the stay', () =>
    searchFirstHotelId(page, { city: 'Chicago', ...stay }),
  );

  const held = await test.step('a fixture guest books every free room of one room type', () =>
    apiLeaveFreeRooms(request, hotelId, stay, 0),
  );

  await test.step('open the hotel: that room type shows 0 and its Book is disabled', async () => {
    await openHotel(page, page.getByTestId(`view-rooms-${hotelId}`));
    await expect(page.getByTestId(`available-count-${held.roomType.id}`)).toHaveText('0');
    await expect(page.getByTestId(`book-${held.roomType.id}`)).toBeDisabled();
  });

  await test.step('the other room types keep their counts and an enabled Book', async () => {
    for (const other of held.others) {
      await expect(page.getByTestId(`available-count-${other.id}`)).toHaveText(String(other.availableRooms));
      await expect(page.getByTestId(`book-${other.id}`)).toBeEnabled();
    }
  });
});

test('Book answering a real 409 shows it above the room table and refreshes the counts', async ({ page, request }) => {
  const [checkIn, checkOut] = utcDates(150, 152);
  const stay: Stay = { checkIn, checkOut, guests: 1 };
  await test.step('register and log in', async () => {
    // Recorded before "Book" is clicked, so a booking the API wrongly accepts
    // is cancelled after the test, whatever this test reads of its answer.
    cancelGuestBookingsAfterTest(page, await apiSignIn(request, await registerAndLogIn(page)));
  });
  const hotelId = await test.step('search Seattle for the stay', () =>
    searchFirstHotelId(page, { city: 'Seattle', ...stay }),
  );

  const held = await test.step('a fixture guest leaves one room of one room type free', () =>
    apiLeaveFreeRooms(request, hotelId, stay, 1),
  );
  const book = page.getByTestId(`book-${held.roomType.id}`);
  const count = page.getByTestId(`available-count-${held.roomType.id}`);

  const hotelUrl = await test.step('open the hotel: one room is left and its Book is enabled', async () => {
    await openHotel(page, page.getByTestId(`view-rooms-${hotelId}`));
    await expect(count).toHaveText('1');
    await expect(book).toBeEnabled();
    for (const other of held.others) {
      await expect(page.getByTestId(`available-count-${other.id}`)).toHaveText(String(other.availableRooms));
    }
    return page.url();
  });

  // The page has rendered the last room as free; only now does it go.
  await test.step('the fixture guest books that last room', () => apiBook(request, held.session, held.booking));

  const reads: { status: number; url: URL }[] = [];
  page.on('response', (response) => {
    const url = new URL(response.url());
    if (isApiEndpoint(url, `/hotels/${hotelId}`) && response.request().method() === 'GET') {
      reads.push({ status: response.status(), url });
    }
  });

  await test.step('click Book: the API answers 409', async () => {
    const created = page.waitForResponse(
      (r) => isBookingsCollection(new URL(r.url())) && r.request().method() === 'POST',
    );
    await book.click();
    expect((await created).status(), 'Book answers 409').toBe(409);
  });

  await test.step('the conflict shows above the room table, on the same page', async () => {
    const table = page.getByTestId('rooms-scroll');
    // The hotel content holding the table; the late-booking notice sits outside it.
    const error = table.locator('xpath=..').getByTestId('inline-error');
    await expect(error.getByRole('alert').locator('strong')).toHaveText('No room available');
    await expect(error.getByRole('alert').locator('p')).toHaveText(
      `No ${held.roomType.name} room is free from ${checkIn} to ${checkOut}.`,
    );
    await expect(page.getByTestId('inline-error')).toHaveCount(1);
    await expect(page.getByTestId('late-booking')).toHaveCount(0);
    const errorBox = await error.boundingBox();
    const tableBox = await table.boundingBox();
    expect(
      errorBox !== null && tableBox !== null && errorBox.y + errorBox.height <= tableBox.y,
      'the error sits above the room table',
    ).toBe(true);
    await expect(page).toHaveURL(hotelUrl);
    await expect(page.getByTestId('nav-logout')).toBeVisible();
  });

  await test.step('the page reads the stay again: 0 left, Book disabled, other counts unchanged', async () => {
    await expect.poll(() => reads.length, { message: 'the page reads the hotel once more' }).toBe(1);
    const [read] = reads;
    expect(read.status, 'the revalidation GET returns 200').toBe(200);
    expect(read.url.searchParams.get('checkIn')).toBe(checkIn);
    expect(read.url.searchParams.get('checkOut')).toBe(checkOut);
    expect(read.url.searchParams.get('guests')).toBe('1');
    await expect(count).toHaveText('0');
    await expect(book).toBeDisabled();
    for (const other of held.others) {
      await expect(page.getByTestId(`available-count-${other.id}`)).toHaveText(String(other.availableRooms));
      await expect(page.getByTestId(`book-${other.id}`)).toBeEnabled();
    }
    // The conflict stays shown beside the fresh counts.
    await expect(page.getByTestId('inline-error')).toContainText('No room available');
    expect(reads).toHaveLength(1);
  });
});
