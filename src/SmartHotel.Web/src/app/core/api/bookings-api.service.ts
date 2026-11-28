import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import type { HttpResponse } from '@angular/common/http';
import { map, throwError } from 'rxjs';
import type { MonoTypeOperatorFunction, Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline, withResponseBody } from '../http/problem-details';
import type { Booking, BookingStatus, CreateBookingRequest, ProblemDetails } from './models';

/** The smallest value of the API's C# `int`: the 32-bit signed minimum. */
const MIN_INT32 = -2147483648;

/** The largest value of the API's C# `int`, and so of every booking id: the 32-bit signed maximum. */
const MAX_INT32 = 2147483647;

/** The statusText of the error {@link withStatus} raises for a success status other than the endpoint's own. */
const UNEXPECTED_STATUS_TEXT = 'Unexpected response status';

/** The media type of every API error response. */
const PROBLEM_MEDIA_TYPE = 'application/problem+json';

/** The `type` the API gives a 404: the RFC 9110 section that defines the status. */
const NOT_FOUND_TYPE = 'https://tools.ietf.org/html/rfc9110#section-15.5.5';

/** The `title` of the API's 404 for an unknown booking id. */
const UNKNOWN_BOOKING_TITLE = 'Booking not found';

/** The `detail` of the API's 404 for an unknown booking id. */
const UNKNOWN_BOOKING_DETAIL = 'No booking matches the requested id.';

/**
 * An exact `yyyy-MM-dd` date, the form the API writes `checkIn` and `checkOut`
 * in, in a year from 0001 to 9999: the range of the C# `DateOnly` the API
 * serializes. Year 0000, which JavaScript dates accept, is refused.
 */
const ISO_DATE = /^(?!0000)\d{4}-\d{2}-\d{2}$/;

/**
 * A UTC instant in the form the API writes `createdAt` in:
 * `yyyy-MM-ddTHH:mm:ss`, then a `.` and 1 to 7 fractional-second digits
 * unless the instant is a whole second, then `Z`, in a year from 0001 to 9999:
 * the range of the C# `DateTime` the API serializes. Year 0000, which
 * JavaScript dates accept, is refused. The API's serializer writes a
 * `DateTime` with up to seven fractional digits, the 100-nanosecond ticks, and
 * drops trailing zeros; the API stamps bookings to the microsecond, so its own
 * timestamps carry at most six.
 */
const UTC_TIMESTAMP = /^(?!0000)\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/;

/**
 * Passes on a full `HttpClient` response whose status is `expected`, the
 * endpoint's one success status, and fails on any other with an
 * `HttpErrorResponse` carrying the response's own status, URL and headers,
 * statusText "Unexpected response status" and no body. `HttpClient` emits
 * every 2xx whose body it can parse as a success, so without this check a 200
 * to the DELETE would read as a cancellation and a 200 to the POST as a
 * confirmed booking. A 2xx it cannot parse arrives as its own error and never
 * reaches this operator. The error's 2xx status makes `toUiError` show "The
 * service sent an unexpected response.". The operator never retries and never
 * logs.
 *
 * @param expected - The endpoint's success status: 201, 200 or 204.
 * @returns An operator that mirrors a response of that status.
 */
function withStatus(expected: number): MonoTypeOperatorFunction<HttpResponse<unknown>> {
  return map((response) => {
    if (response.status === expected) {
      return response;
    }
    throw new HttpErrorResponse({
      status: response.status,
      statusText: UNEXPECTED_STATUS_TEXT,
      url: response.url ?? undefined,
      headers: response.headers,
    });
  });
}

/**
 * Returns the failure {@link BookingsApiService.cancel} gives a booking id
 * that is not a positive 32-bit integer, without sending a request: a 404
 * that mirrors the API's answer for an unknown booking id, with the same
 * status, the `application/problem+json` media type and a body with the same
 * `type`, `title`, `status` and `detail`, so `toUiError` shows both alike. The
 * body has no `traceId`, because no server handled the request, and the error
 * has no `url`, because none was requested. Each call returns a new error and
 * body.
 */
function unknownBookingError(): HttpErrorResponse {
  const problem: ProblemDetails = {
    type: NOT_FOUND_TYPE,
    title: UNKNOWN_BOOKING_TITLE,
    status: 404,
    detail: UNKNOWN_BOOKING_DETAIL,
  };
  return new HttpErrorResponse({
    error: problem,
    headers: new HttpHeaders({ 'Content-Type': PROBLEM_MEDIA_TYPE }),
    status: 404,
    statusText: 'Not Found',
  });
}

/** Whether `value` is a JSON object: neither `null` nor an array. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Whether `value` is a 32-bit signed integer, {@link MIN_INT32} to
 * {@link MAX_INT32}: the C# `int` the API serializes for every integer field
 * of a booking.
 */
function isInt32(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= MIN_INT32 && value <= MAX_INT32;
}

/**
 * Whether `value` is a positive 32-bit signed integer, 1 to
 * {@link MAX_INT32}: the range of the ids the API assigns to bookings, hotels
 * and room types. Such a number prints as its canonical decimal, so it is safe
 * in a `data-testid` and as the `{id}` path segment of a request.
 */
function isPositiveInt32(value: unknown): value is number {
  return isInt32(value) && value >= 1;
}

/**
 * Whether `value` is a string holding an exact {@link ISO_DATE}, in a year
 * from 0001 to 9999, that names a real calendar date: read as midnight UTC, it
 * prints back as the same day, so a day past its month's end, such as
 * `2099-02-30`, which `Date.parse` would roll over into March, fails.
 */
function isIsoDate(value: unknown): value is string {
  if (typeof value !== 'string' || !ISO_DATE.test(value)) {
    return false;
  }
  const ms = Date.parse(`${value}T00:00:00Z`);
  return Number.isFinite(ms) && new Date(ms).toISOString().slice(0, 10) === value;
}

/**
 * Whether `value` is a string holding an exact {@link UTC_TIMESTAMP}, in a
 * year from 0001 to 9999, that names a real instant. `Date.parse` alone also
 * reads a day past its month's end, such as `2026-02-30T00:00:00Z`, or the
 * hour `24`, by rolling it over, so the parsed instant must print back with
 * the same date and time to the second.
 */
function isUtcTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !UTC_TIMESTAMP.test(value)) {
    return false;
  }
  const ms = Date.parse(value);
  return Number.isFinite(ms) && new Date(ms).toISOString().slice(0, 19) === value.slice(0, 19);
}

/**
 * Whether `value` is a finite number with at most two decimal places, the form
 * the API writes every USD amount in. A JSON number such as `129.99` parses to
 * the double nearest that decimal; scaled to cents, rounded and scaled back,
 * it gives exactly that double again for every two-decimal amount below 10
 * trillion dollars, far above any total the API stores, while one with more
 * decimals, such as `200.001`, does not. No bound is applied: the server alone
 * sets prices and totals.
 */
function isUsdAmount(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && Math.round(value * 100) / 100 === value;
}

/** Whether `value` is one of the two {@link BookingStatus} names the API writes. */
function isBookingStatus(value: unknown): value is BookingStatus {
  return value === 'Confirmed' || value === 'Cancelled';
}

/**
 * Whether a booking carries every {@link Booking} field with its wire type:
 * `id`, `hotelId` and `roomTypeId` positive 32-bit integers
 * ({@link isPositiveInt32}); `hotelName`, `roomTypeName` and `roomNumber`
 * strings; `checkIn` and `checkOut` exact `yyyy-MM-dd` real calendar dates
 * in the years 0001 to 9999 ({@link isIsoDate}); `nights` and `guests` 32-bit
 * signed integers ({@link isInt32}); `totalPrice` a finite number with at most
 * two decimal places ({@link isUsdAmount}); `status` `Confirmed` or
 * `Cancelled`; and `createdAt` a UTC instant in the years 0001 to 9999 ending
 * in `Z` ({@link isUtcTimestamp}). Only the wire shape is checked, never a
 * booking rule such as the stay length, the order of the dates or the size of
 * the total: the server alone applies those.
 */
function isBooking(value: unknown): value is Booking {
  return (
    isRecord(value) &&
    isPositiveInt32(value['id']) &&
    isPositiveInt32(value['hotelId']) &&
    typeof value['hotelName'] === 'string' &&
    isPositiveInt32(value['roomTypeId']) &&
    typeof value['roomTypeName'] === 'string' &&
    typeof value['roomNumber'] === 'string' &&
    isIsoDate(value['checkIn']) &&
    isIsoDate(value['checkOut']) &&
    isInt32(value['nights']) &&
    isInt32(value['guests']) &&
    isUsdAmount(value['totalPrice']) &&
    isBookingStatus(value['status']) &&
    isUtcTimestamp(value['createdAt'])
  );
}

/**
 * Whether a create 201 body is a valid booking ({@link isBooking}) whose
 * `status` is `Confirmed`, the status the API gives every booking it creates.
 */
function isConfirmedBooking(body: unknown): body is Booking {
  return isBooking(body) && body.status === 'Confirmed';
}

/**
 * Whether a list 200 body is an array of valid bookings ({@link isBooking})
 * whose ids are all distinct; `[]`, when there are none, is one. My bookings
 * keys each row, its "Cancel" button and its status cell by booking id, and a
 * cancellation marks the row with that id `Cancelled`, so two bookings sharing
 * an id would make "Cancel" ambiguous and could mark both rows.
 */
function isBookingList(body: unknown): body is Booking[] {
  if (!Array.isArray(body)) {
    return false;
  }
  const items: readonly unknown[] = body;
  if (!items.every((item): item is Booking => isBooking(item))) {
    return false;
  }
  return new Set(items.map((booking) => booking.id)).size === items.length;
}

/**
 * Typed client for the guest-only endpoints under `/api/bookings`: booking a
 * room, listing the caller's own bookings and cancelling one of them.
 *
 * Every URL starts with the validated `apiBaseUrl` from {@link API_CONFIG}.
 * All three endpoints require a bearer token, yet this service never sets the
 * `Authorization` header itself: `authInterceptor` supplies the bearer header,
 * and only for requests to the API origin under `/api`. `HttpClient` sets
 * `Content-Type: application/json` for the JSON body of
 * {@link BookingsApiService.create}. This client never enables
 * `withCredentials`, so no cookies or other ambient browser credentials are
 * sent cross-origin, which the API's CORS policy does not allow anyway.
 *
 * On a 401 from these endpoints the interceptor clears the session and passes
 * the error on, and the page that issued the request decides what follows.
 * `BookingsPageComponent` redirects to the login page when its entry
 * {@link BookingsApiService.list} call is rejected, while "Book"
 * ({@link BookingsApiService.create}) and "Cancel"
 * ({@link BookingsApiService.cancel}) stay on their page and show the 401
 * inline with a "Log in again" link.
 *
 * Each request is bounded by `REQUEST_DEADLINE_MS` through
 * {@link withRequestDeadline}: one with no response by then is aborted and
 * fails as a status-0 `HttpErrorResponse`, which `toUiError` shows as "The
 * service is unreachable.". Each success is then checked before it is
 * emitted. Its status must be the endpoint's own: 201 for `create`, 200 for
 * `list` and 204 for `cancel`. Each booking in a body must carry every
 * {@link Booking} field with its wire type: ids that are positive 32-bit
 * integers, `checkIn` and `checkOut` exact `yyyy-MM-dd` calendar dates in the
 * years 0001 to 9999, `totalPrice` a finite number with at most two decimal
 * places, `status` `Confirmed` or `Cancelled` and `createdAt` a UTC instant in
 * those years ending in `Z`. The 201 booking must also be `Confirmed`, and the
 * 200 list must be an array of such bookings with distinct ids, `[]` included.
 * Another 2xx status, or a body that fails its check through
 * {@link withResponseBody}, fails as an `HttpErrorResponse` carrying that 2xx
 * status and no body. A 2xx body that `HttpClient` cannot parse as JSON never
 * reaches these checks: `HttpClient` itself fails it as an `HttpErrorResponse`
 * with that 2xx status whose `error` holds the parse exception and the
 * response text, and that error passes through unchanged. `toUiError` shows
 * each of them as "The service sent an unexpected response.". Only the wire
 * shape is checked; the server alone applies the booking rules. A `create`
 * that fails this check may still have booked the room, and
 * {@link BookingsApiService.list} reports the outcome.
 *
 * Apart from that deadline and those checks the returned observables are the
 * `HttpClient` requests unchanged, except for a booking id that is not a
 * positive 32-bit integer, which `cancel` never sends and fails with a local
 * 404 carrying the API's unknown-booking problem: pages map their errors
 * through `toUiError`, and nothing here retries, caches, transforms or logs
 * them. `create` above all is never retried, because a repeated POST could
 * book a second room, and the server's 409 must reach the guest. A timed-out
 * `create` or `cancel` is never re-sent either: aborting stops only the wait,
 * so the server may already have applied it, and
 * {@link BookingsApiService.list} reports the actual outcome.
 */
@Injectable({ providedIn: 'root' })
export class BookingsApiService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(API_CONFIG);

  /**
   * Books a room of a hotel's room type for a stay: `POST /api/bookings`,
   * bearer token required.
   *
   * The server assigns the room, computes the total and confirms the booking
   * at once; no payment is taken online. The body is a plain object, so it is
   * sent as JSON with `Content-Type: application/json`.
   *
   * Errors, as `application/problem+json`:
   * - 400 for invalid input, with field errors in `errors`: a missing or
   *   non-positive `hotelId`, `roomTypeId` or `guests`; a date that is missing
   *   or is not an exact `yyyy-MM-dd` calendar date; `checkOut` not after
   *   `checkIn`; `checkIn` before today (UTC); a stay longer than 30 nights;
   *   or, keyed `guests`, a party larger than the room type's `maxGuests`;
   * - 401 when the bearer token is missing, invalid or expired;
   * - 404 when the hotel is unknown, or the room type is unknown or belongs
   *   to another hotel;
   * - 409 when no room of that type is free for the dates.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0. The booking may still have been created, and the
   * request is not re-sent.
   *
   * A success other than 201, or a 201 whose body is not a complete
   * `Confirmed` booking, fails as an `HttpErrorResponse` with that 2xx status
   * and no body. A 2xx body that `HttpClient` cannot parse as JSON fails
   * before those checks, as `HttpClient`'s own `HttpErrorResponse` with that
   * status carrying the parse exception and the response text, passed on
   * unchanged. `toUiError` shows both as "The service sent an unexpected
   * response.". As after a timeout, the booking may still have been created:
   * nothing is re-sent, and {@link BookingsApiService.list} reports the
   * outcome.
   *
   * @param req - The hotel, room type, stay and party size, as the page holds
   *   them. Only these five members are copied into the body, so a caller's
   *   wider object never adds a field. The body carries no amount: the server
   *   alone computes the total.
   * @returns The checked 201 body: the confirmed booking, with the
   *   server-assigned `roomNumber` and the server-computed total.
   */
  create(req: CreateBookingRequest): Observable<Booking> {
    const body: CreateBookingRequest = {
      hotelId: req.hotelId,
      roomTypeId: req.roomTypeId,
      checkIn: req.checkIn,
      checkOut: req.checkOut,
      guests: req.guests,
    };
    const url = `${this.config.apiBaseUrl}/bookings`;
    return this.http
      .post<unknown>(url, body, { observe: 'response' })
      .pipe(withRequestDeadline(url), withStatus(201), withResponseBody(isConfirmedBooking));
  }

  /**
   * Lists the caller's own bookings: `GET /api/bookings`, bearer token
   * required.
   *
   * The 200 body holds the caller's bookings in both statuses, `Confirmed` and
   * `Cancelled`, newest `createdAt` first, then highest `id`. `[]` means the
   * guest has no bookings.
   *
   * Error: 401, as `application/problem+json`, when the bearer token is
   * missing, invalid or expired. A request with no response within
   * `REQUEST_DEADLINE_MS` is aborted and fails with status 0. A success other
   * than 200, or a 200 whose body is not an array of complete bookings with
   * distinct ids, fails as an `HttpErrorResponse` with that 2xx status and no
   * body. A 2xx body that `HttpClient` cannot parse as JSON fails before those
   * checks, as `HttpClient`'s own `HttpErrorResponse` with that status
   * carrying the parse exception and the response text, passed on unchanged.
   * `toUiError` shows both as "The service sent an unexpected response.".
   *
   * @returns The checked 200 body: the caller's bookings, in server order,
   *   each with its own positive 32-bit integer `id`.
   */
  list(): Observable<Booking[]> {
    const url = `${this.config.apiBaseUrl}/bookings`;
    return this.http
      .get<unknown>(url, { observe: 'response' })
      .pipe(withRequestDeadline(url), withStatus(200), withResponseBody(isBookingList));
  }

  /**
   * Cancels one of the caller's own bookings: `DELETE /api/bookings/{id}`,
   * bearer token required.
   *
   * The 204 response has no body. The booking's status becomes `Cancelled`,
   * which frees its room for those dates again.
   *
   * Errors, as `application/problem+json`, in the order the server checks
   * them:
   * - 401 when the bearer token is missing, invalid or expired;
   * - 404 when no booking has `id`;
   * - 403 when the booking belongs to another guest;
   * - 409 when the booking is already cancelled, or its check-in date is
   *   before today (UTC).
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0. The booking may still have been cancelled, and the
   * request is not re-sent. A success other than 204 fails as an
   * `HttpErrorResponse` with that 2xx status and no body, or, when
   * `HttpClient` cannot parse its body as JSON, as `HttpClient`'s own
   * `HttpErrorResponse` with that status carrying the parse exception and the
   * response text, passed on unchanged. `toUiError` shows both as "The service
   * sent an unexpected response.", so the page never reads either as a
   * cancellation.
   *
   * An `id` that is not a positive 32-bit integer, such as `0`, `-1`, `1.5`
   * or `NaN`, matches no booking the API assigns, so nothing is sent: it fails
   * at once with a local 404 that has the status of the API's answer for an
   * unknown booking id and carries its problem, as `application/problem+json`
   * with the same `type`, `title` ("Booking not found"), `status` and `detail`
   * ("No booking matches the requested id."), but no `traceId`. Any other id
   * is sent as its canonical decimal.
   *
   * @param id - The booking's id, {@link Booking.id}.
   * @returns Emits once when the 204 arrives, carrying no value, then
   *   completes.
   */
  cancel(id: number): Observable<void> {
    if (!isPositiveInt32(id)) {
      return throwError(() => unknownBookingError());
    }
    const url = `${this.config.apiBaseUrl}/bookings/${String(id)}`;
    return this.http
      .delete<unknown>(url, { observe: 'response' })
      .pipe(withRequestDeadline(url), withStatus(204), map((): void => undefined));
  }
}
