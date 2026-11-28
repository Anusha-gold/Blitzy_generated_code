import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  ViewChild,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, DOCUMENT, DatePipe, NgFor, NgIf } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { Subject, catchError, distinctUntilChanged, finalize, map, merge, of, switchMap, take, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { Booking, CreateBookingRequest, HotelDetail, RoomTypeAvailability, StayQuery } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';
import { bookingConfirmationState } from '../bookings/bookings-page.component';
import type { BookingConfirmationState } from '../bookings/bookings-page.component';

/** What one "Book" click submitted, captured when it was clicked. */
interface BookingAttempt {
  /** The loaded hotel, whose id and server-echoed stay the request carried. */
  hotel: HotelDetail;
  /** The room type the request booked. */
  roomType: RoomTypeAvailability;
}

/**
 * A booking's answer shown in the late-booking notice rather than above the
 * room table: one for another hotel or stay than the one on screen, or a 201
 * for the stay on screen once no live session remains.
 */
interface LateBookingOutcome {
  /** What was submitted. */
  attempt: BookingAttempt;
  /**
   * The router state handing the created booking to My bookings, or `null`
   * when the booking failed.
   */
  confirmation: BookingConfirmationState | null;
  /** The failure as shown inline, or `null` when the booking was created. */
  error: UiError | null;
}

/**
 * Whether a booking submitted for `booked` belongs to the hotel and stay on
 * screen: the same hotel id, dates and party size. Both are the server's own
 * echoes, so two URLs the server reads alike compare equal.
 */
function isSameStay(shown: HotelDetail, booked: HotelDetail): boolean {
  return (
    shown.id === booked.id &&
    shown.checkIn === booked.checkIn &&
    shown.checkOut === booked.checkOut &&
    shown.guests === booked.guests
  );
}

/**
 * Whether a booking submitted for `booked` draws on the rooms the table on
 * screen counts: the same hotel, and dates that overlap as half-open ranges,
 * the check-out day being free. The counts are physical rooms whatever the
 * party size, so `guests` plays no part. Both stays are server echoes in exact
 * `yyyy-MM-dd` form, whose text order is their date order.
 */
function overlapsStay(shown: HotelDetail, booked: HotelDetail): boolean {
  return shown.id === booked.id && booked.checkIn < shown.checkOut && shown.checkIn < booked.checkOut;
}

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
 * is not an integer literal with a local 404 carrying the API's unknown-hotel
 * problem, sending nothing. A newer load cancels the one still in flight.
 * While a load runs, the previous hotel
 * stays rendered but hidden, so none of its content or "Book" buttons can be
 * seen, focused or clicked, and the app-wide indicator shows progress; the new
 * detail then reuses the room rows whose room-type ids recur. A failed load,
 * such as the 400 for invalid dates or a malformed `guests` or the 404 for an
 * unknown or non-integer hotel id, removes the previous hotel and is shown in
 * place of the content.
 *
 * Booking: "Book" while signed out makes no request and navigates to
 * `/login?returnUrl=<current URL>`, so the guest comes back here after logging
 * in. Signed in, it sends `POST /api/bookings` built from the stay the server
 * echoed in the loaded detail, not from the raw query: those are exactly the
 * dates and party size that the displayed availability and stay total were
 * computed for, so the booking matches what the guest saw. The request carries
 * no price; the server computes the total. The booking is confirmed on
 * creation, with no payment step. The booking request is never retried.
 *
 * One booking is in flight at a time, and its answer belongs to the hotel and
 * stay it was submitted for: the loaded detail's id, dates and party size. It
 * is never retried, and neither a route change nor leaving the page cancels
 * it, because cancelling cannot undo a booking the server may already have
 * committed; every "Book" stays disabled until it is settled. Only the
 * request's deadline ends the client's wait: `BookingsApiService.create`
 * aborts a request still unanswered after `REQUEST_DEADLINE_MS` (30 seconds)
 * and fails it with status 0, settled like any other failure and shown as
 * "The service is unreachable.". That abort cannot undo a booking the server
 * may already have committed either. While the request is in flight the page
 * is destroyed only by the app's own teardown, because leaving is held as
 * described below; an answer that arrives after that is dropped, with no
 * navigation and nothing shown. An answer that arrives while a
 * load runs waits for that load, so it is matched against what the guest is
 * about to see; it is then compared with the hotel and stay on screen, both as
 * the server echoed them:
 * - The same hotel and stay: while the guest has a live session, a 201
 *   navigates to `/bookings` with the new booking in the router state
 *   ({@link bookingConfirmationState}), held only in memory and never written
 *   to the browser's history, and `BookingsPageComponent` confirms it from its
 *   own list of the caller's bookings. Once "Log out" or the session's expiry
 *   has left no live session, the guard of `/bookings` would send the guest
 *   to the login page and lose the confirmation, so the 201 is shown on this
 *   page in the notice described next, and the stay is revalidated. Any
 *   failure, status 0 included, is shown above the room table. A 409 means
 *   the room type has no room free for the stay any more, so the page also
 *   revalidates the stay in place.
 * - Another hotel or stay, or none after a failed load: the answer leaves the
 *   current view alone, with no navigation and no error above its table. A
 *   notice at the top of the page names the room type, hotel and dates it was
 *   for and says that the booking is confirmed, with a link to My bookings
 *   that hands over the booking for the same confirmation, or that it could
 *   not be confirmed, with the error inline (and "Log in again" for a 401).
 *   When the answer is a 201 or a 409 for the same hotel and dates that
 *   overlap the stay on screen, it changed or exposed a change in the very
 *   rooms the table counts, whatever the party size, so the page revalidates
 *   that stay as well. The next load or booking clears the notice; a
 *   revalidation does not.
 *
 * A revalidation reads the stay on screen again and goes through the same
 * cancel-on-supersede pipeline as the route's loads, so a route change ends
 * it. The content stays visible and its rows are reused by id; every "Book"
 * stays disabled until the fresh counts arrive, and which room types are full
 * is the server's answer, never assumed here. A failed revalidation is shown
 * like a failed load, in place of the content, because the counts on screen
 * are known to be stale; a booking error such as the 409 stays shown beside
 * it.
 *
 * Leaving while a booking is in flight: the `hotels/:id` route's
 * `canDeactivate` asks {@link HotelDetailPageComponent.canLeave}. Every
 * navigation to another route, whatever the session state, such as browser
 * Back to the search, nav "Search", "My bookings" or "Log in", or the
 * navigation to `/search` that "Log out" starts, waits with the loading
 * indicator on until the answer has been settled as above, and is then
 * refused, so the error above the table or the notice stays in view; a 201
 * for the stay on screen with a live session instead goes to `/bookings` with
 * its confirmation, superseding the navigation the guest started. The
 * request's deadline bounds the wait. "Log out" itself is not held:
 * `AuthService.logout` discards the session at once and the nav bar switches
 * to "Log in"; only its route change waits and is refused. While the hold
 * lasts, no navigation, the one to `/login` included, leaves this page, so no
 * other guest can sign in on this tab before the outcome is shown here to the
 * guest who sent the request; the session lives only in this tab's memory,
 * so other tabs are unaffected. A navigation to another hotel or stay keeps
 * this page and is never held.
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
  imports: [NgIf, NgFor, RouterLink, CurrencyPipe, DatePipe, ErrorMessageComponent],
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

  /** Tells which element has focus, when "Book" sends and once its answer is settled. */
  private readonly document = inject(DOCUMENT);

  /** Registers the focus restore after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /** The "Log in again" link, rendered only while a 401 from "Book" is shown above the table. */
  @ViewChild('loginAgain') private loginAgain?: ElementRef<HTMLAnchorElement>;

  /**
   * The focusable rooms region around the room table, rendered while the
   * loaded hotel has room types; where focus lands after a booking answer when
   * the "Book" it should return to is gone or disabled.
   */
  @ViewChild('roomsScroll') private roomsScroll?: ElementRef<HTMLDivElement>;

  /**
   * The heading of the load error shown in place of the content, focusable
   * from script only; where focus lands after a booking answer when a failed
   * revalidation has removed the room table, and the "Book" and rooms region
   * with it.
   */
  @ViewChild('loadErrorHeading') private loadErrorHeading?: ElementRef<HTMLHeadingElement>;

  /** Read by the template, whose "Log in again" links return to `router.url`. */
  protected readonly router = inject(Router);

  /**
   * The loaded hotel for the current stay; `null` before the first load
   * completes and after a failed one. A newer load keeps the previous hotel,
   * hidden through {@link HotelDetailPageComponent.loadPending}, until it
   * completes.
   */
  protected readonly hotel = signal<HotelDetail | null>(null);

  /** `true` while a route-driven load is in flight; hides the previous hotel. */
  protected readonly loadPending = signal(false);

  /**
   * The failed load's or revalidation's error, shown in place of the content;
   * `null` otherwise.
   */
  protected readonly loadError = signal<UiError | null>(null);

  /**
   * The failed booking's error, shown above the room table, or beside the
   * load error when the revalidation after it failed; `null` otherwise.
   */
  protected readonly bookError = signal<UiError | null>(null);

  /** `true` from a "Book" click until its answer has been settled. */
  private readonly postPending = signal(false);

  /** `true` while a revalidation after a booking's answer is in flight. */
  private readonly refreshPending = signal(false);

  /**
   * `true` while this page's booking request, or the revalidation after its
   * answer, is in flight; disables every "Book".
   */
  protected readonly bookingPending = computed(() => this.postPending() || this.refreshPending());

  /**
   * The answer to a booking submitted for another hotel or stay than the one
   * on screen, or a 201 for the stay on screen once no live session remains,
   * shown as a notice at the top of the page; `null` otherwise.
   */
  protected readonly lateOutcome = signal<LateBookingOutcome | null>(null);

  /**
   * Asks for a revalidation of the stay on screen after a booking's answer,
   * carrying what to run once it has answered.
   */
  private readonly refresh$ = new Subject<() => void>();

  /** Emits when a route-driven load settles, with a hotel or with an error. */
  private readonly loadSettled$ = new Subject<void>();

  /**
   * Emits once a booking's answer has been settled on this page, after
   * {@link HotelDetailPageComponent.booked} or
   * {@link HotelDetailPageComponent.bookingFailed} has run; ends a navigation
   * that {@link HotelDetailPageComponent.canLeave} holds.
   */
  private readonly bookingSettled$ = new Subject<void>();

  /** `true` once this page is destroyed; a booking answer arriving later is not settled. */
  private readonly destroyed = signal(false);

  constructor() {
    this.destroyRef.onDestroy(() => this.destroyed.set(true));

    const loads$ = merge(this.route.paramMap, this.route.queryParamMap).pipe(
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
      map(({ id, stay }) => () => this.load(id, stay)),
    );

    // Route loads and revalidations share one switchMap, so whichever starts
    // last ends the other and only one GET at a time writes `hotel`. Each
    // emission maps to a thunk that switchMap runs only after it has ended the
    // previous request.
    merge(loads$, this.refresh$.pipe(map((settled) => () => this.refresh(settled))))
      .pipe(
        switchMap((run) => run()),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /**
   * Books one room of `rt` for the loaded stay, or sends a signed-out guest to
   * the login page with this URL as the return URL. Does nothing before the
   * hotel has loaded, while a newer load runs, or while a booking request or
   * the revalidation after its answer is in flight, so a repeated click can
   * never book a second room or book from stale counts.
   *
   * The answer is matched by the hotel and stay the request carried against
   * the ones on screen once no load is running, so it is never applied to
   * another hotel or stay. Neither a route change nor leaving the page cancels
   * the request, because cancelling cannot undo a booking the server may
   * already have committed. Only its deadline ends the client's wait:
   * `BookingsApiService.create` aborts it after `REQUEST_DEADLINE_MS` and
   * fails it with status 0, shown inline as "The service is unreachable.",
   * which cannot undo a booking the server may already have committed either.
   * While it is in flight, {@link HotelDetailPageComponent.canLeave} holds
   * every navigation away from this route, whatever the session state, until
   * the answer has been settled here, so it reaches the guest who sent it
   * even after "Log out" or the session's expiry; only the app's own teardown
   * destroys the page first, and an answer arriving after that is dropped,
   * with no navigation and nothing shown. The element focused when the
   * request is sent, normally this "Book" button, is recorded so that an
   * answer that keeps the guest on this page can move focus to "Log in again"
   * after a 401 shown above the table, back to it otherwise, or, when the
   * button is gone or disabled by then, to the rooms region, or to the load
   * error's heading when a failed revalidation has removed the table.
   *
   * @param rt - The room type whose "Book" button was clicked.
   */
  protected book(rt: RoomTypeAvailability): void {
    const hotel = this.hotel();
    if (hotel === null || this.loadPending() || this.bookingPending()) {
      return;
    }

    if (!this.auth.isAuthenticated()) {
      void this.router.navigateByUrl('/login?returnUrl=' + encodeURIComponent(this.router.url));
      return;
    }

    const focused = this.document.activeElement;
    const trigger = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;
    this.bookError.set(null);
    this.lateOutcome.set(null);
    this.postPending.set(true);
    const attempt: BookingAttempt = { hotel, roomType: rt };
    const request: CreateBookingRequest = {
      hotelId: hotel.id,
      roomTypeId: rt.id,
      checkIn: hotel.checkIn,
      checkOut: hotel.checkOut,
      guests: hotel.guests,
    };

    // Deliberately not tied to this page's lifetime: leaving must never abort
    // a POST the server may already have committed, and canLeave holds every
    // navigation away until its answer is settled. Only the request deadline
    // in BookingsApiService.create ends the wait. afterLoad drops an answer
    // that arrives once the app's teardown has destroyed the page.
    this.bookings.create(request).subscribe({
      next: (booking: Booking) => this.afterLoad(() => this.booked(attempt, booking, trigger)),
      error: (err: unknown) => {
        const error = toUiError(err);
        this.afterLoad(() => this.bookingFailed(attempt, error, trigger));
      },
    });
  }

  /**
   * Decides whether the router may leave this page for `next`; the
   * `hotels/:id` route's `canDeactivate` calls it. It answers `true` at once
   * unless both hold: a booking request from this page is in flight, and
   * `next` leaves the `hotels/:id` route. A navigation to another hotel or
   * stay keeps this page, so it is never held, and its answer is settled
   * against the new view.
   *
   * Otherwise the navigation waits until the answer has been settled here,
   * with the loading indicator still on because the request is pending, and
   * is then refused, so the error above the table or the late-booking notice
   * stays in view. The session state plays no part: once "Log out" or the
   * session's expiry has left no live session, the answer is still settled
   * here, a 201 for the stay on screen in the late-booking notice. "Log out"
   * itself is not held: it discards the session at once, and only the
   * navigation to `/search` it starts waits and is refused. While the hold
   * lasts no navigation leaves this page, the one to `/login` included, so no
   * other guest can sign in on this tab before the outcome is shown. A 201
   * for the stay on screen with a live session starts its own navigation to
   * My bookings while it is settled; that navigation supersedes the held one
   * and passes this check, because no booking request is in flight any more.
   *
   * Leaving never cancels the request, because cancelling cannot undo a
   * booking the server may already have committed. Only the request's
   * deadline, `REQUEST_DEADLINE_MS` in `BookingsApiService.create`, ends the
   * client's wait, and so bounds the hold: the request is then aborted and
   * settled as a status-0 error like any other failure, which cannot undo a
   * booking the server may already have committed either. This hold keeps a
   * booking's outcome in view; it does not protect access to any page.
   *
   * @param next - The router state the navigation would activate.
   * @returns `true` to let the navigation proceed, or an observable that emits
   *   `false` once the booking's answer has been settled here.
   */
  canLeave(next: RouterStateSnapshot): boolean | Observable<boolean> {
    if (!this.postPending() || this.keepsThisRoute(next)) {
      return true;
    }
    return this.bookingSettled$.pipe(
      take(1),
      map(() => false),
    );
  }

  /** Identifies a room type row by its id for `NgFor`. */
  protected trackById(_index: number, rt: RoomTypeAvailability): number {
    return rt.id;
  }

  /**
   * Whether `next` still activates this page's route, `hotels/:id`, as a
   * navigation to another hotel or stay does: the router then keeps this
   * component and only changes its parameters.
   */
  private keepsThisRoute(next: RouterStateSnapshot): boolean {
    const config = this.route.routeConfig;
    const activates = (snapshot: ActivatedRouteSnapshot): boolean =>
      snapshot.routeConfig === config || snapshot.children.some(activates);
    return activates(next.root);
  }

  /**
   * Settles a booking's answer through `settle` once no route-driven load is
   * in flight: at once, or when the load now running settles. A load that a
   * newer one supersedes never settles, so the newest load decides what is on
   * screen. {@link HotelDetailPageComponent.bookingSettled$} emits right after
   * `settle` has run, so a navigation held for the answer resumes only once
   * the answer is shown or its own navigation has started. Once the page is
   * destroyed, which while a booking is in flight only the app's own teardown
   * does, because {@link HotelDetailPageComponent.canLeave} holds every
   * navigation away, nothing runs, at once or from the wait: a late answer
   * neither navigates nor touches the destroyed page.
   */
  private afterLoad(settle: () => void): void {
    if (this.destroyed()) {
      return;
    }
    const run = (): void => {
      if (this.destroyed()) {
        return;
      }
      settle();
      this.bookingSettled$.next();
    };
    if (!this.loadPending()) {
      run();
      return;
    }
    this.loadSettled$.pipe(take(1), takeUntilDestroyed(this.destroyRef)).subscribe(run);
  }

  /**
   * Settles a 201. For the hotel and stay on screen, while the guest still has
   * a live session, it navigates to My bookings with the booking in the router
   * state. Otherwise it reports the booking in the late-outcome notice, as
   * confirmed with no payment taken online and with the "View it in My
   * bookings" link, and stays on the current view, which it revalidates when
   * the booked room may be one its table counts, and then returns focus to
   * `trigger` as after a failed booking. That includes the stay on screen once
   * "Log out" or the session's expiry has left no live session: the guard of
   * `/bookings` would then send the guest to the login page and lose the
   * confirmation, so it is shown here.
   */
  private booked(attempt: BookingAttempt, booking: Booking, trigger: HTMLElement | null): void {
    this.postPending.set(false);
    const shown = this.hotel();
    if (shown !== null && isSameStay(shown, attempt.hotel) && this.auth.isAuthenticated()) {
      void this.router.navigate(['/bookings'], { state: bookingConfirmationState(booking) });
      return;
    }

    this.lateOutcome.set({ attempt, confirmation: bookingConfirmationState(booking), error: null });
    const restore = (): void => this.restoreFocusAfterBooking(trigger, false);
    if (shown !== null && overlapsStay(shown, attempt.hotel)) {
      this.revalidate(restore);
    } else {
      restore();
    }
  }

  /**
   * Settles a failed booking. For the hotel and stay on screen the error shows
   * above the room table, and a 409 also revalidates the stay so the counts
   * catch up with the conflict. Otherwise the error shows in the late-outcome
   * notice and the current table is left alone, except that a 409 for rooms
   * the table counts revalidates it too. Focus then moves as
   * {@link HotelDetailPageComponent.restoreFocusAfterBooking} describes, once
   * any revalidation has settled, so it returns to the pressed "Book" only
   * when the fresh counts left it enabled, otherwise lands on the rooms
   * region, and lands on the load error's heading when the revalidation
   * failed and removed the table.
   */
  private bookingFailed(attempt: BookingAttempt, error: UiError, trigger: HTMLElement | null): void {
    this.postPending.set(false);
    const shown = this.hotel();
    if (shown !== null && isSameStay(shown, attempt.hotel)) {
      this.bookError.set(error);
      const restore = (): void => this.restoreFocusAfterBooking(trigger, error.status === 401);
      if (error.status === 409) {
        this.revalidate(restore);
      } else {
        restore();
      }
      return;
    }

    this.lateOutcome.set({ attempt, confirmation: null, error });
    const restore = (): void => this.restoreFocusAfterBooking(trigger, false);
    if (error.status === 409 && shown !== null && overlapsStay(shown, attempt.hotel)) {
      this.revalidate(restore);
    } else {
      restore();
    }
  }

  /**
   * Starts a revalidation of the stay on screen, running `settled` once it has
   * answered; a revalidation that a route change ends never runs it.
   * `refreshPending` rises in the same turn as `postPending` falls, so no
   * "Book" is enabled in between.
   */
  private revalidate(settled: () => void): void {
    this.refreshPending.set(true);
    this.refresh$.next(settled);
  }

  /**
   * Reads the hotel and stay on screen again, as the server echoed them, after
   * a booking's answer, so the counts catch up with it. It runs in the route's
   * `switchMap` and keeps `bookingPending` raised until it settles. The
   * content stays visible and its rows are reused by id; the booking error and
   * the late-outcome notice stay as they are. A failure removes the stale
   * hotel and is shown in place of the content, with the booking error beside
   * it. `settled` runs after either answer, once `bookingPending` has fallen,
   * and never when a route change ends the revalidation.
   *
   * As in {@link HotelDetailPageComponent.load}, the error handling sits
   * inside this inner observable, and `switchMap` ends the superseded request,
   * running its `finalize`, before it calls this method.
   */
  private refresh(settled: () => void): Observable<HotelDetail | null> {
    const shown = this.hotel();
    if (shown === null) {
      this.refreshPending.set(false);
      settled();
      return of(null);
    }

    let answered = false;
    const stay: StayQuery = { checkIn: shown.checkIn, checkOut: shown.checkOut, guests: String(shown.guests) };
    return this.hotels.getHotel(String(shown.id), stay).pipe(
      tap((detail) => {
        this.hotel.set(detail);
        answered = true;
      }),
      catchError((err: unknown) => {
        this.hotel.set(null);
        this.loadError.set(toUiError(err));
        answered = true;
        return of(null);
      }),
      finalize(() => {
        this.refreshPending.set(false);
        if (answered) {
          settled();
        }
      }),
    );
  }

  /**
   * Loads one hotel for one stay, after clearing the previous outcome. The
   * previous hotel stays in place, hidden through `loadPending`, so its room
   * rows are reused by id when the new detail arrives; a failed load removes
   * it, so an error never sits beside another stay's counts. `switchMap` ends
   * a revalidation still in flight, and a booking still in flight keeps every
   * "Book" disabled until its answer is settled against this load's outcome.
   *
   * The error handling sits inside this inner observable, so a failed load
   * never ends the outer route stream and the next route change loads again.
   * `switchMap` ends the superseded request, running its `finalize`, before
   * it calls this method, so an old request never clears the new one's
   * `loadPending`; only a load that answered signals `loadSettled$`.
   */
  private load(id: string, stay: StayQuery): Observable<HotelDetail | null> {
    this.loadPending.set(true);
    this.loadError.set(null);
    this.bookError.set(null);
    this.lateOutcome.set(null);
    let settled = false;

    return this.hotels.getHotel(id, stay).pipe(
      tap((detail) => {
        this.hotel.set(detail);
        settled = true;
      }),
      catchError((err: unknown) => {
        this.hotel.set(null);
        this.loadError.set(toUiError(err));
        settled = true;
        return of(null);
      }),
      finalize(() => {
        this.loadPending.set(false);
        if (settled) {
          this.loadSettled$.next();
        }
      }),
    );
  }

  /**
   * Moves focus once a booking answer that keeps the guest on this page has
   * rendered and re-enabled the "Book" buttons: to the "Log in again" link
   * after a 401 shown above the table, otherwise, or when that link is
   * missing, back to `trigger`. When that target is missing, no longer in the
   * document or disabled, as the pressed "Book" is once the revalidation after
   * a 409 finds its room type full, focus moves to the rooms region around the
   * table instead. When the rooms region is gone too, as after a revalidation
   * that failed and removed the hotel content, focus moves to the heading of
   * the load error shown in its place, beside which the booking error stays.
   * It never moves to another room type's "Book", where one more Enter would
   * book a room the guest did not choose. Disabling the focused "Book" button
   * while the request is pending can drop focus to the document body, so
   * focus moves only when it is still on the body, on nothing, or on
   * `trigger`; anything else the guest focused meanwhile keeps focus. With
   * none of these targets rendered, focus stays where it is. An answer that
   * arrives once the page is destroyed never calls this, and a 201 for the
   * stay on screen while the guest still has a live session navigates to My
   * bookings instead.
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
        const preferred = loginAgain?.isConnected ? loginAgain : trigger;
        const fallbacks = [this.roomsScroll?.nativeElement, this.loadErrorHeading?.nativeElement];
        const target =
          preferred !== null && preferred.isConnected && !preferred.matches(':disabled')
            ? preferred
            : fallbacks.find((element) => element?.isConnected);
        target?.focus();
      },
      { injector: this.injector },
    );
  }
}
