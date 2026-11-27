import { inject } from '@angular/core';
import { Router } from '@angular/router';
import type { CanActivateFn } from '@angular/router';

import { AuthService } from './auth.service';

/**
 * Admits a protected route only for a signed-in guest.
 *
 * Navigation is admitted when a session exists and its `expiresAt` is later
 * than now ({@link AuthService.isAuthenticated}). Otherwise the guard returns a
 * redirect to `/login?returnUrl=<requested URL>`, such as
 * `/login?returnUrl=%2Fbookings`, so the login page can send the guest back
 * afterwards. This covers a missing token, an expired token and a page reload,
 * which starts signed out because the session lives only in memory. The guard
 * never adds `reason=session-expired`: that notice belongs to the bookings
 * page's redirect after a server 401.
 *
 * An expired session is cleared before redirecting, so the nav bar stops
 * showing the guest as signed in. `clearSession` never navigates, so the
 * returned `UrlTree` alone decides where the guest goes.
 *
 * Only the local expiry is checked. A token the API rejects before `expiresAt`
 * cannot be detected here: `authInterceptor` clears the session on a 401 from
 * the bookings endpoints, and the page that sent the request decides whether to
 * redirect to the login page or show the error inline.
 *
 * @param _route - Unused: the decision does not depend on the route.
 * @param state - The router state being activated; its `url` becomes `returnUrl`.
 * @returns `true` to admit, or a `UrlTree` that redirects to the login page.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  if (auth.session() !== null) {
    auth.clearSession();
  }

  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
