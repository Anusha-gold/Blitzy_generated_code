import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, combineLatest, finalize, of, switchMap, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { Booking, CreateBookingRequest, HotelDetail, RoomTypeAvailability, StayQuery } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * The hotel detail screen, route `hotels/:id` with the query parameters
 * `checkIn`, `checkOut` and `guests`, reached through "View rooms" on a search
 * result. It shows the hotel, its room types with their nightly price, the
 * count of rooms free for the stay and the server-computed stay total, and it
 * books a room type through "Book".
 *
 * Loading: every change of the route's `id` or of its query sends
 * `GET /api/hotels/{id}` for that stay. `guests` defaults to 1 when the query
 * omits it, as on the server; every other value is passed on unchecked,
 * because the server is the only authority on the stay rules. A newer load
 * cancels the one still in flight. While a load runs the page shows nothing of
 * its own, and the app-wide indicator shows progress. A failed load, such as
 * the 400 for invalid dates or the 404 for an unknown hotel, is shown in place
 * of the content.
 *
 * Booking: "Book" while signed out makes no request and navigates to
 * `/login?returnUrl=<current URL>`, so the guest comes back here after logging
 * in. Signed in, it sends `POST /api/bookings` built from the stay the server
 * echoed in the loaded detail, not from the raw query: those are exactly the
 * dates and party size that the displayed availability and stay total were
 * computed for, so the booking matches what the guest saw. The request carries
 * no price; the server computes the total. The booking is confirmed on
 * creation, with no payment step, and a 201 navigates to `/bookings` with the
 * new booking in the router state, where `BookingsPageComponent` shows the
 * confirmation. Any failure, status 0 included, is shown above the room table.
 *
 * A 401 from "Book" (a token the server rejects before its expiry) follows the
 * inline branch of the app's 401 rule: `authInterceptor` has already cleared
 * the session, so the nav bar switches to "Log in" by itself, and this page
 * stays on `/hotels/:id` and shows the 401 inline with a "Log in again" link
 * back to this URL. It never redirects. The redirect branch belongs to
 * `BookingsPageComponent`, whose entry `GET /api/bookings` is a protected-page
 * request rather than an action.
 */
@Component({
  selector: 'app-hotel-detail-page',
  standalone: true,
  imports: [NgIf, NgFor, RouterLink, CurrencyPipe, ErrorMessageComponent],
  templateUrl: './hotel-detail-page.component.html',
  styleUrl: './hotel-detail-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HotelDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly hotels = inject(HotelsApiService);
  private readonly bookings = inject(BookingsApiService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  /** Read by the template, whose "Log in again" link returns to `router.url`. */
  protected readonly router = inject(Router);

  /** The loaded hotel for the current stay; `null` while loading or after a failed load. */
  protected readonly hotel = signal<HotelDetail | null>(null);

  /** The failed load's error, shown in place of the content; `null` otherwise. */
  protected readonly loadError = signal<UiError | null>(null);

  /** The failed booking's error, shown above the room table; `null` otherwise. */
  protected readonly bookError = signal<UiError | null>(null);

  /** `true` while this page's booking request is in flight; disables every "Book". */
  protected readonly bookingPending = signal(false);

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(
        switchMap(([params, query]) =>
          this.load(Number(params.get('id')), {
            checkIn: query.get('checkIn') ?? '',
            checkOut: query.get('checkOut') ?? '',
            guests: query.has('guests') ? Number(query.get('guests')) : 1,
          }),
        ),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /**
   * Books one room of `rt` for the loaded stay, or sends a signed-out guest to
   * the login page with this URL as the return URL. Does nothing before the
   * hotel has loaded or while a booking request is already in flight, so a
   * repeated click can never book a second room.
   *
   * @param rt - The room type whose "Book" button was clicked.
   */
  protected book(rt: RoomTypeAvailability): void {
    const hotel = this.hotel();
    if (hotel === null || this.bookingPending()) {
      return;
    }

    if (!this.auth.isAuthenticated()) {
      void this.router.navigateByUrl('/login?returnUrl=' + encodeURIComponent(this.router.url));
      return;
    }

    this.bookError.set(null);
    this.bookingPending.set(true);
    const request: CreateBookingRequest = {
      hotelId: hotel.id,
      roomTypeId: rt.id,
      checkIn: hotel.checkIn,
      checkOut: hotel.checkOut,
      guests: hotel.guests,
    };

    this.bookings
      .create(request)
      .pipe(
        finalize(() => this.bookingPending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (booking: Booking) => {
          void this.router.navigate(['/bookings'], { state: { booking } });
        },
        error: (err: HttpErrorResponse) => this.bookError.set(toUiError(err)),
      });
  }

  /** Identifies a room type row by its id for `NgFor`. */
  protected trackById(_index: number, rt: RoomTypeAvailability): number {
    return rt.id;
  }

  /**
   * Loads one hotel for one stay, after clearing the previous outcome. The
   * error handling sits inside this inner observable, so a failed load never
   * ends the outer route stream and the next route change loads again.
   */
  private load(id: number, stay: StayQuery): Observable<HotelDetail | null> {
    this.hotel.set(null);
    this.loadError.set(null);
    this.bookError.set(null);

    return this.hotels.getHotel(id, stay).pipe(
      tap((detail) => this.hotel.set(detail)),
      catchError((err: HttpErrorResponse) => {
        this.loadError.set(toUiError(err));
        return of(null);
      }),
    );
  }
}
