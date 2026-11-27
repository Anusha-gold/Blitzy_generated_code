import { DestroyRef, Injectable, NgZone, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { map, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { AuthApiService } from '../api/auth-api.service';

/** The longest delay `setTimeout` honours; a longer one fires at once. */
const MAX_TIMER_DELAY_MS = 2_147_483_647;

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
 * Expiry: storing a session starts a timer for its `expiresAt`, and the timer
 * clears the session once that instant has passed, so the nav bar shows the
 * guest signed out on any page without a request being sent. Logging out,
 * clearing the session and a new login each cancel the pending timer. It runs
 * outside the Angular zone, so the wait never keeps the app from becoming
 * stable; only the clearing re-enters the zone to update the views.
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
  private readonly zone = inject(NgZone);
  private readonly _session = signal<Session | null>(null);

  /** The timer that clears the current session at its expiry, or `null` when none is pending. */
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * The current session, or `null` when signed out. It becomes `null` once its
   * `expiresAt` has passed, when the expiry timer fires. A timer can fire late,
   * for example in a throttled background tab or after the device sleeps, so
   * {@link AuthService.isAuthenticated} still compares the clock.
   */
  readonly session = this._session.asReadonly();

  /**
   * Hands the email of a successful registration to the login page without
   * putting it in the URL. The register page sets it after the 201; the login
   * page reads it once when `registered=1`, pre-fills the email and sets it back
   * to `null`. {@link AuthService.logout} also sets it to `null`.
   */
  readonly pendingLoginEmail = signal<string | null>(null);

  constructor() {
    // The root injector's teardown, such as the app being destroyed, leaves no
    // timer behind to fire into it.
    inject(DestroyRef).onDestroy(() => this.cancelExpiry());
  }

  /**
   * Whether a session exists and its `expiresAt` is still in the future.
   *
   * It is a method rather than a `computed` because the clock is not reactive:
   * until the expiry timer fires, which can be late, the session signal still
   * holds the expired session, so a computed value would stay `true`. An
   * unparseable `expiresAt` counts as expired.
   */
  isAuthenticated(): boolean {
    const current = this._session();
    return current !== null && Date.parse(current.expiresAt) > Date.now();
  }

  /**
   * Logs the guest in and, on success, replaces the session.
   *
   * The observable is cold: nothing is sent until it is subscribed. Any existing
   * session stays in place until the new one is stored, and storing it replaces
   * the previous session's expiry timer with one for the new `expiresAt`.
   * Errors, such as the 401 for wrong credentials, the 400 for an unbindable
   * body or status 0 when the API is unreachable, pass through unchanged for the
   * page to show.
   *
   * @param email - The email from the login form, sent unchanged. The server
   *   trims and folds its own copy.
   * @param password - The password from the login form, sent unchanged.
   * @returns The stored session.
   */
  login(email: string, password: string): Observable<Session> {
    return this.api.login({ email, password }).pipe(
      map((r): Session => ({ token: r.token, expiresAt: r.expiresAt, email })),
      tap((s) => this.store(s)),
    );
  }

  /**
   * Signs the guest out: discards the session and any unconsumed
   * {@link AuthService.pendingLoginEmail}, so a later login page never
   * pre-fills a previous guest's email, and navigates to `/search`.
   *
   * There is no logout endpoint and no HTTP call, so the discarded token stays
   * valid on the server until it expires, at most 60 minutes later.
   */
  logout(): void {
    this.store(null);
    this.pendingLoginEmail.set(null);
    void this.router.navigateByUrl('/search');
  }

  /**
   * Discards the session without navigating. `authInterceptor` calls it on a
   * 401 from the bookings endpoints and `authGuard` on an expired session; the
   * caller decides where the guest goes next. It leaves
   * {@link AuthService.pendingLoginEmail} as it is.
   */
  clearSession(): void {
    this.store(null);
  }

  /**
   * Replaces the session together with its expiry timer: the pending timer is
   * cancelled, and a new session gets one for its own `expiresAt`.
   */
  private store(session: Session | null): void {
    this.cancelExpiry();
    this._session.set(session);
    if (session !== null) {
      this.scheduleExpiry(session);
    }
  }

  /**
   * Starts the timer that clears `session` once its `expiresAt` has passed.
   *
   * The delay is capped at the longest one `setTimeout` honours. An unparseable
   * or past `expiresAt` gives a delay of `0`, so such a session is cleared on
   * the next task, matching {@link AuthService.isAuthenticated}.
   */
  private scheduleExpiry(session: Session): void {
    const remaining = Date.parse(session.expiresAt) - Date.now();
    const delay = remaining > 0 ? Math.min(remaining, MAX_TIMER_DELAY_MS) : 0;
    this.expiryTimer = this.zone.runOutsideAngular(() => setTimeout(() => this.expire(session), delay));
  }

  /**
   * Runs when the expiry timer fires. A timer whose session has been replaced
   * does nothing. While `expiresAt` is still ahead, because the delay was capped
   * or the system clock moved back, the timer starts again. Otherwise the
   * session is cleared inside the Angular zone, so the views reading
   * {@link AuthService.session} re-render.
   */
  private expire(session: Session): void {
    this.expiryTimer = null;
    if (this._session() !== session) {
      return;
    }
    if (Date.parse(session.expiresAt) > Date.now()) {
      this.scheduleExpiry(session);
      return;
    }
    this.zone.run(() => this._session.set(null));
  }

  /** Cancels the pending expiry timer, if there is one. */
  private cancelExpiry(): void {
    if (this.expiryTimer !== null) {
      clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
  }
}
