import { Routes } from '@angular/router';
import type { RouterStateSnapshot } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
// Type-only, so the hotel detail page stays out of the eager bundle.
import type { HotelDetailPageComponent } from './features/hotel-detail/hotel-detail-page.component';
import { SearchPageComponent } from './features/search/search-page.component';

/**
 * The application's route table, passed to `provideRouter` in `app.config.ts`.
 *
 * - `''` redirects to `search`, the entry screen.
 * - `search`: hotel search; reads `city`, `checkIn`, `checkOut` and `guests`
 *   from the query.
 * - `hotels/:id`: hotel detail and booking; reads `checkIn`, `checkOut` and
 *   `guests` from the query. It has no access guard. Its `canDeactivate`
 *   calls `HotelDetailPageComponent.canLeave`, which holds every navigation
 *   to another route, whatever the session state, while the page's booking
 *   request is in flight, until the page has settled the answer, and then
 *   refuses it; a 201 for the stay on screen goes to My bookings instead
 *   while the guest has a live session, and is shown on the page otherwise.
 *   The hold keeps a booking's outcome in view; it is not access protection.
 * - `login`: reads `returnUrl`, `registered` and `reason` from the query.
 * - `register`: guest registration.
 * - `bookings`: the guest's own bookings, guarded by `authGuard`, which
 *   redirects a guest without a live session to `/login?returnUrl=…`. The
 *   guard runs on every navigation to it (`runGuardsAndResolvers: 'always'`),
 *   a same-URL one included, so nav "My bookings" clicked on the open page
 *   after the session ended redirects to login, and with a live session the
 *   page reloads its list.
 * - `**`: every unknown URL redirects to `search`. No route has a named
 *   outlet, so the app's URL serializer reads parentheses in a path as path
 *   data, and `/search(x)` is an unknown URL too.
 *
 * Query parameters are not declared here: each page reads its own through
 * `ActivatedRoute`.
 *
 * Search, the entry screen, is eager; the other routes use `loadComponent`.
 * Hotel detail may also load the bookings page module, for its confirmation
 * helper `bookingConfirmationState`. `authGuard` controls activation of the
 * bookings route, not the download of its code.
 */
export const routes: Routes = [
  { path: '', redirectTo: 'search', pathMatch: 'full' },
  { path: 'search', component: SearchPageComponent, title: 'Search · SmartHotel' },
  {
    path: 'hotels/:id',
    loadComponent: () =>
      import('./features/hotel-detail/hotel-detail-page.component').then(
        (m) => m.HotelDetailPageComponent,
      ),
    canDeactivate: [
      (page: HotelDetailPageComponent, _route: unknown, _state: unknown, next: RouterStateSnapshot) =>
        page.canLeave(next),
    ],
    title: 'Hotel · SmartHotel',
  },
  {
    path: 'login',
    loadComponent: () =>
      import('./features/auth/login-page.component').then((m) => m.LoginPageComponent),
    title: 'Log in · SmartHotel',
  },
  {
    path: 'register',
    loadComponent: () =>
      import('./features/auth/register-page.component').then((m) => m.RegisterPageComponent),
    title: 'Register · SmartHotel',
  },
  {
    path: 'bookings',
    loadComponent: () =>
      import('./features/bookings/bookings-page.component').then((m) => m.BookingsPageComponent),
    canActivate: [authGuard],
    runGuardsAndResolvers: 'always',
    title: 'My bookings · SmartHotel',
  },
  { path: '**', redirectTo: 'search' },
];
