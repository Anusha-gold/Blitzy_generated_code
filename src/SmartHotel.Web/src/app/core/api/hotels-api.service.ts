import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline } from '../http/problem-details';
import type { HotelDetail, HotelSearchQuery, HotelSummary, StayQuery } from './models';

/**
 * Appends the stay criteria to `params` as `checkIn`, `checkOut` and `guests`,
 * in that order, with every value unchanged.
 */
function withStayParams(params: HttpParams, q: StayQuery): HttpParams {
  const stay = params.set('checkIn', q.checkIn).set('checkOut', q.checkOut);
  // An omitted `guests` leaves the parameter out so that the server applies its
  // default of 1. Stringifying it would send the literal "undefined", which the
  // server rejects with 400.
  return q.guests === undefined ? stay : stay.set('guests', q.guests);
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
 * service is unreachable.". Apart from that deadline the returned observables
 * are the `HttpClient` requests unchanged: pages map their errors through
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
   *   sent, an empty string included, so the server answers it with 400; the
   *   exception is an omitted `guests`, which the server takes as 1.
   * @returns The 200 body: the qualifying hotels, in server order.
   */
  search(q: HotelSearchQuery): Observable<HotelSummary[]> {
    const params = withStayParams(new HttpParams().set('city', q.city), q);
    const url = `${this.config.apiBaseUrl}/hotels`;
    return this.http.get<HotelSummary[]>(url, { params }).pipe(withRequestDeadline(url));
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
   * - 404 when no hotel has `id`. A non-integer id, such as `NaN`, matches no
   *   API route, and the server answers it with 404 as well.
   *
   * A request with no response within `REQUEST_DEADLINE_MS` is aborted and
   * fails with status 0.
   *
   * @param id - The hotel's id, as the page's route holds it.
   * @param q - The stay as the page holds it, sent unchanged like the search
   *   criteria.
   * @returns The 200 body: the hotel with its room types.
   */
  getHotel(id: number, q: StayQuery): Observable<HotelDetail> {
    const params = withStayParams(new HttpParams(), q);
    const url = `${this.config.apiBaseUrl}/hotels/${id}`;
    return this.http.get<HotelDetail>(url, { params }).pipe(withRequestDeadline(url));
  }
}
