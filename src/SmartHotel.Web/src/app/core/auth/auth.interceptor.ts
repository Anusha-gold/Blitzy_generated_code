import { inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import type { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

import { API_CONFIG } from '../config/runtime-config';
import { AuthService } from './auth.service';

/**
 * Whether `pathname` is `boundary` itself or a path beneath it. A bare prefix
 * test is not enough: it would put `/apix` inside `/api`.
 */
function isWithin(pathname: string, boundary: string): boolean {
  return pathname === boundary || pathname.startsWith(boundary + '/');
}

/**
 * Resolves a request URL as the browser will send it: an absolute URL as it
 * stands, a relative one against the document's base URI. A URL that does not
 * parse is not an API URL, so `null` keeps the token off it and leaves the
 * failure to the backend.
 */
function resolveRequestUrl(url: string): URL | null {
  try {
    return new URL(url, document.baseURI);
  } catch {
    return null;
  }
}

/**
 * Attaches the guest's bearer token to API requests and discards the session
 * when the bookings endpoints reject it.
 *
 * Attachment rule: `Authorization: Bearer <token>` is set only when a session
 * exists, the request URL's origin equals `apiOrigin` from {@link API_CONFIG},
 * and its path is `apiPath` (`/api`) itself or lies under `/api/`. The token
 * therefore never leaves for another origin, the web origin and `/config.json`
 * included, nor for a look-alike path such as `/apix`, so only the API ever
 * receives it. Expiry is not checked here: the server is the authority, and an
 * expired token gets its 401.
 *
 * 401 rule: a 401 from `/api/bookings` or a path under it (the list, "Book" and
 * "Cancel") calls {@link AuthService.clearSession}, so the nav bar shows "Log
 * in" at once. A 401 from any other path, such as a failed login, leaves the
 * session in place. The interceptor never changes the route and never
 * swallows, transforms or retries an error: it rethrows the original error
 * object, and the page that sent the request decides between redirecting to
 * login and showing the error inline.
 *
 * `app.config.ts` registers it after `loadingInterceptor`, so the loading
 * counter is outermost and counts every request, including the clone made here.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const config = inject(API_CONFIG);

  const url = resolveRequestUrl(req.url);
  const apiOriginPath = url !== null && url.origin === config.apiOrigin ? url.pathname : null;
  const isApi = apiOriginPath !== null && isWithin(apiOriginPath, config.apiPath);
  const isBookings = apiOriginPath !== null && isWithin(apiOriginPath, config.apiPath + '/bookings');

  const session = auth.session();
  const request =
    isApi && session !== null
      ? req.clone({ setHeaders: { Authorization: `Bearer ${session.token}` } })
      : req;

  return next(request).pipe(
    catchError((err: unknown) => {
      // Only the session whose token this request carried is discarded: a
      // session stored by a login while the request was in flight holds a
      // token the server has not rejected.
      if (
        isBookings &&
        err instanceof HttpErrorResponse &&
        err.status === 401 &&
        session !== null &&
        auth.session() === session
      ) {
        auth.clearSession();
      }
      return throwError(() => err);
    }),
  );
};
