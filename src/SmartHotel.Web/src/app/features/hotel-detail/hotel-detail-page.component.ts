import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, DOCUMENT, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, distinctUntilChanged, finalize, map, merge, of, switchMap, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { Booking, CreateBookingRequest, HotelDetail, RoomTypeAvailability, StayQuery } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';
import { bookingConfirmationState } from '../bookings/bookings-page.component';

/**
 * The hotel detail screen, route `hotels/:id` with the query parameters
 * `checkIn`, `checkOut` and `guests`, reached through "View rooms" on a search
 * result. It shows the hotel, its room types with their nightly price, the
 * count of rooms free for the stay and the server-computed stay total, and it
 * books a room type through "Book".
 *
 * Loading: the route's `id` and its query are read together, once per
 * committed navigation, and each navigation that changes the `id`, `checkIn`,
 * `checkOut` or `guests` sends one `GET /api/hotels/{id}` for that stay; one
 * that changes none of them does not reload. The `id` and every query value
 * are passed on exactly as the URL holds them, and `guests` is sent as 1 when
 * the query omits it, as on the server, because the server is the only
 * authority on the stay rules; `HotelsApiService.getHotel` answers an id that
 * is not an integer literal with the API's own 404, sending nothing. A newer
 * load cancels the one still in flight. While a load runs the page shows
 * nothing of its own, and the app-wide indicator shows progress. A failed
 * load, such as the 400 for invalid dates or a malformed `guests` or the 404
 * for an unknown or non-integer hotel id, is shown in place of the content.
 *
 * Booking: "Book" while signed out makes no request and navigates to
 * `/login?returnUrl=<current URL>`, so the guest comes back here after logging
 * in. Signed in, it sends `POST /api/bookings` built from the stay the server
 * echoed in the loaded detail, not from the raw query: those are exactly the
 * dates and party size that the displayed availability and stay total were
 * computed for, so the booking matches what the guest saw. The request carries
 * no price; the server computes the total. The booking is confirmed on
 * creation, with no payment step, and a 201 navigates to `/bookings` with the
 * new booking in the router state ({@link bookingConfirmationState}), held
 * only in memory and never written to the browser's history;
 * `BookingsPageComponent` confirms it from its own list of the caller's
 * bookings. Any failure, status 0 included, is shown above the room table.
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

  /** Tells which element has focus, when "Book" sends and once it fails. */
  private readonly document = inject(DOCUMENT);

  /** Registers the focus restore after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /** The "Log in again" link, rendered only while a 401 from "Book" is shown. */
  @ViewChild('loginAgain') private loginAgain?: ElementRef<HTMLAnchorElement>;

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
    merge(this.route.paramMap, this.route.queryParamMap)
      .pipe(
        // Either stream only signals a committed navigation. The router assigns
        // the route's new snapshot before it emits the query and then the path
        // parameters, so reading both from the snapshot never pairs an old id
        // with a new stay. The texts go out as the URL holds them; an absent
        // `guests` is sent as 1, the server's own default.
        map(() => {
          const { paramMap, queryParamMap } = this.route.snapshot;
          return {
            id: paramMap.get('id') ?? '',
            stay: {
              checkIn: queryParamMap.get('checkIn') ?? '',
              checkOut: queryParamMap.get('checkOut') ?? '',
              guests: queryParamMap.get('guests') ?? '1',
            },
          };
        }),
        // Entry emits once per stream, a navigation changing both id and query
        // emits twice, and an unrelated query parameter changes no criterion:
        // each of those loads at most once.
        distinctUntilChanged(
          (a, b) =>
            a.id === b.id &&
            a.stay.checkIn === b.stay.checkIn &&
            a.stay.checkOut === b.stay.checkOut &&
            a.stay.guests === b.stay.guests,
        ),
        switchMap(({ id, stay }) => this.load(id, stay)),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /**
   * Books one room of `rt` for the loaded stay, or sends a signed-out guest to
   * the login page with this URL as the return URL. Does nothing before the
   * hotel has loaded or while a booking request is already in flight, so a
   * repeated click can never book a second room. The element focused when the
   * request is sent, normally this "Book" button, is recorded so that a failed
   * booking can move focus to "Log in again" after a 401, or back to it
   * otherwise.
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

    const focused = this.document.activeElement;
    const trigger = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;
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
          void this.router.navigate(['/bookings'], { state: bookingConfirmationState(booking) });
        },
        error: (err: HttpErrorResponse) => {
          const uiError = toUiError(err);
          this.bookError.set(uiError);
          this.restoreFocusAfterBooking(trigger, uiError.status === 401);
        },
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
  private load(id: string, stay: StayQuery): Observable<HotelDetail | null> {
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

  /**
   * Moves focus once a failed booking has rendered its error and re-enabled
   * the "Book" buttons: to the "Log in again" link after a 401, otherwise, or
   * when that link is missing, back to `trigger`. Disabling the focused "Book"
   * button while the request is pending can drop focus to the document body,
   * so focus moves only when it is still on the body, on nothing, or on
   * `trigger`, and only to an element that is still in the document and
   * enabled; anything else the guest focused meanwhile keeps focus. A
   * cancelled request never calls this, and a 201 navigates away instead.
   *
   * @param trigger - The element focused when the request was sent, or `null`.
   * @param unauthorized - Whether the booking failed with 401.
   */
  private restoreFocusAfterBooking(trigger: HTMLElement | null, unauthorized: boolean): void {
    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active !== null && active !== this.document.body && active !== trigger) {
          return;
        }

        const loginAgain = unauthorized ? this.loginAgain?.nativeElement : undefined;
        const target = loginAgain?.isConnected ? loginAgain : trigger;
        if (target !== null && target.isConnected && !target.matches(':disabled')) {
          target.focus();
        }
      },
      { injector: this.injector },
    );
  }
}
