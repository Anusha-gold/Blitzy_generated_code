import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { LoginPageComponent } from './features/auth/login-page.component';
import { RegisterPageComponent } from './features/auth/register-page.component';
import { BookingsPageComponent } from './features/bookings/bookings-page.component';
import { HotelDetailPageComponent } from './features/hotel-detail/hotel-detail-page.component';
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
 * `ActivatedRoute`. Components are referenced eagerly, so the app ships as
 * one bundle.
 */
export const routes: Routes = [
  { path: '', redirectTo: 'search', pathMatch: 'full' },
  { path: 'search', component: SearchPageComponent, title: 'Search · SmartHotel' },
  { path: 'hotels/:id', component: HotelDetailPageComponent, title: 'Hotel · SmartHotel' },
  { path: 'login', component: LoginPageComponent, title: 'Log in · SmartHotel' },
  { path: 'register', component: RegisterPageComponent, title: 'Register · SmartHotel' },
  {
    path: 'bookings',
    component: BookingsPageComponent,
    canActivate: [authGuard],
    title: 'My bookings · SmartHotel',
  },
  { path: '**', redirectTo: 'search' },
];
