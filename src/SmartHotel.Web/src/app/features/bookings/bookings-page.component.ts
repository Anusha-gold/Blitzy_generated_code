import { ChangeDetectionStrategy, ChangeDetectorRef, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, Location, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { finalize, switchMap } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import type { Booking } from '../../core/api/models';
import { type UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * Bookings per page of the table. The page renders one window of this many
 * rows, so a long booking history never becomes one unbounded DOM table.
 */
const PAGE_SIZE = 10;

/**
 * The "My bookings" page, route `bookings`, behind `authGuard`. It lists the
 * caller's own bookings in both statuses, in the server's order (newest
 * `createdAt` first, then highest id), and lets the guest cancel a future one.
 *
 * Paging: the table shows one window of {@link PAGE_SIZE} bookings, and
 * "Previous" and "Next" reach every other one. The window opens on the first
 * page, which holds the newest booking, and it keeps its page across the
 * reload after a cancel, so the cancelled row stays in view.
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
  private readonly changeDetector = inject(ChangeDetectorRef);

  /**
   * The caller's bookings in server order; `null` until the first load
   * succeeds, and after an entry load that failed.
   */
  protected readonly bookings = signal<Booking[] | null>(null);

  /**
   * The zero-based page the guest last asked for. A reload does not reset it;
   * {@link page} clamps it to the pages the current list has.
   */
  private readonly requestedPage = signal(0);

  /** How many pages the list fills; at least 1, so page 0 always exists. */
  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil((this.bookings()?.length ?? 0) / PAGE_SIZE)),
  );

  /**
   * The zero-based page on screen: the requested page, clamped to the last
   * page, so a shorter reload never shows an empty page.
   */
  protected readonly page = computed(() => Math.min(this.requestedPage(), this.pageCount() - 1));

  /** The bookings on the current page, in server order. */
  protected readonly visibleBookings = computed(() => {
    const start = this.page() * PAGE_SIZE;
    return (this.bookings() ?? []).slice(start, start + PAGE_SIZE);
  });

  /**
   * The pager label's numbers: the one-based positions of the first and last
   * visible bookings, and the total. `first` and `last` are 0 for an empty list.
   */
  protected readonly range = computed(() => {
    const total = this.bookings()?.length ?? 0;
    const start = this.page() * PAGE_SIZE;
    return {
      first: total === 0 ? 0 : start + 1,
      last: Math.min(start + PAGE_SIZE, total),
      total,
    };
  });

  /** The inline error of the last load or cancel, or `null` when there is none. */
  protected readonly error = signal<UiError | null>(null);

  /**
   * Whether a cancel, or the list reload that follows it, is in flight;
   * disables every "Cancel" button.
   */
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
    this.loadOnEntry();
  }

  /**
   * Cancels a booking. A 204 reloads the list, so the row's new status comes
   * from the server. Every error of the cancel or of that reload, 401
   * included, is shown inline and the page stays where it is.
   *
   * The reload runs inside the pending window: "Cancel" stays disabled until
   * the row shows the server's new status, so a second click can never send a
   * DELETE for a booking that is already cancelled. While a cancel, or its
   * reload, is in flight the method returns without a request, because the
   * disabled binding reaches the DOM only on the next change-detection pass
   * and a click landing before it still calls here. The subscription is bound
   * to the stored `DestroyRef`, because this runs from an event callback,
   * outside an injection context.
   */
  protected cancel(b: Booking): void {
    if (this.cancelPending()) {
      return;
    }

    this.error.set(null);
    this.cancelPending.set(true);

    this.api
      .cancel(b.id)
      .pipe(
        switchMap(() => this.api.list()),
        finalize(() => this.cancelPending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (list) => this.bookings.set(list),
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
   * Shows the previous page. On the first page the click is ignored: the
   * button is only `aria-disabled` there, so it keeps keyboard focus.
   *
   * @param control - The "Previous" button that was pressed; it is kept in
   *   view after the page changes (see {@link showPage}).
   */
  protected previousPage(control: HTMLElement): void {
    const page = this.page();
    if (page > 0) {
      this.showPage(page - 1, control);
    }
  }

  /**
   * Shows the next page. On the last page the click is ignored: the button is
   * only `aria-disabled` there, so it keeps keyboard focus.
   *
   * @param control - The "Next" button that was pressed; it is kept in view
   *   after the page changes (see {@link showPage}).
   */
  protected nextPage(control: HTMLElement): void {
    const page = this.page();
    if (page < this.pageCount() - 1) {
      this.showPage(page + 1, control);
    }
  }

  /**
   * Switches the table to `page` and keeps the pressed pager button in view.
   *
   * The pager sits below the rows, so a page with a different row count moves
   * it: a short last page to a full one pushes it down, and a full page to a
   * short one pulls it up. The button keeps keyboard focus, but the browser
   * does not scroll a focused element back into view when layout moves it, so
   * its focus ring could end up outside the viewport. The new rows are
   * therefore rendered synchronously, and the button is then scrolled the
   * least distance that brings it fully into view. Focus stays where it is,
   * and the table region's own horizontal scroll is untouched, because the
   * pager is not inside that region.
   *
   * @param page - The zero-based page to show; the callers keep it in range.
   * @param control - The pager button that was pressed.
   */
  private showPage(page: number, control: HTMLElement): void {
    this.requestedPage.set(page);
    this.changeDetector.detectChanges();
    control.scrollIntoView({ block: 'nearest' });
  }

  /**
   * Loads the caller's bookings when the page opens. This protected-page
   * request is the only one whose 401 redirects to the login page; any other
   * error is shown inline.
   */
  private loadOnEntry(): void {
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => this.bookings.set(list),
        error: (err: HttpErrorResponse) => {
          if (err.status === 401) {
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
