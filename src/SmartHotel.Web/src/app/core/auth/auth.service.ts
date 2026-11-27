import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { map, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { AuthApiService } from '../api/auth-api.service';

/** A signed-in guest's session, as {@link AuthService} holds it in memory. */
export interface Session {
  /** The JWT bearer token returned by `POST /api/auth/login`, valid for 60 minutes. */
  token: string;
  /**
   * When the token expires: the API's `expiresAt`, a UTC ISO 8601 timestamp in
   * whole seconds, `yyyy-MM-ddTHH:mm:ssZ`.
   */
  expiresAt: string;
  /** The email from the login form, kept for display in the nav bar. */
  email: string;
}

/**
 * The guest's session, held in memory only.
 *
 * Token storage: the token lives only in this service's signal, never in Web
 * Storage (local or session), a cookie, IndexedDB or the URL. This follows the
 * OWASP Session Management Cheat Sheet, because any same-origin script can read
 * those stores. A reload or a new tab therefore starts signed out.
 *
 * A server 401 that the expiry check cannot predict, such as a token the API
 * rejects before `expiresAt`, is handled in two places:
 * - `authInterceptor` calls {@link AuthService.clearSession} on a 401 from
 *   `/api/bookings` or `/api/bookings/…`, and never navigates;
 * - the page that sent the request decides the outcome.
 *   `BookingsPageComponent` answers a 401 on its entry `GET /api/bookings` by
 *   navigating to `/login?returnUrl=%2Fbookings&reason=session-expired`. "Book"
 *   on `HotelDetailPageComponent` and "Cancel" on `BookingsPageComponent` stay
 *   on the page and show the 401 inline with a "Log in again" link.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(AuthApiService);
  private readonly router = inject(Router);
  private readonly _session = signal<Session | null>(null);

  /**
   * The current session, or `null` when signed out. It is not cleared when the
   * token expires; {@link AuthService.isAuthenticated} checks the expiry.
   */
  readonly session = this._session.asReadonly();

  /**
   * Hands the email of a successful registration to the login page without
   * putting it in the URL. The register page sets it after the 201; the login
   * page reads it once when `registered=1`, pre-fills the email and sets it back
   * to `null`.
   */
  readonly pendingLoginEmail = signal<string | null>(null);

  /**
   * Whether a session exists and its `expiresAt` is still in the future.
   *
   * It is a method rather than a `computed` because the clock is not reactive:
   * a computed value would stay `true` after the token expires. An unparseable
   * `expiresAt` counts as expired.
   */
  isAuthenticated(): boolean {
    const current = this._session();
    return current !== null && Date.parse(current.expiresAt) > Date.now();
  }

  /**
   * Logs the guest in and, on success, replaces the session.
   *
   * The observable is cold: nothing is sent until it is subscribed. Any existing
   * session stays in place until the new one is stored. Errors, such as the 401
   * for wrong credentials, the 400 for an unbindable body or status 0 when the
   * API is unreachable, pass through unchanged for the page to show.
   *
   * @param email - The email from the login form, sent unchanged. The server
   *   trims and folds its own copy.
   * @param password - The password from the login form, sent unchanged.
   * @returns The stored session.
   */
  login(email: string, password: string): Observable<Session> {
    return this.api.login({ email, password }).pipe(
      map((r): Session => ({ token: r.token, expiresAt: r.expiresAt, email })),
      tap((s) => this._session.set(s)),
    );
  }

  /**
   * Signs the guest out: discards the session and navigates to `/search`.
   *
   * There is no logout endpoint and no HTTP call, so the discarded token stays
   * valid on the server until it expires, at most 60 minutes later.
   */
  logout(): void {
    this._session.set(null);
    void this.router.navigateByUrl('/search');
  }

  /**
   * Discards the session without navigating. `authInterceptor` calls it on a
   * 401 from the bookings endpoints and `authGuard` on an expired session; the
   * caller decides where the guest goes next.
   */
  clearSession(): void {
    this._session.set(null);
  }
}
