import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { throwError } from 'rxjs';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline, withResponseBody } from '../http/problem-details';
import type { HotelDetail, HotelSearchQuery, HotelSummary, RoomTypeAvailability, StayQuery } from './models';

/**
 * Appends the stay criteria to `params` as `checkIn`, `checkOut` and `guests`,
 * in that order, each as the exact text the page holds, so the server's own
 * binding decides whether it is valid.
 */
function withStayParams(params: HttpParams, q: StayQuery): HttpParams {
  const stay = params.set('checkIn', q.checkIn).set('checkOut', q.checkOut);
  // A present `guests` goes out as its text, an empty one included. An omitted
  // one leaves the parameter out so that the server applies its default of 1;
  // stringifying it would send the literal "undefined", which the server
  // rejects with 400.
  return q.guests === undefined ? stay : stay.set('guests', q.guests);
}

/** The smallest id the API's `{id:int}` route accepts: the 32-bit signed minimum. */
const MIN_ROUTE_ID = -2147483648;

/** The largest id the API's `{id:int}` route accepts: the 32-bit signed maximum. */
const MAX_ROUTE_ID = 2147483647;

/**
 * Returns the path segment that carries the hotel id `text` to the API's
 * `{id:int}` route, or `null` when `text` is not an integer literal.
 *
 * An integer literal is an optional `+` or `-` followed by ASCII digits,
 * nothing else, whose value is a 32-bit signed integer, the range of that
 * route. It is written as its canonical decimal, `String(value)`: the server
 * parses it to the same value, and the segment stays short whatever the
 * number of leading zeros. Any other text, such as `1.0`, `abc`, an empty id,
 * `.`, `..`, a NUL or another control character, white space around the
 * digits or digits beyond that range, gives `null`; it is never converted to
 * a number or percent-encoded.
 */
function hotelIdSegment(text: string): string | null {
  if (!/^[+-]?[0-9]+$/.test(text)) {
    return null;
  }
  const value = Number(text);
  return Number.isSafeInteger(value) && value >= MIN_ROUTE_ID && value <= MAX_ROUTE_ID
    ? String(value)
    : null;
}

/** An exact `yyyy-MM-dd` date, the form the API writes and the detail page compares as text. */
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Whether `value` is a JSON object: neither `null` nor an array. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Whether `value` is a 32-bit signed integer, {@link MIN_ROUTE_ID} to
 * {@link MAX_ROUTE_ID}: the C# `int` that the API serializes for every
 * integer field of its wire models, the same range its `{id:int}` route binds.
 */
function isInt32(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= MIN_ROUTE_ID && value <= MAX_ROUTE_ID;
}

/**
 * Whether `value` is a string holding an exact {@link ISO_DATE} that names a
 * real calendar date: read as midnight UTC, it prints back as the same day, so
 * a day past its month's end, such as `2099-02-30`, which `Date.parse` would
 * roll over into March, fails.
 */
function isIsoDate(value: unknown): value is string {
  if (typeof value !== 'string' || !ISO_DATE.test(value)) {
    return false;
  }
  const ms = Date.parse(`${value}T00:00:00Z`);
  return Number.isFinite(ms) && new Date(ms).toISOString().slice(0, 10) === value;
}

/** Whether `value` is an array whose every element `isItem` accepts; `[]` is one. */
function isArrayOf<T>(value: unknown, isItem: (item: unknown) => item is T): value is T[] {
  if (!Array.isArray(value)) {
    return false;
  }
  const items: readonly unknown[] = value;
  return items.every((item) => isItem(item));
}

/**
 * Whether a search result carries every {@link HotelSummary} field with its
 * wire type: `id` a 32-bit signed integer ({@link isInt32}), `name`, `city`
 * and `address` strings, and `lowestNightlyPrice` a finite number.
 */
function isHotelSummary(value: unknown): value is HotelSummary {
  return (
    isRecord(value) &&
    isInt32(value['id']) &&
    typeof value['name'] === 'string' &&
    typeof value['city'] === 'string' &&
    typeof value['address'] === 'string' &&
    Number.isFinite(value['lowestNightlyPrice'])
  );
}

/** Whether a search 200 body is a list of valid results; `[]`, when no hotel qualifies, is one. */
function isHotelSummaryList(body: unknown): body is HotelSummary[] {
  return isArrayOf(body, isHotelSummary);
}

/**
 * Whether a room type carries every {@link RoomTypeAvailability} field with
 * its wire type: `id`, `maxGuests` and `availableRooms` 32-bit signed
 * integers ({@link isInt32}), `name` a string, and `nightlyPrice` and
 * `stayTotal` finite numbers.
 */
function isRoomTypeAvailability(value: unknown): value is RoomTypeAvailability {
  return (
    isRecord(value) &&
    isInt32(value['id']) &&
    typeof value['name'] === 'string' &&
    isInt32(value['maxGuests']) &&
    Number.isFinite(value['nightlyPrice']) &&
    isInt32(value['availableRooms']) &&
    Number.isFinite(value['stayTotal'])
  );
}

/**
 * Whether a detail 200 body carries every {@link HotelDetail} field with its
 * wire type: `id`, `nights` and `guests` 32-bit signed integers
 * ({@link isInt32}); `name`, `city`, `address` and `description` strings;
 * `checkIn` and `checkOut` exact `yyyy-MM-dd` real calendar dates
 * ({@link isIsoDate}), which the page compares as text and books with; and
 * `roomTypes` a list of valid room types, `[]` included.
 */
function isHotelDetail(body: unknown): body is HotelDetail {
  return (
    isRecord(body) &&
    isInt32(body['id']) &&
    typeof body['name'] === 'string' &&
    typeof body['city'] === 'string' &&
    typeof body['address'] === 'string' &&
    typeof body['description'] === 'string' &&
    isIsoDate(body['checkIn']) &&
    isIsoDate(body['checkOut']) &&
    isInt32(body['nights']) &&
    isInt32(body['guests']) &&
    isArrayOf(body['roomTypes'], isRoomTypeAvailability)
  );
}

/**
 * Typed client for the anonymous catalogue endpoints under `/api/hotels`: the
 * hotel search and the hotel detail.
 *
 * Every URL starts with the validated `apiBaseUrl` from {@link API_CONFIG}.
 * The criteria are sent as query parameters exactly as the page holds them:
 * nothing here trims, defaults, reformats or validates them, so the server
 * alone applies the date, stay-length, city and party-size rules, and its 400
 * and 404 problems reach the page inline. `HttpParams` percent-encodes each
 * value.
 *
 * Each request is bounded by `REQUEST_DEADLINE_MS` through
 * {@link withRequestDeadline}: one with no response by then is aborted and
 * fails as a status-0 `HttpErrorResponse`, which `toUiError` shows as "The
 * service is unreachable.". Each 200 body is checked through
 * {@link withResponseBody} before it is emitted: a search body that is not a
 * list, or a body that lacks any field of its wire model ({@link HotelSummary}
 * for each search result, {@link HotelDetail} with its
 * {@link RoomTypeAvailability} room types) or holds one with another wire
 * type, such as an `id` that is fractional or outside the 32-bit signed range
 * of the API's C# `int`, or a `checkIn` that is not an exact `yyyy-MM-dd`
 * calendar date, fails as an `HttpErrorResponse` carrying that 200, which
 * `toUiError` shows as "The service sent an unexpected response."; an empty
 * search list `[]` is valid.
 * Apart from that deadline and that check the returned observables are the
 * `HttpClient` requests unchanged, except for a hotel id that is not an
 * integer literal, which fails with a local 404 and sends nothing
 * ({@link HotelsApiService.getHotel}): pages map their errors through
 * `toUiError`, and nothing here retries, caches, transforms or logs them.
 * Availability changes with every booking and cancellation, so each call sends
 * a new request.
 */
@Injectable({ providedIn: 'root' })
export class HotelsApiService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(API_CONFIG);

  /**
   * Searches a city for a stay: `GET /api/hotels`, anonymous.
   *
   * The 200 body lists the hotels that have at least one room type able to
   * host `guests` with a room free for the whole stay. Each carries
   * `lowestNightlyPrice`, the lowest nightly rate among those room types, and
   * the list is ordered by that rate, then by name. `[]` means no hotel
   * qualifies, an unknown city included.
   *
   * Error: 400, as `application/problem+json` with field errors in `errors`,
   * when a date is missing or is not an exact `yyyy-MM-dd` calendar date,
   * `checkOut` is not after `checkIn`, `checkIn` is before today (UTC), the
   * stay is longer than 30 nights, `city` is missing, blank or longer than 100
   * characters, or `guests` is not an integer of at least 1. A request with no
   * response within `REQUEST_DEADLINE_MS` is aborted and fails with status 0.
   *
   * @param q - The city and stay as the page holds them. Every parameter is
   *   sent as its text, an empty string included, so the server decides it:
   *   an empty city, date or `guests` gets 400; only an omitted `guests` the
   *   server takes as 1.
   * @returns The checked 200 body: the qualifying hotels, in server order; a
   *   body that is not a list of complete results fails with that 200 status.
   */
  search(q: HotelSearchQuery): Observable<HotelSummary[]> {
    const params = withStayParams(new HttpParams().set('city', q.city), q);
    const url = `${this.config.apiBaseUrl}/hotels`;
    return this.http
      .get<unknown>(url, { params, observe: 'response' })
      .pipe(withRequestDeadline(url), withResponseBody(isHotelSummaryList));
  }

  /**
   * Reads one hotel for a stay: `GET /api/hotels/{id}`, anonymous.
   *
   * The 200 body echoes the validated stay (`checkIn`, `checkOut`, `nights`
   * and the effective `guests`) and lists every room type of the hotel, ordered
   * by nightly rate. Each room type carries `availableRooms`, the count of its
   * rooms with no confirmed booking overlapping the stay, which is physical
   * inventory whatever `guests` is, and `stayTotal`, computed on the server.
   *
   * Errors, as `application/problem+json`:
   * - 400 for the date and `guests` rules of {@link HotelsApiService.search},
   *   checked before the hotel is looked up;
   * - 404 when no hotel has `id`.
   *
   * An id that is an integer literal, an optional sign and ASCII digits within
   * the 32-bit signed range, is sent as its canonical decimal, so the server
   * keeps its own order for it: a 400 for the stay or `guests` before a 404
   * for an unknown hotel. Any other id, such as `1.0` or `abc`, is a
   * non-integer id, which the API's `{id:int}` route answers with 404. That
   * 404 is given here instead, with nothing sent, because some non-integer
   * text cannot travel as one path segment: an empty id would address the
   * search, URL parsing collapses the dot segments `.` and `..`,
   * percent-encoded or not, and the API's HTTP server rejects a NUL before
   * routing with a bare 400 that carries no CORS headers, which the page could
   * only show as an unreachable service. The local 404 is an
   * `HttpErrorResponse` with no body.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0.
   *
   * @param id - The hotel's id: the route's path segment text, unchanged.
   * @param q - The stay as the page holds it, sent unchanged like the search
   *   criteria.
   * @returns The checked 200 body: the hotel with its room types; a body
   *   that is not a complete hotel detail fails with that 200 status.
   */
  getHotel(id: string, q: StayQuery): Observable<HotelDetail> {
    const segment = hotelIdSegment(id);
    if (segment === null) {
      return throwError(() => new HttpErrorResponse({ status: 404, statusText: 'Not Found' }));
    }
    const params = withStayParams(new HttpParams(), q);
    const url = `${this.config.apiBaseUrl}/hotels/${segment}`;
    return this.http
      .get<unknown>(url, { params, observe: 'response' })
      .pipe(withRequestDeadline(url), withResponseBody(isHotelDetail));
  }
}
