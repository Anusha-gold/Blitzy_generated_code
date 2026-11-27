import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { loadingInterceptor } from './core/http/loading.interceptor';

/**
 * The application-wide providers that `main.ts` passes to
 * `bootstrapApplication(AppComponent, …)`.
 *
 * - Zone.js change detection, with event coalescing. `zone.js` is the
 *   polyfill configured in `angular.json`.
 * - The router with {@link routes}, using path-based URLs, which the hosting
 *   nginx serves through its `index.html` fallback.
 * - `HttpClient` on its default XHR backend, with two functional
 *   interceptors in this order:
 *   1. {@link loadingInterceptor} is outermost, so the loading counter spans
 *      the whole request, including the auth interceptor's work and its 401
 *      handling.
 *   2. {@link authInterceptor} runs next, closest to the backend. It attaches
 *      the bearer token to API requests only and clears the session when a
 *      bookings endpoint answers 401.
 *
 * `API_CONFIG` is deliberately absent: its value exists only once
 * `loadRuntimeConfig()` has resolved, so `main.ts` appends that provider to
 * these when it bootstraps.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([loadingInterceptor, authInterceptor])),
  ],
};
