import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { SearchPageComponent } from './features/search/search-page.component';

/**
 * The application's route table, passed to `provideRouter` in `app.config.ts`.
 *
 * - `''` redirects to `search`, the entry screen.
 * - `search`: hotel search; reads `city`, `checkIn`, `checkOut` and `guests`
 *   from the query.
 * - `hotels/:id`: hotel detail and booking; reads `checkIn`, `checkOut` and
 *   `guests` from the query.
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
 * pages are loaded through `loadComponent` on their first navigation, each as
 * its own chunk. The router runs `authGuard` before it fetches the bookings
 * chunk, so a guest without a live session never downloads that page.
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
