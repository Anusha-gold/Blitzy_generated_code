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
 *   redirects a guest without a live session to `/login?returnUrl=…`.
 * - `**`: every unknown URL redirects to `search`.
 *
 * Query parameters are not declared here: each page reads its own through
 * `ActivatedRoute`.
 *
 * Only the search page, the entry screen, is bundled eagerly. The other four
 * pages are loaded through `loadComponent` on their first navigation. The
 * router runs `authGuard` before it activates the bookings route, so a guest
 * without a live session never sees that page. The guard does not keep the
 * bookings page's code from loading: the hotel detail page imports its
 * confirmation handoff helper, `bookingConfirmationState`, from the bookings
 * page module, so that module also loads with the hotel detail page.
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
    title: 'My bookings · SmartHotel',
  },
  { path: '**', redirectTo: 'search' },
];
