import {
  type AfterViewInit,
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  NgZone,
  ViewChild,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { CurrencyPipe, DOCUMENT, Location, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { finalize, skip, switchMap, tap } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import type { Booking } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { type UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * Bookings per page of the table. The page renders one window of this many
 * rows, so a long booking history never becomes one unbounded DOM table.
 */
const PAGE_SIZE = 10;

/**
 * The router state `HotelDetailPageComponent` passes to `/bookings` after a
 * 201: the new booking, exactly as `POST /api/bookings` returned it. Create it
 * with {@link bookingConfirmationState}, never as an object literal, so the
 * booking stays in the router's memory and out of the browser's history.
 *
 * A type alias rather than an interface: an interface has no implicit index
 * signature, so it would not be assignable to the router's `state` extra.
 */
export type BookingConfirmationState = { readonly booking: Booking };

/**
 * Creates the router state that hands a new booking to `/bookings`.
 *
 * The router copies a navigation's state into the browser's history entry,
 * which the browser structured-clones and keeps for the life of the tab, past
 * a logout and into a later guest's session. `booking` is therefore a
 * non-enumerable property: a structured clone, like the object spread with
 * which the router builds that entry, copies only own enumerable properties,
 * so no history entry ever holds the booking. The router passes the state
 * object itself, by reference, through its in-memory navigation, where
 * {@link BookingsPageComponent} reads `booking` while that navigation
 * constructs it.
 *
 * @param booking - The booking from the 201 of `POST /api/bookings`.
 * @returns The state for the `state` extra of `Router.navigate`.
 */
export function bookingConfirmationState(booking: Booking): BookingConfirmationState {
  const state = {} as BookingConfirmationState;
  Object.defineProperty(state, 'booking', { value: booking, enumerable: false });
  return state;
}

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
 * with the new booking in the router state, a
 * {@link BookingConfirmationState} held only in the router's memory and never
 * written to the browser's history. The page accepts it once, at
 * construction, from the router's in-memory navigation that is constructing
 * it, never from history state: the navigation must be a fresh in-app one,
 * made while a session exists, and its booking must carry a valid id. A page
 * load, Back or Forward replays history state that may belong to an earlier
 * guest's visit, so it never shows a banner. Whether or not it accepts one,
 * the page removes any `booking` member from the current history entry. Of
 * the handed-over booking the page keeps only its id; any change of the
 * session (a logout, the session cleared after a 401, a new login) ends the
 * handoff, and so does a 204 on that booking. The banner shows the booking's
 * row from the caller's own list, never the router state, while a session
 * exists and the row is `Confirmed`. Absent or invalid state shows no banner
 * and no error. A booking is confirmed on creation and no payment is taken
 * online, which the banner states. Once the banner renders it takes focus,
 * unless the guest has already focused something, so keyboard navigation
 * continues from the confirmation rather than from the top of the document.
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
 * applies (a check-in before today, UTC, gives 409). The date is kept current
 * while the page stays open, so a stay whose check-in has passed stops offering
 * "Cancel" without a reload. The check is display-only; the server stays the
 * authority and its 409 still shows inline.
 */
@Component({
  selector: 'app-bookings-page',
  standalone: true,
  imports: [NgIf, NgFor, RouterLink, CurrencyPipe, ErrorMessageComponent],
  templateUrl: './bookings-page.component.html',
  styleUrl: './bookings-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BookingsPageComponent implements AfterViewInit {
  private readonly api = inject(BookingsApiService);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly changeDetector = inject(ChangeDetectorRef);

  /**
   * The page's document: the resume listeners of the UTC date, and the focused
   * element the confirmation banner and the cancel focus handoff check.
   */
  private readonly document = inject(DOCUMENT);

  /** Registers the banner focus after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /** Runs the UTC-date timer and listeners outside change detection. */
  private readonly zone = inject(NgZone);

  /** This page's host element, the scope of every focus-target lookup. */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * The rendered confirmation banner, the focus destination after a booking;
   * `undefined` while no banner is shown.
   */
  @ViewChild('confirmationBanner') private confirmationBanner?: ElementRef<HTMLElement>;

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
   * The text of the page's polite live region (`role="status"`). It starts
   * empty, and the entry message waits until the region has been in the page
   * for {@link LIVE_REGION_DELAY_MS} (see {@link ngAfterViewInit}): the
   * confirmation, once the caller's list confirms the handed-over booking as
   * the banner does, or "You have no bookings yet." when the entry load finds
   * no bookings. Each 204 announces the cancelled booking. A cancel clears it
   * first, so a repeated message is announced again. Errors are announced by
   * the inline alert instead.
   */
  protected readonly announcement = signal('');

  /**
   * Whether the live region has been in the page for
   * {@link LIVE_REGION_DELAY_MS}, so that text written into it is announced.
   * Until then the entry load leaves its message to the live-region timer.
   */
  private readonly liveRegionReady = signal(false);

  /**
   * The timer that marks the live region ready and publishes the entry
   * message into {@link announcement}; `undefined` when none is pending.
   */
  private liveRegionTimer: ReturnType<typeof setTimeout> | undefined;

  /**
   * The current UTC calendar date as `yyyy-MM-dd`, from {@link utcToday}.
   * {@link keepTodayCurrent} refreshes it at each UTC midnight and whenever the
   * tab is shown or focused again, so an open page never keeps a past date.
   */
  protected readonly today = signal(utcToday());

  /**
   * The ids of the bookings that offer "Cancel": `Confirmed`, with a check-in
   * of {@link today} or later. Exact `yyyy-MM-dd` strings order correctly as
   * text. Recomputed only when the list or the UTC date changes, never per
   * change-detection pass; empty while no list is loaded. Display-only: the
   * server stays the authority and its 409 still shows inline.
   */
  protected readonly cancellableIds = computed<ReadonlySet<number>>(() => {
    const today = this.today();
    const ids = new Set<number>();
    for (const b of this.bookings() ?? []) {
      if (b.status === 'Confirmed' && b.checkIn >= today) {
        ids.add(b.id);
      }
    }
    return ids;
  });

  /**
   * The id of the booking whose confirmation handoff this page accepted at
   * construction, or `null` when the page was reached any other way. It is
   * the only trace of the handoff the page keeps, in memory. Any change of the
   * session, and a 204 on that booking, set it back to `null` for the life of
   * the page.
   */
  private readonly handoff = signal<number | null>(this.takeHandoff());

  /**
   * The booking the banner confirms, or `null` for no banner. It is the
   * handed-over booking's row in the caller's own list, so it appears once the
   * list has loaded, and only while a session exists and the row is
   * `Confirmed`. A cancelled booking, or one absent from the list, shows no
   * banner. The session check hides the banner in the same change-detection
   * pass that clears the session, before the handoff itself is ended.
   */
  protected readonly confirmation = computed<Booking | null>(() => {
    const bookingId = this.handoff();
    if (bookingId === null || this.auth.session() === null) {
      return null;
    }
    return this.bookings()?.find((b) => b.id === bookingId && b.status === 'Confirmed') ?? null;
  });

  constructor() {
    // Any change of the session ends the handoff: a logout, the session
    // cleared after a 401, or a new login. `skip(1)` passes over the session
    // current at construction, which the handoff was accepted under.
    toObservable(this.auth.session)
      .pipe(skip(1), takeUntilDestroyed())
      .subscribe(() => this.handoff.set(null));

    this.keepTodayCurrent();
    this.loadOnEntry();
  }

  /**
   * Publishes the entry message once the live region is in the page. Screen
   * readers miss text written into a live region in the same frame the region
   * is inserted, and the entry load can settle before the first render, so a
   * timer marks the region ready {@link LIVE_REGION_DELAY_MS} after the view
   * is initialised, the interval Angular CDK's `LiveAnnouncer` uses, and then
   * writes {@link entryAnnouncement} for the list loaded so far; when the list
   * loads later, {@link loadOnEntry} writes it. The timer is cleared when the
   * page is destroyed and when a cancel starts, so it never overwrites a
   * cancellation message.
   */
  ngAfterViewInit(): void {
    this.destroyRef.onDestroy(() => this.clearLiveRegionTimer());
    this.liveRegionTimer = setTimeout(() => {
      this.liveRegionTimer = undefined;
      this.liveRegionReady.set(true);
      this.publishEntryAnnouncement();
    }, LIVE_REGION_DELAY_MS);
  }

  /**
   * Cancels a booking. The 204 marks that row `Cancelled` at once, ends the
   * confirmation handoff when it is the handed-over booking, so its banner
   * goes even if the reload fails, and announces the cancellation through
   * {@link announcement}; the list is then reloaded and, when the reload
   * succeeds, replaced from the server. Every error of the cancel or of that
   * reload, 401 included, is shown inline and the page stays where it is.
   * After a reload error the row keeps the `Cancelled` status the 204
   * confirmed, so it no longer offers "Cancel". An entry message still waiting
   * to be published (see {@link ngAfterViewInit}) is dropped when the cancel
   * starts, so it never overwrites the cancellation's message.
   *
   * The pending flag blocks concurrent DELETEs: it is set before the request
   * and released only once the cancel and its reload have both settled. While
   * it is set every "Cancel" is disabled and this method returns without a
   * request, because the disabled binding reaches the DOM only on the next
   * change-detection pass and a click landing before it still calls here. The
   * flag does not track the server: a booking cancelled elsewhere offers
   * "Cancel" until the next load, and the server's 409 then shows inline. The
   * subscription is bound to the stored `DestroyRef`, because this runs from
   * an event callback, outside an injection context.
   *
   * Focus: the button is disabled at once and removed when the 204 marks the
   * row `Cancelled`, so when `trigger` holds focus as the guest activates it,
   * focus moves synchronously, before the disabled binding renders, to the
   * booking's status cell, and the cancel owns that focus. When `trigger` does
   * not hold focus (some browsers do not focus a clicked button) the cancel
   * owns nothing and never moves focus. Once the cancel and its reload have
   * settled, {@link settleCancel} moves focus on only while the cancel still
   * owns it.
   *
   * @param b - The booking to cancel.
   * @param trigger - The "Cancel" button the guest activated; focus is handed
   *   off from it only when it holds focus.
   */
  protected cancel(b: Booking, trigger: HTMLButtonElement): void {
    if (this.cancelPending()) {
      return;
    }

    this.clearLiveRegionTimer();
    this.error.set(null);
    this.announcement.set('');
    this.cancelPending.set(true);

    const owned = this.document.activeElement === trigger ? this.statusCell(b.id) : null;
    owned?.focus();

    this.api
      .cancel(b.id)
      .pipe(
        // The 204 already proves the booking cancelled, so its row stops
        // offering "Cancel" and its banner goes now, even if the reload below
        // fails.
        tap(() => {
          if (this.handoff() === b.id) {
            this.handoff.set(null);
          }
          this.bookings.update((list) =>
            list === null
              ? null
              : list.map((row): Booking => (row.id === b.id ? { ...row, status: 'Cancelled' } : row)),
          );
          this.announcement.set(
            `Booking cancelled: ${b.hotelName}, room ${b.roomNumber}, ${b.checkIn} to ${b.checkOut}.`,
          );
        }),
        switchMap(() => this.api.list()),
        finalize(() => this.cancelPending.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (list) => {
          this.bookings.set(list);
          this.settleCancel(b.id, owned, false);
        },
        error: (err: HttpErrorResponse) => {
          const uiError = toUiError(err);
          this.error.set(uiError);
          this.settleCancel(b.id, owned, uiError.status === 401);
        },
      });
  }

  /**
   * Ends a cancel whose request and reload have settled, successfully or not.
   * It releases the pending flag and renders the outcome at once. It then
   * moves focus only while the cancel still owns it: `owned`, the status cell
   * {@link cancel} focused, still has focus, or `owned` left the document with
   * its row and focus fell to the body or to nothing. Focus on the body alone
   * is never enough, because a guest who moved on and clicked blank space also
   * leaves it there; and with `owned` `null` focus is never moved.
   *
   * The target is "Log in again" after a 401, as on the hotel page, because
   * the session is gone and that link is the way on; otherwise the booking's
   * "Cancel" when it is shown and enabled again, so a failed cancel restores
   * focus to it; otherwise the booking's status cell, which keeps focus and
   * reads `Cancelled` after a 204; and the page heading when the row is no
   * longer shown. Targets are looked up inside this page only.
   *
   * It runs from the `next` and `error` callbacks, never on teardown: when the
   * page is destroyed mid-request, `takeUntilDestroyed` only completes the
   * stream, so nothing renders or moves focus on a destroyed view.
   *
   * @param id - The booking the cancel was for.
   * @param owned - The element the cancel moved focus to, or `null` when it
   *   took no focus.
   * @param unauthorized - Whether the cancel or its reload failed with 401.
   */
  private settleCancel(id: number, owned: HTMLElement | null, unauthorized: boolean): void {
    this.cancelPending.set(false);
    this.changeDetector.detectChanges();

    if (owned === null) {
      return;
    }
    const active = this.document.activeElement;
    const stillOwned =
      active === owned || (!owned.isConnected && (active === null || active === this.document.body));
    if (!stillOwned) {
      return;
    }
    const host = this.host.nativeElement;
    const loginAgain = unauthorized ? host.querySelector<HTMLElement>('[data-testid="login-again"]') : null;
    const target =
      loginAgain ??
      host.querySelector<HTMLElement>(`[data-testid="cancel-${id}"]:enabled`) ??
      this.statusCell(id) ??
      host.querySelector<HTMLElement>('h1');
    target?.focus();
  }

  /**
   * The status cell (`booking-status-{id}`, `tabindex="-1"`) of a booking on
   * this page, or `null` when its row is not shown.
   *
   * @param id - The booking whose status cell to find.
   */
  private statusCell(id: number): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(`[data-testid="booking-status-${id}"]`);
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
   * Moves focus to the confirmation banner after the render that first shows
   * it, which follows the entry load, because the banner confirms the
   * handed-over booking's row in the loaded list. The "Book" button that sent
   * the booking left the document with the hotel page, so focus is normally
   * on the document body; it moves only from the body or from nothing, so an
   * element the guest focused meanwhile keeps focus. Does nothing when the
   * entry load shows no banner.
   */
  private focusConfirmationAfterRender(): void {
    if (this.confirmation() === null) {
      return;
    }

    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active === null || active === this.document.body) {
          this.confirmationBanner?.nativeElement.focus();
        }
      },
      { injector: this.injector },
    );
  }

  /**
   * Loads the caller's bookings when the page opens. This protected-page
   * request is the only one whose 401 redirects to the login page; any other
   * error is shown inline.
   *
   * Once the live region is ready, the loaded list publishes the entry message
   * (see {@link publishEntryAnnouncement}); before that, the live-region timer
   * publishes it when it fires (see {@link ngAfterViewInit}). An error leaves
   * the announcement unchanged.
   */
  private loadOnEntry(): void {
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.bookings.set(list);
          if (this.liveRegionReady()) {
            this.publishEntryAnnouncement();
          }
          this.focusConfirmationAfterRender();
        },
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

  /**
   * Accepts the confirmation handoff of the navigation constructing this page,
   * after removing any `booking` member from the current history entry.
   *
   * The handoff is read from the router's in-memory navigation only. It counts
   * when that navigation is imperative and follows an earlier one in this app,
   * a session exists, and its state's `booking` carries a valid id. The
   * initial navigation of a page load is imperative too, but it and every Back
   * or Forward navigation replay history state, which may come from an earlier
   * guest's visit; neither has the in-app predecessor, or the trigger, that
   * this requires. Only the id is taken: the banner shows the booking from the
   * caller's own list.
   *
   * @returns The handed-over booking's id, or `null` when there is no
   *   handoff to accept.
   */
  private takeHandoff(): number | null {
    this.clearHistoryReference();

    const navigation = this.router.getCurrentNavigation();
    if (
      navigation === null ||
      navigation.trigger !== 'imperative' ||
      navigation.previousNavigation === null ||
      this.auth.session() === null
    ) {
      return null;
    }

    const reference: unknown = navigation.extras.state?.['booking'];
    return isBookingReference(reference) ? reference.id : null;
  }

  /**
   * Removes any `booking` member from the current history entry's state, so a
   * reload, Back or Forward finds no booking there. This app's handoff never
   * puts one in history ({@link bookingConfirmationState}), but an entry
   * written earlier in the same tab, such as by an older version of the app,
   * may carry one. The router has already written this page's URL to history
   * when the component is constructed, so the current entry is this page's.
   * Every other member, such as the router's `navigationId`, is kept.
   */
  private clearHistoryReference(): void {
    const state = this.location.getState();
    if (!isRecord(state) || !('booking' in state)) {
      return;
    }
    const kept: Record<string, unknown> = { ...state };
    delete kept['booking'];
    this.location.replaceState(this.location.path(true), '', kept);
  }

  /**
   * Writes the entry message into {@link announcement} when there is one, and
   * leaves the announcement unchanged otherwise.
   */
  private publishEntryAnnouncement(): void {
    const message = this.entryAnnouncement();
    if (message !== '') {
      this.announcement.set(message);
    }
  }

  /**
   * The page's entry message: the booking the banner confirms
   * ({@link confirmation}), when there is one, then "You have no bookings
   * yet." when the loaded list is empty. Empty when neither applies, which
   * includes while no list is loaded, because the banner too waits for the
   * caller's list.
   */
  private entryAnnouncement(): string {
    const c = this.confirmation();
    const list = this.bookings();
    const messages: string[] = [];
    if (c !== null) {
      messages.push(
        `Booking confirmed: ${c.hotelName}, ${c.roomTypeName}, room ${c.roomNumber}, ` +
          `${c.checkIn} to ${c.checkOut}. No payment is taken online in this version.`,
      );
    }
    if (list !== null && list.length === 0) {
      messages.push('You have no bookings yet.');
    }
    return messages.join(' ');
  }

  /** Cancels a pending live-region timer; does nothing when none is pending. */
  private clearLiveRegionTimer(): void {
    clearTimeout(this.liveRegionTimer);
    this.liveRegionTimer = undefined;
  }

  /**
   * Keeps {@link today} current while the page is open. A timer fires at the
   * next UTC midnight; the tab becoming visible (`visibilitychange`), being
   * restored from the back-forward cache (`pageshow`) or regaining focus
   * (`focus`) refreshes the date too, because a hidden or frozen tab delays its
   * timers. Every refresh re-arms the timer from the current clock, so a timer
   * that fired early or late corrects itself.
   *
   * The timer and the listeners run outside Angular's zone and so trigger no
   * change detection; only a refresh that finds a new date re-enters the zone
   * to apply it through {@link applyToday}. All of them are removed when the
   * page is destroyed.
   */
  private keepTodayCurrent(): void {
    const view = this.document.defaultView;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const refresh = (): void => {
      clearTimeout(timer);
      timer = setTimeout(refresh, msUntilNextUtcMidnight());
      const date = utcToday();
      if (date !== this.today()) {
        this.zone.run(() => this.applyToday(date));
      }
    };
    const refreshIfVisible = (): void => {
      if (this.document.visibilityState === 'visible') {
        refresh();
      }
    };

    this.zone.runOutsideAngular(() => {
      timer = setTimeout(refresh, msUntilNextUtcMidnight());
      this.document.addEventListener('visibilitychange', refreshIfVisible);
      view?.addEventListener('pageshow', refresh);
      view?.addEventListener('focus', refresh);
    });

    this.destroyRef.onDestroy(() => {
      clearTimeout(timer);
      this.document.removeEventListener('visibilitychange', refreshIfVisible);
      view?.removeEventListener('pageshow', refresh);
      view?.removeEventListener('focus', refresh);
    });
  }

  /**
   * Sets {@link today} to a new UTC date. A check-in that is now in the past
   * stops offering "Cancel", so when the "Cancel" on this page that has focus
   * is removed by the change, focus moves to that booking's status cell
   * instead of falling to the document body. The removal is rendered at once
   * to find out; focus anywhere else is left alone.
   *
   * @param date - The new current UTC date, as `yyyy-MM-dd`.
   */
  private applyToday(date: string): void {
    const active = this.document.activeElement;
    const focusedCancel =
      active !== null &&
      this.host.nativeElement.contains(active) &&
      active.matches('[data-testid^="cancel-"]')
        ? active
        : null;
    const row = focusedCancel?.closest('tr');
    const status = row?.querySelector<HTMLElement>('[data-testid^="booking-status-"]') ?? null;

    this.today.set(date);

    if (focusedCancel !== null) {
      this.changeDetector.detectChanges();
      if (!focusedCancel.isConnected) {
        status?.focus();
      }
    }
  }
}

/** The length of a day in milliseconds, as ECMAScript time counts every day. */
const DAY_MS = 86_400_000;

/**
 * How long, in milliseconds, the live region is in the page before the entry
 * message is written into it: the interval Angular CDK's `LiveAnnouncer`
 * waits, because screen readers miss live-region text written in the same
 * frame the region is inserted.
 */
const LIVE_REGION_DELAY_MS = 100;

/**
 * The current UTC calendar date as `yyyy-MM-dd`. It is derived from the ISO
 * string, never from local-time date methods, so it matches the API's UTC
 * boundary in every time zone.
 */
function utcToday(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * The milliseconds from now until the next UTC midnight, from 1 to
 * {@link DAY_MS}. ECMAScript time has no leap seconds and its epoch is a UTC
 * midnight, so the remainder is the time elapsed since the last one.
 */
function msUntilNextUtcMidnight(): number {
  return DAY_MS - (Date.now() % DAY_MS);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/**
 * Whether the `booking` member of router state identifies a booking: an
 * object whose `id` is a positive safe integer, the form of every id the API
 * issues. The state is untyped input, so anything else, such as `1.5`, `NaN`,
 * `Infinity`, `0`, a negative number, the string `"7"` or a missing id, is
 * rejected. Only the id is checked, because it is all the page takes from the
 * state; the banner's details come from the caller's own list.
 */
function isBookingReference(value: unknown): value is Pick<Booking, 'id'> {
  if (!isRecord(value)) {
    return false;
  }
  const id = value['id'];
  return typeof id === 'number' && Number.isSafeInteger(id) && id >= 1;
}
