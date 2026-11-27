import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, Location, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import type { Booking } from '../../core/api/models';
import { type UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * The "My bookings" page, route `bookings`, behind `authGuard`. It lists the
 * caller's own bookings in both statuses, in the server's order (newest
 * `createdAt` first, then highest id), and lets the guest cancel a future one.
 *
 * Confirmation banner: after a 201, `HotelDetailPageComponent` navigates here
 * with the server's booking in the one-shot router state `booking`. The page
 * reads that state once, at construction, and shows the banner only when the
 * value passes {@link isBooking}; absent or malformed state shows no banner
 * and no error. A booking is confirmed on creation and no payment is taken
 * online, which the banner states.
 *
 * 401 handling, split with `authInterceptor`: the interceptor clears the
 * session when a `/api/bookings` request is rejected with 401 and never
 * navigates; this page decides what follows.
 * - The entry `GET /api/bookings` redirects to
 *   `/login?returnUrl=%2Fbookings&reason=session-expired`, where the login
 *   page shows its session-expired notice.
 * - "Cancel", and the list reload that follows it, stay on the page and show
 *   the 401 inline with a "Log in again" link.
 * - "Book" on `HotelDetailPageComponent` owns its own inline branch.
 *
 * Every other error renders inline through `ErrorMessageComponent`; nothing
 * is swallowed.
 *
 * Cancel boundary: "Cancel" is offered on `Confirmed` bookings whose check-in
 * is not earlier than the current UTC calendar date, the boundary the API
 * applies (a check-in before today, UTC, gives 409). The check is display-only;
 * the server stays the authority and its 409 still shows inline.
 */
@Component({
  selector: 'app-bookings-page',
  standalone: true,
  imports: [NgIf, NgFor, RouterLink, CurrencyPipe, ErrorMessageComponent],
  templateUrl: './bookings-page.component.html',
  styleUrl: './bookings-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BookingsPageComponent {
  private readonly api = inject(BookingsApiService);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The caller's bookings in server order; `null` until the first load
   * succeeds, and after an entry load that failed.
   */
  protected readonly bookings = signal<Booking[] | null>(null);

  /** The inline error of the last load or cancel, or `null` when there is none. */
  protected readonly error = signal<UiError | null>(null);

  /** Whether a cancel request is in flight; disables every "Cancel" button. */
  protected readonly cancelPending = signal(false);

  /**
   * The current UTC calendar date as `yyyy-MM-dd`. It is derived from the ISO
   * string, never from local-time date methods, so it matches the API's UTC
   * boundary in every time zone.
   */
  protected readonly today = new Date().toISOString().slice(0, 10);

  /**
   * The booking just created, from the navigation's router state, or `null`
   * when the page was reached any other way. Read once; it never changes.
   */
  protected readonly confirmation: Booking | null = readConfirmation(this.location.getState());

  constructor() {
    this.load(true);
  }

  /**
   * Cancels a booking. A 204 reloads the list, so the row's new status comes
   * from the server. Every error, 401 included, is shown inline and the page
   * stays where it is.
   */
  protected cancel(b: Booking): void {
    this.error.set(null);
    this.cancelPending.set(true);

    this.api
      .cancel(b.id)
      .pipe(
        finalize(() => this.cancelPending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => this.load(false),
        error: (err: HttpErrorResponse) => this.error.set(toUiError(err)),
      });
  }

  /**
   * Whether "Cancel" is offered: the booking is `Confirmed` and its check-in is
   * today (UTC) or later. Exact `yyyy-MM-dd` strings order correctly as text.
   */
  protected canCancel(b: Booking): boolean {
    return b.status === 'Confirmed' && b.checkIn >= this.today;
  }

  /** Identifies a booking row by its id for `NgFor`. */
  protected trackById(_index: number, b: Booking): number {
    return b.id;
  }

  /**
   * Loads the caller's bookings. `entry` marks the request sent when the page
   * opens: only its 401 redirects to the login page. A reload after a cancel
   * runs from an event callback, outside an injection context, so the
   * subscription is bound to the stored `DestroyRef`.
   */
  private load(entry: boolean): void {
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => this.bookings.set(list),
        error: (err: HttpErrorResponse) => {
          if (entry && err.status === 401) {
            // `replaceUrl` drops the rejected `/bookings` entry from history,
            // so Back does not return to a page that bounces to login again.
            void this.router.navigate(['/login'], {
              queryParams: { returnUrl: '/bookings', reason: 'session-expired' },
              replaceUrl: true,
            });
            return;
          }
          this.error.set(toUiError(err));
        },
      });
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/**
 * Whether router state holds a complete booking as the API returns it. Every
 * member of {@link Booking} is checked, because the state is untyped input:
 * a stale or foreign history entry must not render a partial banner.
 */
function isBooking(value: unknown): value is Booking {
  if (!isRecord(value)) {
    return false;
  }
  const status = value['status'];
  return (
    typeof value['id'] === 'number' &&
    typeof value['hotelId'] === 'number' &&
    typeof value['hotelName'] === 'string' &&
    typeof value['roomTypeId'] === 'number' &&
    typeof value['roomTypeName'] === 'string' &&
    typeof value['roomNumber'] === 'string' &&
    typeof value['checkIn'] === 'string' &&
    typeof value['checkOut'] === 'string' &&
    typeof value['nights'] === 'number' &&
    typeof value['guests'] === 'number' &&
    typeof value['totalPrice'] === 'number' &&
    typeof value['createdAt'] === 'string' &&
    (status === 'Confirmed' || status === 'Cancelled')
  );
}

/**
 * Returns the `booking` member of the history state when it is a valid
 * {@link Booking}, and `null` otherwise. Other members, such as Angular's
 * `navigationId`, are ignored.
 */
function readConfirmation(state: unknown): Booking | null {
  if (!isRecord(state)) {
    return null;
  }
  const booking = state['booking'];
  return isBooking(booking) ? booking : null;
}
