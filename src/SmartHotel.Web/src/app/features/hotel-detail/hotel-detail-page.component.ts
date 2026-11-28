import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
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
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { CurrencyPipe, DOCUMENT, DatePipe, NgFor, NgIf } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { Subject, catchError, distinctUntilChanged, finalize, map, merge, of, skip, switchMap, take, tap } from 'rxjs';
import type { Observable } from 'rxjs';

import { BookingsApiService } from '../../core/api/bookings-api.service';
import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { Booking, CreateBookingRequest, HotelDetail, RoomTypeAvailability, StayQuery } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import type { Session } from '../../core/auth/auth.service';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';
import { focusHeadingOnInAppEntry } from '../auth/login-page.component';
import { bookingConfirmationState } from '../bookings/bookings-page.component';
import type { BookingConfirmationState } from '../bookings/bookings-page.component';

/** What one "Book" click submitted, captured when it was clicked. */
interface BookingAttempt {
  /** The loaded hotel, whose id and server-echoed stay the request carried. */
  hotel: HotelDetail;
  /** The room type the request booked. */
  roomType: RoomTypeAvailability;
  /** The session whose token the request carried: the current one when "Book" sent it. */
  session: Session;
  /**
   * Set once {@link HotelDetailPageComponent.canLeave} has let a navigation
   * leave this page because that session had ended; the answer is then
   * dropped, whenever it arrives.
   */
  released: boolean;
}

/**
 * The session state a booking's answer belongs to, decided when the answer
 * arrives: the live session that sent the request, or `null`, the signed-out
 * state that the request's own 401 produced.
 */
interface AnswerOwner {
  /** The sending session, or `null` for the signed-out state of its own 401. */
  readonly session: Session | null;
}

/**
 * The failed booking's error shown above the room table, with the session
 * state it was settled for; shown only while that state is current.
 */
interface BookErrorShown {
  /** The failure as shown inline. */
  error: UiError;
  /** The session it belongs to, or `null` for a 401 that signed the guest out. */
  owner: Session | null;
}

/**
 * A booking's answer shown in the late-booking notice rather than above the
 * room table, because it was for another hotel or stay than the one on
 * screen, with the session state it was settled for; shown only while that
 * state is current.
 */
interface LateBookingOutcome {
  /**
   * What was submitted, which the notice names, or `null` for a 401 that
   * signed the guest out: the signed-out page keeps none of the booking's
   * details, so its notice names no hotel, room type or dates.
   */
  attempt: BookingAttempt | null;
  /** The session it belongs to, or `null` for a 401 that signed the guest out. */
  owner: Session | null;
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
 * Entry focus: when a navigation within the app opens this page, as "View
 * rooms" does, the page's heading takes focus once it renders: the hotel's
 * name when the first load completes, or the load error's heading shown in
 * its place, unless focus is on another element or outside the page by then.
 * The first page of a fresh load is left alone. The helper,
 * `focusHeadingOnInAppEntry`, comes from the login page's module, which
 * focuses its own heading the same way, so this page loads that module's
 * code with its own, as it loads the bookings page's for
 * {@link bookingConfirmationState}.
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
 * stay it was submitted for, the loaded detail's id, dates and party size, and
 * to the session whose token it carried. It is never retried, and neither a
 * route change nor leaving the page cancels it, because cancelling cannot
 * undo a booking the server may already have committed; every "Book" stays
 * disabled until it is settled. `BookingsApiService.create` bounds the
 * request's network wait: it aborts a request still unanswered after
 * `REQUEST_DEADLINE_MS` (30 seconds) and fails it with status 0, settled like
 * any other failure and shown as "The service is unreachable.". That abort
 * cannot undo a booking the server may already have committed either. An
 * answer that arrives once the page is destroyed is dropped, with no
 * navigation and nothing shown. An answer that arrives while a load runs
 * waits for that load, so it is matched against what the guest is about to
 * see.
 *
 * Session: an answer is shown only while the session that sent the request is
 * still current and unexpired, checked when the answer arrives and again when
 * it is settled. A 401 that arrives once `authInterceptor` has cleared that
 * session for it, before the session's expiry, is the request's own rejection
 * and belongs to the signed-out state it produced, as the 401 rule below
 * requires. Once "Log out" or the session's expiry has ended the sending
 * session first, the answer is suppressed: no navigation, no router-state
 * hand-off, no notice naming the booking and no error above the table. A
 * neutral notice says only that the session ended before the booking was
 * answered and that My bookings, after logging in, shows whether it was
 * made; leaving cannot cancel a booking, and the notice never says it did. A
 * suppressed 201 or 409 for rooms the table counts (the same hotel,
 * overlapping dates) still revalidates them, as any visitor sees them. The
 * next load or booking clears the notice. A shown outcome stays bound to its
 * session state too: the error above the table and the late-booking notice
 * render only while that state is current, so a session that ends removes
 * them in the render that shows the change, and the notice's link to My
 * bookings never outlives the session it hands the booking to: a click on it
 * compares that session's expiry again, so an expiry not yet noticed
 * anywhere else sends the guest to My bookings without the booking, and the
 * guard asks for a login.
 *
 * An answer that belongs to the current session state is compared with the
 * hotel and stay on screen, both as the server echoed them:
 * - The same hotel and stay: a 201 navigates to `/bookings` with the new
 *   booking in the router state ({@link bookingConfirmationState}), held only
 *   in memory and never written to the browser's history, and
 *   `BookingsPageComponent` confirms it from its own list of the caller's
 *   bookings. Any failure, status 0 included, is shown above the room table.
 *   A 409 means the room type has no room free for the stay any more, so the
 *   page also revalidates the stay in place.
 * - Another hotel or stay, or none after a failed load: the answer leaves the
 *   current view alone, with no navigation and no error above its table. A
 *   notice at the top of the page names the room type, hotel and dates it was
 *   for and says that the booking is confirmed, with a link to My bookings
 *   that hands over the booking for the same confirmation, or that it could
 *   not be confirmed, with the error inline. A 401 has signed the guest out,
 *   so its notice keeps and names none of those details: it says only that
 *   the booking could not be confirmed, with the 401 inline and "Log in
 *   again".
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
 * `canDeactivate` asks {@link HotelDetailPageComponent.canLeave}. Once the
 * session that sent the booking has ended, as "Log out" ends it before it
 * starts its navigation to `/search`, every navigation leaves at once; the
 * request is neither cancelled nor retried, and the destroyed page drops its
 * answer. While that session is live, every navigation to another route, such
 * as browser Back to the search or nav "Search", "My bookings" or "Log in",
 * waits with the loading indicator on until the answer has been settled as
 * above. It is then refused, so the error above the table or the notice
 * stays in view, unless the session ended during the wait and the answer was
 * suppressed, when it proceeds; a 201 for the stay on screen instead goes to
 * `/bookings` with its confirmation, superseding the held navigation. The
 * request's deadline bounds only its network wait: an answer that arrives
 * while a load runs is settled once that load has, so the hold can also wait
 * for the load. While the hold lasts, no navigation, the one to `/login`
 * included, leaves this page; once the session has ended its answer is
 * dropped or hidden, so it never reaches another guest who signs in on this
 * tab. The session lives only in this tab's memory, so other tabs are
 * unaffected. A navigation to another hotel or stay keeps this page and is
 * never held.
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

  /** Tells which element has focus, on entry, when "Book" sends and once its answer is settled. */
  private readonly document = inject(DOCUMENT);

  /**
   * Registers the focus restore after the next render, outside an injection
   * context, and the focus move onto this page's heading on entry; both end
   * with the page.
   */
  private readonly injector = inject(Injector);

  /** Renders a session change at once, so focus on an outcome it removes can move on. */
  private readonly changeDetector = inject(ChangeDetectorRef);

  /** The "Log in again" link, rendered only while a 401 from "Book" is shown above the table. */
  @ViewChild('loginAgain') private loginAgain?: ElementRef<HTMLAnchorElement>;

  /** The late-booking notice, rendered while {@link HotelDetailPageComponent.lateOutcome} is set. */
  @ViewChild('lateNotice') private lateNotice?: ElementRef<HTMLDivElement>;

  /**
   * The loaded hotel's heading, focusable from script only; where focus lands
   * when the end of a session removes the late-booking notice that held it.
   */
  @ViewChild('hotelHeading') private hotelHeading?: ElementRef<HTMLHeadingElement>;

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
   * The failed booking's error last settled for the stay on screen, with the
   * session state it belongs to; `null` when there is none. Read through
   * {@link HotelDetailPageComponent.bookError}.
   */
  private readonly bookErrorShown = signal<BookErrorShown | null>(null);

  /**
   * The failed booking's error, shown above the room table, or beside the
   * load error when the revalidation after it failed, while the session state
   * it was settled for is current; `null` otherwise. A session that ends, at
   * "Log out" or at its expiry, hides it in the same render.
   */
  protected readonly bookError = computed<UiError | null>(() => {
    const shown = this.bookErrorShown();
    return shown !== null && shown.owner === this.auth.session() ? shown.error : null;
  });

  /** `true` from a "Book" click until its answer has been settled. */
  private readonly postPending = signal(false);

  /**
   * The booking request in flight, read by
   * {@link HotelDetailPageComponent.canLeave}; set when "Book" sends it and
   * `null` again once its answer is settled.
   */
  private pendingAttempt: BookingAttempt | null = null;

  /** `true` while a revalidation after a booking's answer is in flight. */
  private readonly refreshPending = signal(false);

  /**
   * `true` while this page's booking request, or the revalidation after its
   * answer, is in flight; disables every "Book".
   */
  protected readonly bookingPending = computed(() => this.postPending() || this.refreshPending());

  /**
   * The answer last settled for a booking submitted for another hotel or
   * stay than the one on screen, with the session state it belongs to; `null`
   * when there is none. Read through
   * {@link HotelDetailPageComponent.lateOutcome}.
   */
  private readonly lateOutcomeShown = signal<LateBookingOutcome | null>(null);

  /**
   * The answer to a booking submitted for another hotel or stay than the one
   * on screen, shown as a notice at the top of the page while the session
   * state it was settled for is current; `null` otherwise. A session that
   * ends hides it, with its link to My bookings, in the same render.
   */
  protected readonly lateOutcome = computed<LateBookingOutcome | null>(() => {
    const late = this.lateOutcomeShown();
    return late !== null && late.owner === this.auth.session() ? late : null;
  });

  /**
   * `true` once a booking's answer was suppressed because the session that
   * sent it had ended, showing the neutral session-ended notice; the next
   * load or booking sets it back to `false`.
   */
  protected readonly sessionEnded = signal(false);

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
   * {@link HotelDetailPageComponent.bookingFailed} has run: `true` when the
   * outcome was shown, or its navigation to My bookings started, for the
   * session state it belongs to, and `false` when it was suppressed because
   * the sending session had ended. Ends a navigation that
   * {@link HotelDetailPageComponent.canLeave} holds.
   */
  private readonly bookingSettled$ = new Subject<boolean>();

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

    // `bookError` and `lateOutcome` already hide an outcome once its session
    // state is no longer current; this also drops it from memory, so it can
    // never show again. `skip(1)` passes over the first emission, the session
    // current when the page starts watching, which no outcome predates.
    toObservable(this.auth.session)
      .pipe(skip(1), takeUntilDestroyed())
      .subscribe((session) => this.sessionChanged(session));

    // "View rooms" leaves with the search page, so focus is on the body. The
    // heading renders only once the first load settles, and the wait retries
    // on every render until then.
    focusHeadingOnInAppEntry(this.document, this.injector, this.router);
  }

  /**
   * Books one room of `rt` for the loaded stay, or sends a signed-out guest to
   * the login page with this URL as the return URL. Does nothing before the
   * hotel has loaded, while a newer load runs, or while a booking request or
   * the revalidation after its answer is in flight, so a repeated click can
   * never book a second room or book from stale counts.
   *
   * The request records the session whose token it carries. Its answer is
   * matched by the hotel and stay the request carried against the ones on
   * screen once no load is running, so it is never applied to another hotel
   * or stay, and it is shown only while that session is current and
   * unexpired, or, for the request's own 401, while the signed-out state that
   * 401 produced lasts; otherwise it is suppressed, as
   * {@link HotelDetailPageComponent.afterLoad} describes. Neither a route
   * change nor leaving the page cancels the request, because cancelling
   * cannot undo a booking the server may already have committed.
   * `BookingsApiService.create` bounds its network wait: it aborts the
   * request after `REQUEST_DEADLINE_MS` and fails it with status 0, shown
   * inline as "The service is unreachable.", which cannot undo a booking the
   * server may already have committed either. While it is in flight,
   * {@link HotelDetailPageComponent.canLeave} holds a navigation away from
   * this route while the session is live, and lets it leave at once once the
   * session has ended; an answer arriving after the page is destroyed is
   * dropped, with no navigation and nothing shown. The element focused when
   * the request is sent, normally this "Book" button, is recorded so that an
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

    const session = this.auth.session();
    if (session === null || !this.auth.isAuthenticated()) {
      void this.router.navigateByUrl('/login?returnUrl=' + encodeURIComponent(this.router.url));
      return;
    }

    const focused = this.document.activeElement;
    const trigger = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;
    this.bookErrorShown.set(null);
    this.lateOutcomeShown.set(null);
    this.sessionEnded.set(false);
    this.postPending.set(true);
    const attempt: BookingAttempt = { hotel, roomType: rt, session, released: false };
    this.pendingAttempt = attempt;
    const request: CreateBookingRequest = {
      hotelId: hotel.id,
      roomTypeId: rt.id,
      checkIn: hotel.checkIn,
      checkOut: hotel.checkOut,
      guests: hotel.guests,
    };

    // Deliberately not tied to this page's lifetime: leaving must never abort
    // a POST the server may already have committed. canLeave holds a
    // navigation away while the sending session is live and lets it go once
    // that session has ended; only the request deadline in
    // BookingsApiService.create ends the network wait. Who the answer belongs
    // to is decided here, as it arrives and before afterLoad can defer it
    // behind a load, and afterLoad drops an answer that arrives once the page
    // is destroyed.
    this.bookings.create(request).subscribe({
      next: (booking: Booking) =>
        this.afterLoad(attempt, this.answerOwner(attempt, false), (owner) =>
          this.booked(attempt, booking, trigger, owner),
        ),
      error: (err: unknown) => {
        const error = toUiError(err);
        this.afterLoad(attempt, this.answerOwner(attempt, error.status === 401), (owner) =>
          this.bookingFailed(attempt, error, trigger, owner),
        );
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
   * When the session that sent the booking is no longer current and live,
   * because "Log out" has already discarded it, it has expired, or the
   * booking's own 401 has cleared it while that answer waits for a load, the
   * navigation proceeds at once: the request is marked
   * released, so its answer is dropped whenever it arrives, and the page is
   * destroyed. That is how "Log out" reaches `/search` with a booking in
   * flight; the request is neither cancelled nor retried, and leaving does
   * not undo a booking the server may already have committed.
   *
   * Otherwise the navigation waits, with the loading indicator still on
   * because the request is pending, until the answer has been settled here.
   * It is then refused when the outcome was shown for its session state, so
   * the error above the table or the late-booking notice stays in view, and
   * proceeds when the answer was suppressed because the session ended during
   * the wait. While the hold lasts no navigation leaves this page, the one to
   * `/login` included. A 201 for the stay on screen starts its own navigation
   * to My bookings while it is settled; that navigation supersedes the held
   * one and passes this check, because no booking request is in flight any
   * more.
   *
   * The request's deadline, `REQUEST_DEADLINE_MS` in
   * `BookingsApiService.create`, bounds its network wait: the request is then
   * aborted and settled as a status-0 error like any other failure, which
   * cannot undo a booking the server may already have committed either. The
   * hold lasts until the answer is settled, which can additionally wait for
   * the stay load in flight when the answer arrives. This hold keeps a
   * booking's outcome in view; it does not protect access to any page.
   *
   * @param next - The router state the navigation would activate.
   * @returns `true` to let the navigation proceed, or an observable that emits
   *   once the booking's answer has been settled here: `false` when its
   *   outcome is in view, `true` when it was suppressed.
   */
  canLeave(next: RouterStateSnapshot): boolean | Observable<boolean> {
    const attempt = this.pendingAttempt;
    if (attempt === null || this.keepsThisRoute(next)) {
      return true;
    }
    if (!this.isLive(attempt)) {
      attempt.released = true;
      return true;
    }
    return this.bookingSettled$.pipe(
      take(1),
      map((shown) => !shown),
    );
  }

  /**
   * Opens My bookings from the late-booking notice's "View it in My bookings"
   * link, handing over the created booking only while the session it belongs
   * to is still current and unexpired at the click itself. The expiry timer
   * can be late, and `AuthService` compares the clock after a click only once
   * this handler has run, so the clock is compared here as well.
   *
   * A click with another button than the primary one, or with a modifier key,
   * is left to the browser, so a new tab or window opens the link's
   * `/bookings` itself, with no router state and, like every new tab, signed
   * out. Otherwise the browser's own navigation is prevented and the router
   * navigates to `/bookings`: with the booking in the router state
   * ({@link bookingConfirmationState}) while its session is live, for the
   * same confirmation as after a booking on the current view. Once it is not,
   * the stale outcomes are dropped first
   * ({@link HotelDetailPageComponent.dropStaleOutcomes}), a current session
   * whose `expiresAt` has passed is cleared, as `authGuard` clears it, and the
   * navigation carries no state, so the guard asks for a login and the
   * booking never reaches the router.
   *
   * @param event - The link's click.
   * @param late - The outcome the notice shows, whose `confirmation` the link
   *   hands over.
   */
  protected openLateBooking(event: MouseEvent, late: LateBookingOutcome): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();

    const current = this.auth.session();
    if (late.confirmation !== null && late.owner !== null && this.isCurrentState(late.owner, current)) {
      void this.router.navigate(['/bookings'], { state: late.confirmation });
      return;
    }

    this.dropStaleOutcomes(current);
    if (current !== null && !this.auth.isAuthenticated()) {
      this.auth.clearSession();
    }
    void this.router.navigate(['/bookings']);
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
   * Whether the session that sent `attempt` is still live: no navigation has
   * been released for it, it is still the current session, and its
   * `expiresAt` is still ahead. The expiry timer can fire late, so the clock
   * is compared as well; an unparseable `expiresAt` counts as expired.
   */
  private isLive(attempt: BookingAttempt): boolean {
    return (
      !attempt.released &&
      this.auth.session() === attempt.session &&
      Date.parse(attempt.session.expiresAt) > Date.now()
    );
  }

  /**
   * Whether `owner`, the session state a shown outcome was settled for, is
   * still the current and live one: it is `current`, and, unless it is the
   * signed-out state, its `expiresAt` is still ahead. The expiry timer can
   * fire late, so the clock is compared as well; an unparseable `expiresAt`
   * counts as expired.
   */
  private isCurrentState(owner: Session | null, current: Session | null): boolean {
    return owner === current && (owner === null || Date.parse(owner.expiresAt) > Date.now());
  }

  /**
   * Decides, as a booking's answer arrives, which session state it belongs
   * to, or `null` when it is suppressed because the sending session ended
   * first.
   *
   * A 201 or a failure other than 401 belongs to the sending session while it
   * is live. A 401 belongs to the signed-out state when the session is gone,
   * no navigation was released for it, and its `expiresAt` is still ahead:
   * `authInterceptor` clears a session on a 401 only while the rejected
   * request's session is still current, so that is this request's own
   * rejection, which the 401 rule keeps on this page with "Log in again".
   * That signed-out state keeps none of the booking's details when the 401
   * is shown in the late-booking notice
   * ({@link HotelDetailPageComponent.bookingFailed}). Any other 401 arrived
   * once the session had already ended, at "Log out" or its expiry, and is
   * suppressed with the rest.
   *
   * @param attempt - The booking the answer is for.
   * @param unauthorized - Whether the answer is a 401.
   */
  private answerOwner(attempt: BookingAttempt, unauthorized: boolean): AnswerOwner | null {
    if (!unauthorized) {
      return this.isLive(attempt) ? { session: attempt.session } : null;
    }
    const rejectedHere =
      this.auth.session() === null && !attempt.released && Date.parse(attempt.session.expiresAt) > Date.now();
    return rejectedHere ? { session: null } : null;
  }

  /**
   * Whether the session state an answer was assigned to on arrival is still
   * current when the answer is settled: the signed-out state while no
   * session exists, or the sending session while it is live.
   */
  private stillOwns(attempt: BookingAttempt, owner: AnswerOwner): boolean {
    return owner.session === null ? this.auth.session() === null : this.isLive(attempt);
  }

  /**
   * Settles a booking's answer through `settle` once no route-driven load is
   * in flight: at once, or when the load now running settles. A load that a
   * newer one supersedes never settles, so the newest load decides what is on
   * screen. `owner` is the session state
   * {@link HotelDetailPageComponent.answerOwner} assigned on arrival; it is
   * checked again when the answer is settled, because the wait for a load can
   * outlast the session, and `settle` receives `null`, a suppressed answer,
   * when that state is no longer current.
   * {@link HotelDetailPageComponent.bookingSettled$} emits right after
   * `settle` has run, so a navigation held for the answer resumes only once
   * the answer is shown, suppressed, or its own navigation has started. Once
   * the page is destroyed, as it is after
   * {@link HotelDetailPageComponent.canLeave} has let a navigation go because
   * the session had ended, nothing runs, at once or from the wait: a late
   * answer neither navigates nor touches the destroyed page.
   */
  private afterLoad(attempt: BookingAttempt, owner: AnswerOwner | null, settle: (owner: AnswerOwner | null) => void): void {
    if (this.destroyed()) {
      return;
    }
    const run = (): void => {
      if (this.destroyed()) {
        return;
      }
      const current = owner !== null && this.stillOwns(attempt, owner) ? owner : null;
      this.pendingAttempt = null;
      settle(current);
      this.bookingSettled$.next(current !== null);
    };
    if (!this.loadPending()) {
      run();
      return;
    }
    this.loadSettled$.pipe(take(1), takeUntilDestroyed(this.destroyRef)).subscribe(run);
  }

  /**
   * Settles a 201. With `owner` `null`, the sending session had ended, so it
   * is suppressed as {@link HotelDetailPageComponent.suppressAnswer}
   * describes. For the hotel and stay on screen it navigates to My bookings
   * with the booking in the router state. Otherwise it reports the booking in
   * the late-outcome notice, bound to its session, as confirmed with no
   * payment taken online and with the "View it in My bookings" link, and
   * stays on the current view, which it revalidates when the booked room may
   * be one its table counts, and then returns focus to `trigger` as after a
   * failed booking.
   */
  private booked(attempt: BookingAttempt, booking: Booking, trigger: HTMLElement | null, owner: AnswerOwner | null): void {
    this.postPending.set(false);
    if (owner === null) {
      this.suppressAnswer(attempt, trigger, true);
      return;
    }

    const shown = this.hotel();
    if (shown !== null && isSameStay(shown, attempt.hotel)) {
      void this.router.navigate(['/bookings'], { state: bookingConfirmationState(booking) });
      return;
    }

    this.lateOutcomeShown.set({
      attempt,
      owner: owner.session,
      confirmation: bookingConfirmationState(booking),
      error: null,
    });
    const restore = (): void => this.restoreFocusAfterBooking(trigger, false);
    if (shown !== null && overlapsStay(shown, attempt.hotel)) {
      this.revalidate(restore);
    } else {
      restore();
    }
  }

  /**
   * Settles a failed booking. With `owner` `null`, the sending session had
   * ended, so it is suppressed as
   * {@link HotelDetailPageComponent.suppressAnswer} describes. For the hotel
   * and stay on screen the error shows above the room table, bound to its
   * session state, and a 409 also revalidates the stay so the counts catch up
   * with the conflict. Otherwise the error shows in the late-outcome notice,
   * bound the same way, and the current table is left alone, except that a
   * 409 for rooms the table counts revalidates it too. The notice names the
   * room type, hotel and dates while `owner` is the sending session; for a
   * 401 owned by the signed-out state it produced, the notice stores no
   * {@link BookingAttempt} and names no booking, so the signed-out page shows
   * only that a booking could not be confirmed, with the 401 and "Log in
   * again". Focus then moves as
   * {@link HotelDetailPageComponent.restoreFocusAfterBooking} describes, once
   * any revalidation has settled, so it returns to the pressed "Book" only
   * when the fresh counts left it enabled, otherwise lands on the rooms
   * region, and lands on the load error's heading when the revalidation
   * failed and removed the table.
   */
  private bookingFailed(attempt: BookingAttempt, error: UiError, trigger: HTMLElement | null, owner: AnswerOwner | null): void {
    this.postPending.set(false);
    if (owner === null) {
      this.suppressAnswer(attempt, trigger, error.status === 409);
      return;
    }

    const shown = this.hotel();
    if (shown !== null && isSameStay(shown, attempt.hotel)) {
      this.bookErrorShown.set({ error, owner: owner.session });
      const restore = (): void => this.restoreFocusAfterBooking(trigger, error.status === 401);
      if (error.status === 409) {
        this.revalidate(restore);
      } else {
        restore();
      }
      return;
    }

    // The signed-out state of the booking's own 401 keeps none of its details.
    this.lateOutcomeShown.set({
      attempt: owner.session === null ? null : attempt,
      owner: owner.session,
      confirmation: null,
      error,
    });
    const restore = (): void => this.restoreFocusAfterBooking(trigger, false);
    if (error.status === 409 && shown !== null && overlapsStay(shown, attempt.hotel)) {
      this.revalidate(restore);
    } else {
      restore();
    }
  }

  /**
   * Settles an answer whose sending session ended before it was settled. It
   * shows none of it: no navigation to My bookings, no router-state
   * hand-off, no late-booking notice and no error above the table. Only the
   * neutral session-ended notice shows, which names no hotel, room type,
   * dates or status and never says the booking was cancelled. When the answer
   * created or exposed a change in the rooms the table counts, `changesRooms`
   * for a 201 or a 409 together with the same hotel and overlapping dates,
   * the stay on screen is revalidated, with the counts any visitor sees.
   * Focus then moves as after a failed booking.
   *
   * @param attempt - The booking the answer is for.
   * @param trigger - The element focused when the request was sent, or `null`.
   * @param changesRooms - Whether the answer was a 201 or a 409.
   */
  private suppressAnswer(attempt: BookingAttempt, trigger: HTMLElement | null, changesRooms: boolean): void {
    this.sessionEnded.set(true);
    const shown = this.hotel();
    const restore = (): void => this.restoreFocusAfterBooking(trigger, false);
    if (changesRooms && shown !== null && overlapsStay(shown, attempt.hotel)) {
      this.revalidate(restore);
    } else {
      restore();
    }
  }

  /**
   * Runs when the current session changes, before the change is rendered,
   * and drops the outcomes that belong to another session state than
   * `session`, as {@link HotelDetailPageComponent.dropStaleOutcomes}
   * describes.
   */
  private sessionChanged(session: Session | null): void {
    this.dropStaleOutcomes(session);
  }

  /**
   * Drops from memory the error above the table and the late-booking notice
   * when the session state they were settled for is not current and live for
   * `current` ({@link HotelDetailPageComponent.isCurrentState}): another
   * session or the signed-out state has replaced it, or it is `current` but
   * its `expiresAt` has passed. {@link HotelDetailPageComponent.bookError}
   * and {@link HotelDetailPageComponent.lateOutcome} already hide an outcome
   * of another session state; dropping it means it can never show again.
   * {@link HotelDetailPageComponent.sessionChanged} calls it for every
   * session change, and {@link HotelDetailPageComponent.openLateBooking} for
   * a click on the notice's link once its session is no longer live.
   *
   * When focus was inside the notice, as on its link to My bookings, the page
   * is rendered at once, and focus that the render removed moves to the
   * hotel's heading, or to the load error's heading shown in its place,
   * instead of falling to the document body. Focus anywhere else is left
   * alone.
   *
   * @param current - The current session, or `null` when signed out.
   */
  private dropStaleOutcomes(current: Session | null): void {
    const notice = this.lateNotice?.nativeElement;
    const active = this.document.activeElement;
    const focused = notice !== undefined && active instanceof HTMLElement && notice.contains(active) ? active : null;

    const error = this.bookErrorShown();
    if (error !== null && !this.isCurrentState(error.owner, current)) {
      this.bookErrorShown.set(null);
    }
    const late = this.lateOutcomeShown();
    if (late !== null && !this.isCurrentState(late.owner, current)) {
      this.lateOutcomeShown.set(null);
    }

    if (focused !== null) {
      this.changeDetector.detectChanges();
      if (!focused.isConnected) {
        const headings = [this.hotelHeading?.nativeElement, this.loadErrorHeading?.nativeElement];
        headings.find((heading) => heading?.isConnected)?.focus();
      }
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
   * content stays visible and its rows are reused by id; the booking error,
   * the late-outcome notice and the session-ended notice stay as they are. A
   * failure removes the stale hotel and is shown in place of the content,
   * with the booking error beside it. `settled` runs after either answer,
   * once `bookingPending` has fallen,
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
    this.bookErrorShown.set(null);
    this.lateOutcomeShown.set(null);
    this.sessionEnded.set(false);
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
   * none of these targets rendered, focus stays where it is. An answer
   * suppressed because its session ended calls this too, once the
   * session-ended notice is shown. An answer that arrives once the page is
   * destroyed never calls this, and a 201 for the stay on screen whose
   * session is still live navigates to My bookings instead.
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
