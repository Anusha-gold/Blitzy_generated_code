import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { DATE_PIPE_DEFAULT_OPTIONS, type DatePipeConfig } from '@angular/common';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withRouterConfig } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { loadingInterceptor } from './core/http/loading.interceptor';

/**
 * The `DatePipe` pattern used when the browser's numeric date order cannot be
 * read: the order an `en-US` browser shows.
 */
const FALLBACK_DATE_FORMAT = 'MM/dd/yyyy';

/**
 * Reads the browser's own numeric date order, the regional order its native
 * `<input type="date">` controls show, as an Angular `formatDate` pattern.
 *
 * The browser's default locale formats a fixed sample date with a two-digit
 * day and month and a numeric year in the Gregorian calendar, the calendar of
 * native date inputs and of `DatePipe`. Only the type of each part is read, so
 * the sample's value and the clock do not matter. Each part maps to a pattern
 * field: `day` to `dd`, `month` to `MM` and `year` to `yyyy`. Each `literal`
 * part (a separator) is emitted quoted, with any `'` doubled, so a letter in a
 * locale's separator is never read as a pattern field.
 *
 * @returns The browser's pattern with its separators quoted, such as
 *   `MM'/'dd'/'yyyy` (`en-US`), `dd'/'MM'/'yyyy` (`en-GB`),
 *   `dd'.'MM'.'yyyy` (`de-DE`) or `yyyy'/'MM'/'dd` (`ja-JP`);
 *   {@link FALLBACK_DATE_FORMAT} when
 *   `Intl` throws, or when the parts other than separators are not exactly
 *   one day, one month and one year.
 */
function browserDateFormat(): string {
  let parts: Intl.DateTimeFormatPart[];
  try {
    parts = new Intl.DateTimeFormat(undefined, {
      calendar: 'gregory',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(new Date(2000, 11, 31));
  } catch {
    return FALLBACK_DATE_FORMAT;
  }
  const fields: Partial<Record<Intl.DateTimeFormatPartTypes, string>> = {
    day: 'dd',
    month: 'MM',
    year: 'yyyy',
  };
  const seen = new Set<Intl.DateTimeFormatPartTypes>();
  let pattern = '';
  for (const { type, value } of parts) {
    if (type === 'literal') {
      // An empty quoted literal ('') would mean one single quote to
      // formatDate, so an empty separator adds nothing.
      if (value !== '') {
        pattern += `'${value.replaceAll("'", "''")}'`;
      }
      continue;
    }
    const field = fields[type];
    if (field === undefined || seen.has(type)) {
      return FALLBACK_DATE_FORMAT;
    }
    seen.add(type);
    pattern += field;
  }
  return seen.size === 3 ? pattern : FALLBACK_DATE_FORMAT;
}

/**
 * The application-wide providers that `main.ts` passes to
 * `bootstrapApplication(AppComponent, …)`.
 *
 * - Zone.js change detection, with event coalescing. `zone.js` is the
 *   polyfill configured in `angular.json`.
 * - The router with {@link routes}, using path-based URLs, which the hosting
 *   nginx serves through its `index.html` fallback. A navigation to the
 *   current URL is processed (`onSameUrlNavigation: 'reload'`) rather than
 *   ignored, so nav "My bookings" on `/bookings` re-runs `authGuard` and
 *   refreshes the list. The search page's query observable does not emit for
 *   an unchanged query, so it re-runs a search only through its own rerun.
 *   A Back or Forward step that a guard refuses, as the hotel detail page
 *   refuses one while it settles a booking in flight, moves the browser back
 *   to the history entry it left (`canceledNavigationResolution: 'computed'`),
 *   so the entry the guest stepped to stays in history rather than being
 *   overwritten.
 * - `HttpClient` on its default XHR backend, with two functional
 *   interceptors in this order:
 *   1. {@link loadingInterceptor} is outermost, so the loading counter spans
 *      the whole request, including the auth interceptor's work and its 401
 *      handling.
 *   2. {@link authInterceptor} runs next, closest to the backend. It attaches
 *      the bearer token to API requests only and clears the session when a
 *      bookings endpoint answers 401.
 * - The app's one display format for stay dates, as the default of every
 *   `DatePipe`: the browser's own numeric date order and separators, read by
 *   {@link browserDateFormat} (`12/27/2026` in an `en-US` browser,
 *   `27/12/2026` in `en-GB`, `27.12.2026` in `de-DE`). The search page's
 *   native date inputs show dates in the browser's regional order, so every
 *   displayed date follows that order too, and a stay reads the same on every
 *   screen. Only the order and separators of the digits follow the browser:
 *   the text stays English on Angular's default `en-US` locale, which needs no
 *   other locale data for numeric fields, and `MM/dd/yyyy` stands in when the
 *   browser's order cannot be read. Only the display changes: dates stay
 *   exact `yyyy-MM-dd` strings in URLs, request bodies, router state and
 *   comparisons. No time zone is set on purpose: the pipe reads a
 *   `yyyy-MM-dd` string as local midnight of that day, so formatting in local
 *   time keeps the calendar day in every zone, whereas a fixed zone such as
 *   UTC would show the previous day east of UTC.
 *
 * `API_CONFIG` is deliberately absent: its value exists only once
 * `loadRuntimeConfig()` has resolved, so `main.ts` appends that provider to
 * these when it bootstraps.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(
      routes,
      withRouterConfig({ onSameUrlNavigation: 'reload', canceledNavigationResolution: 'computed' }),
    ),
    provideHttpClient(withInterceptors([loadingInterceptor, authInterceptor])),
    { provide: DATE_PIPE_DEFAULT_OPTIONS, useFactory: (): DatePipeConfig => ({ dateFormat: browserDateFormat() }) },
  ],
};
