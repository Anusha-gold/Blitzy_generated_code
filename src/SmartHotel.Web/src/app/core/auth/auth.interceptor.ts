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
 * The text the API's `{id:int}` route constraint accepts before its 32-bit
 * range check: `int.TryParse` with `NumberStyles.Integer` and the invariant
 * culture.
 */
const ROUTE_INT = /^[\t\n\v\f\r ]*[+-]?[0-9]+[\t\n\v\f\r ]*$/;

/**
 * Whether a request is one of the three endpoints whose 401 means the server
 * rejected the session's token, `GET /api/bookings`, `POST /api/bookings` and
 * `DELETE /api/bookings/{id:int}`, matched the way the API's endpoint routing
 * matches them:
 * - one trailing slash is ignored;
 * - the path is split on `/` before each segment is percent-decoded, because
 *   the server decodes every escape except `%2F`, so an encoded slash stays
 *   inside its segment and never matches; a malformed escape never matches;
 * - the literal `bookings` is compared ignoring ASCII case only, as routing
 *   does: the non-unicode `i` flag never folds a non-ASCII character, such as
 *   the Kelvin sign or a dotless i, to an ASCII letter;
 * - the id satisfies `{id:int}`: optional leading and trailing white space
 *   (tab, line feed, vertical tab, form feed, carriage return or space), an
 *   optional sign and ASCII digits whose value fits a 32-bit signed integer.
 * Any other method, a deeper path, an empty segment or another id is no match.
 *
 * @param method - The request method; `HttpRequest` has already upper-cased it.
 * @param apiRelativePath - The URL pathname after `apiPath`, such as `/bookings/5`.
 * @returns `true` only for one of the three endpoints.
 */
function isBookingsEndpoint(method: string, apiRelativePath: string): boolean {
  const path = apiRelativePath.endsWith('/') ? apiRelativePath.slice(0, -1) : apiRelativePath;
  if (!path.startsWith('/')) {
    return false;
  }
  const segments: string[] = [];
  for (const raw of path.slice(1).split('/')) {
    try {
      segments.push(decodeURIComponent(raw));
    } catch {
      return false;
    }
  }
  if (!/^bookings$/i.test(segments[0])) {
    return false;
  }
  if (segments.length === 1) {
    return method === 'GET' || method === 'POST';
  }
  if (segments.length !== 2 || method !== 'DELETE' || !ROUTE_INT.test(segments[1])) {
    return false;
  }
  const id = Number(segments[1]);
  return id >= -2_147_483_648 && id <= 2_147_483_647;
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
 * 401 rule: a 401 from one of the three bookings endpoints, `GET /api/bookings`
 * (the list), `POST /api/bookings` ("Book") and `DELETE /api/bookings/{id}`
 * ("Cancel"), recognised as the API routes them (see `isBookingsEndpoint`),
 * calls {@link AuthService.clearSession}, so the nav bar shows "Log in" at
 * once. That happens only when the request carried the token of the session
 * still in place. Any other 401, such as a failed login, another method or a
 * deeper path under `/api/bookings`, or a request sent without the token,
 * leaves the session in place. The interceptor never changes the route and
 * never swallows, transforms or retries an error: it rethrows the original
 * error object, and the page that sent the request decides between
 * redirecting to login and showing the error inline.
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
  const isBookings = isApi && isBookingsEndpoint(req.method, apiOriginPath.slice(config.apiPath.length));

  const session = auth.session();
  const carriesToken = isApi && session !== null;
  const request = carriesToken
    ? req.clone({ setHeaders: { Authorization: `Bearer ${session.token}` } })
    : req;

  return next(request).pipe(
    catchError((err: unknown) => {
      // Only the session whose token this request carried is discarded: a 401
      // for a request sent without the token says nothing about the session,
      // and a session stored by a login while the request was in flight holds
      // a token the server has not rejected.
      if (
        carriesToken &&
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
