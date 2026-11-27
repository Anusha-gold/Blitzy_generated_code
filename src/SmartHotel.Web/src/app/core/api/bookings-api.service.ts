import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { Observable } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { withRequestDeadline } from '../http/problem-details';
import type { Booking, CreateBookingRequest } from './models';

/**
 * Typed client for the guest-only endpoints under `/api/bookings`: booking a
 * room, listing the caller's own bookings and cancelling one of them.
 *
 * Every URL starts with the validated `apiBaseUrl` from {@link API_CONFIG}.
 * All three endpoints require a bearer token, yet this service never sets the
 * `Authorization` header itself: `authInterceptor` attaches it, and only to
 * requests for the API origin under `/api`. No other header is set and no
 * credentials are sent, because the API's CORS policy allows none.
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
 * service is unreachable.". Apart from that deadline the returned observables
 * are the `HttpClient` requests unchanged: pages map their errors through
 * `toUiError`, and nothing here retries, caches, transforms or logs them.
 * `create` above all is never retried, because a repeated POST could book a
 * second room, and the server's 409 must reach the guest. A timed-out `create`
 * or `cancel` is never re-sent either: aborting stops only the wait, so the
 * server may already have applied it, and {@link BookingsApiService.list}
 * reports the actual outcome.
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
   * @param req - The hotel, room type, stay and party size, as the page holds
   *   them. Only these five members are copied into the body, so a caller's
   *   wider object never adds a field. The body carries no amount: the server
   *   alone computes the total.
   * @returns The 201 body: the confirmed booking, with the server-assigned
   *   `roomNumber` and the server-computed total.
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
    return this.http.post<Booking>(url, body).pipe(withRequestDeadline(url));
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
   * `REQUEST_DEADLINE_MS` is aborted and fails with status 0.
   *
   * @returns The 200 body: the caller's bookings, in server order.
   */
  list(): Observable<Booking[]> {
    const url = `${this.config.apiBaseUrl}/bookings`;
    return this.http.get<Booking[]>(url).pipe(withRequestDeadline(url));
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
   * request is not re-sent.
   *
   * @param id - The booking's id, {@link Booking.id}.
   * @returns Emits once when the 204 arrives, carrying no body, then completes.
   */
  cancel(id: number): Observable<void> {
    const url = `${this.config.apiBaseUrl}/bookings/${id}`;
    return this.http.delete<void>(url).pipe(withRequestDeadline(url));
  }
}
