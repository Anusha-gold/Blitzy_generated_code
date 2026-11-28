import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { throwError } from 'rxjs';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline, withResponseBody } from '../http/problem-details';
import type { HotelDetail, HotelSearchQuery, HotelSummary, ProblemDetails, RoomTypeAvailability, StayQuery } from './models';

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

/** The media type of every API error response. */
const PROBLEM_MEDIA_TYPE = 'application/problem+json';

/** The `type` the API gives a 404: the RFC 9110 section that defines the status. */
const NOT_FOUND_TYPE = 'https://tools.ietf.org/html/rfc9110#section-15.5.5';

/** The `title` of the API's 404 for an unknown hotel id. */
const UNKNOWN_HOTEL_TITLE = 'Hotel not found';

/** The `detail` of the API's 404 for an unknown hotel id. */
const UNKNOWN_HOTEL_DETAIL = 'No hotel matches the requested id.';

/**
 * Returns the failure {@link HotelsApiService.getHotel} gives a hotel id that
 * is not an integer literal, without sending a request: a 404 that mirrors the
 * API's answer for an unknown hotel id, with the same status, the
 * `application/problem+json` media type and a body with the same `type`,
 * `title`, `status` and `detail`, so `toUiError` shows both alike. The body has
 * no `traceId`, because no server handled the request, and the error has no
 * `url`, because none was requested. Each call returns a new error and body.
 */
function unknownHotelError(): HttpErrorResponse {
  const problem: ProblemDetails = {
    type: NOT_FOUND_TYPE,
    title: UNKNOWN_HOTEL_TITLE,
    status: 404,
    detail: UNKNOWN_HOTEL_DETAIL,
  };
  return new HttpErrorResponse({
    error: problem,
    headers: new HttpHeaders({ 'Content-Type': PROBLEM_MEDIA_TYPE }),
    status: 404,
    statusText: 'Not Found',
  });
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

/** The party size the API's hotel endpoints use when the query omits `guests`. */
const DEFAULT_GUESTS = 1;

/** The length of one UTC calendar day in milliseconds; UTC has no daylight-saving shifts. */
const MS_PER_DAY = 86_400_000;

/**
 * Matches one character that .NET's `char.IsWhiteSpace` accepts, the set
 * `string.Trim()` removes from both ends: U+0009 to U+000D, the Unicode space
 * separators (U+0020, U+00A0, U+1680, U+2000 to U+200A, U+202F, U+205F and
 * U+3000), U+0085, U+2028 and U+2029. Unlike JavaScript's `trim()`, it takes
 * U+0085 and leaves U+FEFF. Each is one UTF-16 code unit, as each .NET `char` is.
 */
const DOTNET_WHITE_SPACE = /^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/;

/**
 * Returns `text` without the white space .NET's `string.Trim()` removes from
 * both ends ({@link DOTNET_WHITE_SPACE}), scanning each end once, so a long
 * text costs linear time.
 */
function trimDotNetWhiteSpace(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && DOTNET_WHITE_SPACE.test(text[start])) {
    start += 1;
  }
  while (end > start && DOTNET_WHITE_SPACE.test(text[end - 1])) {
    end -= 1;
  }
  return text.slice(start, end);
}

/**
 * Returns the party size the API binds from the `guests` text a request
 * carried, or `null` when it binds none, so that request can only get a 400.
 * An omitted `guests` binds {@link DEFAULT_GUESTS}. A present one is read as
 * the API's binder reads it: its surrounding white space trimmed as .NET's
 * `string.Trim()` trims it ({@link trimDotNetWhiteSpace}), then an optional
 * `+` or `-` followed by ASCII digits and nothing else, whose value is a
 * 32-bit signed integer, leading zeros allowed.
 */
function boundGuests(text: string | undefined): number | null {
  if (text === undefined) {
    return DEFAULT_GUESTS;
  }
  const value = trimDotNetWhiteSpace(text);
  if (!/^[+-]?[0-9]+$/.test(value)) {
    return null;
  }
  const guests = Number(value);
  return guests >= MIN_ROUTE_ID && guests <= MAX_ROUTE_ID ? guests : null;
}

/**
 * Whether a complete detail body ({@link isHotelDetail}) answers the request
 * for the hotel `id` and the stay `q`, as every 200 from the API does: its
 * `id` is `id`; its `checkIn` and `checkOut` are the request's texts exactly,
 * because the API accepts only exact `yyyy-MM-dd` dates and echoes them; its
 * `checkIn` is before its `checkOut`; its `nights` is the count of UTC days
 * between them; and its `guests` is the party size the API binds from the
 * request ({@link boundGuests}). The stay rules themselves, such as the
 * earliest check-in or the longest stay, are the server's alone and are not
 * checked here.
 */
function answersRequest(body: HotelDetail, id: number, q: StayQuery): boolean {
  const nights = (Date.parse(`${body.checkOut}T00:00:00Z`) - Date.parse(`${body.checkIn}T00:00:00Z`)) / MS_PER_DAY;
  return (
    body.id === id &&
    body.checkIn === q.checkIn &&
    body.checkOut === q.checkOut &&
    body.checkIn < body.checkOut &&
    body.nights === nights &&
    body.guests === boundGuests(q.guests)
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
 * search list `[]` is valid. A detail body whose echoed hotel id, dates,
 * nights or party size contradict the request fails the same way
 * ({@link HotelsApiService.getHotel}).
 * Apart from that deadline and that check the returned observables are the
 * `HttpClient` requests unchanged, except for a hotel id that is not an
 * integer literal, which sends nothing and fails with a local 404 carrying
 * the API's unknown-hotel problem ({@link HotelsApiService.getHotel}): pages
 * map their errors through `toUiError`, and nothing here retries, caches,
 * transforms or logs them.
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
   * non-integer id, which matches no hotel and no API route. It gets a local
   * 404 instead, with nothing sent, because some non-integer text cannot
   * travel as one path segment: an empty id would address the search, URL
   * parsing collapses the dot segments `.` and `..`, percent-encoded or not,
   * and the API's HTTP server rejects a NUL while parsing the request, before
   * any middleware runs, so its 400 problem carries no CORS headers, the
   * browser withholds it, and the page could only show an unreachable service.
   * The local 404 has the status of the API's answer for an unknown hotel id
   * and carries its problem, as `application/problem+json` with the same
   * `type`, `title` ("Hotel not found"), `status` and `detail` ("No hotel
   * matches the requested id."), but no `traceId`, so the page shows a
   * non-integer id exactly like an unknown one.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0.
   *
   * A 200 is used only when it answers this request: beyond being a complete
   * hotel detail, its `id` is the id sent, its `checkIn` and `checkOut` are
   * the dates sent, exactly, with `checkIn` first, its `nights` is the count
   * of UTC days between them, and its `guests` is the party size the server
   * binds from the `guests` sent: 1 when it was omitted, otherwise its text
   * with the surrounding white space .NET's `string.Trim()` removes, U+0085
   * included and U+FEFF not, read as an optional sign and ASCII digits within
   * the 32-bit signed range. The server echoes every one of them from the
   * request it validated, so a 200 that contradicts the request is for
   * another hotel or stay, and Book must not be offered against it. Such a
   * 200 fails exactly like an incomplete body. The server's stay rules, such
   * as the earliest check-in or the longest stay, are not checked here.
   *
   * @param id - The hotel's id: the route's path segment text, unchanged.
   * @param q - The stay as the page holds it, sent unchanged like the search
   *   criteria.
   * @returns The checked 200 body: the hotel with its room types for the
   *   requested stay; a body that is not a complete hotel detail, or whose
   *   echoed hotel id, dates, nights or party size contradict the request,
   *   fails with that 200 status.
   */
  getHotel(id: string, q: StayQuery): Observable<HotelDetail> {
    const segment = hotelIdSegment(id);
    if (segment === null) {
      return throwError(() => unknownHotelError());
    }
    const params = withStayParams(new HttpParams(), q);
    const url = `${this.config.apiBaseUrl}/hotels/${segment}`;
    const hotelId = Number(segment);
    const isAnswer = (body: unknown): body is HotelDetail => isHotelDetail(body) && answersRequest(body, hotelId, q);
    return this.http
      .get<unknown>(url, { params, observe: 'response' })
      .pipe(withRequestDeadline(url), withResponseBody(isAnswer));
  }
}
