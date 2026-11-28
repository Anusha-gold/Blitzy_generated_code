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
import { CurrencyPipe, DOCUMENT, NgFor, NgIf } from '@angular/common';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { EMPTY, Observable, Subject, catchError, finalize, map, merge, switchMap, tap } from 'rxjs';

import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { HotelSearchQuery, HotelSummary } from '../../core/api/models';
import { UiError, fieldErrorIds, toUiError, withoutFieldErrors } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * A submit's hand-off of focus from the Search button, which the pending
 * state disables, to the page heading, which keeps it while the search runs.
 */
interface HeadingHandOff {
  /** The heading that took focus. */
  readonly heading: HTMLElement;
  /** Whether focus has stayed on the heading since the hand-off, up to `end`. */
  readonly holds: () => boolean;
  /** Ends the hand-off and removes its listener; calling it again does nothing. */
  readonly end: () => void;
}

/**
 * The hotel search screen, route `search`: a city, a stay and a party size,
 * answered by `GET /api/hotels`.
 *
 * The URL query (`city`, `checkIn`, `checkOut`, `guests`) is the single source
 * of truth for the criteria. Submitting the form only writes them into the URL,
 * and every change of the query runs the search, so a deep link, a reload and
 * back/forward navigation all search and refill the form. Navigating to an
 * identical URL makes the router emit nothing, so a repeated identical search
 * goes through a rerun subject that replays the current query instead. Each
 * search runs through `switchMap`: a newer one cancels the request still in
 * flight, and only the latest outcome is ever shown.
 *
 * The visible states:
 * - idle on bare `/search` (the nav and brand links, "Log out" and the `''`
 *   and `**` redirects): the form only, holding its initial criteria (no
 *   city, no dates, 1 guest), with no results, message or error. Leaving a
 *   search URL for it reuses this page, and the form is reset then too, so
 *   no earlier, rejected or unsent criteria linger without their results;
 * - loading: the app-wide indicator, driven by the loading interceptor, while
 *   the submit button is disabled through {@link SearchPageComponent.pending};
 *   the previous results stay rendered but hidden, so none can be seen,
 *   focused or clicked, and the new list then reuses the rows whose hotel ids
 *   recur;
 * - results in server order (lowest nightly price, then name), or the
 *   empty-result message naming the city that was actually searched; both
 *   read the completed search's criteria, never the live form;
 * - an inline error through `ErrorMessageComponent`, mapped by `toUiError`:
 *   the server's 400 problem with its field errors, or "The service is
 *   unreachable." for status 0. Editing a field the server rejected ends
 *   that field's messages and invalid mark before any new search, as on the
 *   register and login pages.
 *
 * The server is the only authority on the criteria: the page adds no
 * validators and never blocks a submission, so the server's 400 reaches the
 * guest inline.
 */
@Component({
  selector: 'app-search-page',
  standalone: true,
  imports: [NgIf, NgFor, RouterLink, CurrencyPipe, ReactiveFormsModule, ErrorMessageComponent],
  templateUrl: './search-page.component.html',
  styleUrl: './search-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SearchPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly hotels = inject(HotelsApiService);
  private readonly fb = inject(NonNullableFormBuilder);

  /** Tells which element has focus, at submit and once the search settles. */
  private readonly document = inject(DOCUMENT);

  /** Registers the focus restore after the next render, outside an injection context. */
  private readonly injector = inject(Injector);

  /**
   * The element that had focus when the guest last submitted the form, held
   * only until the search that submit starts begins: that search takes and
   * clears it, so the record travels with that one request and no later
   * search can act on it. `null` once taken, or when nothing was focused.
   */
  private focusedAtSubmit: HTMLElement | null = null;

  /**
   * The last submit's hand-off of focus to the page heading, which travels
   * with {@link SearchPageComponent.focusedAtSubmit} to the search that
   * submit starts; `null` when that submit handed none over, and once that
   * search has taken it.
   */
  private headingHandOff: HeadingHandOff | null = null;

  /** The Search button, which the pending state disables. */
  @ViewChild('searchButton') private searchButton?: ElementRef<HTMLButtonElement>;

  /**
   * The page's "Find a hotel" heading, which holds focus while a search sent
   * from the Search button is pending.
   */
  @ViewChild('heading') private heading?: ElementRef<HTMLHeadingElement>;

  /**
   * The search criteria as entered, every one the text its input holds. The
   * date inputs yield raw `yyyy-MM-dd` strings, and the guest count is a text
   * input with a numeric keyboard hint, so a decimal, exponent, signed,
   * white-space or incomplete entry such as `2.0`, `2e0` or `1e` stays exactly
   * as typed, for the server to judge, instead of being read as a number. A
   * guest count the guest cleared is left out of the URL, and `run` then sends
   * the API 1. The one empty guest count that is sent is the URL's own
   * explicit empty `guests=` while the field still holds it unedited: it goes
   * to the API empty, for the server to judge, and the guest count's pristine
   * state is what tells the two apart. These initial values are what `reset()`
   * restores on the idle page, pristine as well.
   */
  protected readonly form = this.fb.group({
    city: '',
    checkIn: '',
    checkOut: '',
    guests: '1',
  });

  /** `true` while this page's own search request is in flight. */
  protected readonly pending = signal(false);

  /**
   * The failed search's error, or `null` when the last search did not fail.
   * Each new search clears it. Editing a field drops that field's messages,
   * and the whole error once no field message is left; an error without field
   * messages, such as status 0, stays until the next search.
   */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'search-error';

  /**
   * The ids of the server's messages per rejected field (`city`, `checkIn`,
   * `checkOut`, `guests`), which the inputs reference through
   * `aria-describedby` and mark with `aria-invalid`; empty while there is no
   * error, so each new search clears them. A field's entry lasts until the
   * guest edits that field or the next search clears the error.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /**
   * The completed search's hotels in server order; `null` until one completes,
   * after a failed one and on the idle page. A newer search keeps them, hidden,
   * until it completes.
   */
  protected readonly results = signal<HotelSummary[] | null>(null);

  /** The criteria of the last search that completed, which the results belong to. */
  protected readonly searched = signal<HotelSearchQuery | null>(null);

  private readonly rerun$ = new Subject<void>();

  constructor() {
    // Editing a field the server rejected ends that field's error, which
    // also removes its aria-invalid mark and description. The control names
    // are the API's error keys. `run` refilling or resetting the form emits
    // here too, and then clears the whole error itself.
    for (const [field, control] of Object.entries(this.form.controls)) {
      control.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
        this.error.update((shown) => (shown === null ? null : withoutFieldErrors(shown, field)));
      });
    }

    merge(this.route.queryParamMap, this.rerun$.pipe(map(() => this.route.snapshot.queryParamMap)))
      .pipe(
        switchMap((params) => {
          // The record belongs to the search this emission starts: taking it
          // here means a cancelled search, or the idle state, cannot leave it
          // for a later back/forward search to act on.
          const target = this.focusedAtSubmit;
          this.focusedAtSubmit = null;
          const handOff = this.headingHandOff;
          this.headingHandOff = null;
          // `complete` follows a response (`run` turns an error into a
          // completion) but never an unsubscribe, so a search that a newer
          // one cancels never moves focus. That search, and one that ends
          // with the page, still ends its hand-off.
          return this.run(params).pipe(
            tap({ complete: () => this.restoreFocusAfterSearch(target, handOff) }),
            finalize(() => handOff?.end()),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe();

    // A hand-off that no search has taken yet ends with the page.
    inject(DestroyRef).onDestroy(() => this.headingHandOff?.end());
  }

  /**
   * Writes the form's criteria into the URL, which runs the search. Every
   * criterion goes into the URL as the text its input holds, with one
   * exception: a guest count left exactly empty means the guest left that
   * optional field blank, so it is left out of the URL, and `run` supplies the
   * explicit 1 the API receives. The one place an empty guest count is sent is
   * an unedited explicit empty from the URL: while the field still shows the
   * current URL's own `guests=`, not edited since the page filled it in (the
   * control is pristine), it goes out as `guests=` again, so the server judges
   * it. Once the guest edits the field, even back to empty, an empty field is
   * left out. Any other guest-count text, white space, a decimal, an exponent
   * or an incomplete entry included, is written verbatim, so the server judges
   * it. When the criteria, that guest count included, equal the current URL,
   * the router would not emit, so the current query is replayed instead, and an
   * unchanged re-submit sends the same text again. The element focused at
   * submit, normally the Search button that the pending state disables, is
   * recorded for the search this submit starts, so focus can return to it
   * once that search completes with a response; a search cancelled by a
   * newer one never moves focus. A disabled button loses focus, so when the
   * Search button holds it, it is handed at once, before the pending state
   * renders, to the page heading, which stays rendered and focusable while
   * the search runs, as {@link SearchPageComponent.handFocusToHeading} sets
   * out; Enter in a field leaves focus in that field.
   */
  protected submit(): void {
    const focused = this.document.activeElement;
    this.focusedAtSubmit = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;
    // An earlier submit's hand-off that no search has taken ends here, as
    // that submit's record of the focused element does.
    this.headingHandOff?.end();
    this.headingHandOff = null;
    const heading = this.heading?.nativeElement;
    if (focused !== null && focused === this.searchButton?.nativeElement && heading !== undefined) {
      this.headingHandOff = this.handFocusToHeading(heading);
    }

    const { city, checkIn, checkOut, guests } = this.form.getRawValue();
    const current = this.route.snapshot.queryParamMap;
    const queryParams: Record<string, string> = { city, checkIn, checkOut };
    const keepsUrlEmpty = this.form.controls.guests.pristine && current.get('guests') === '';
    const sentGuests = guests === '' && !keepsUrlEmpty ? null : guests;
    if (sentGuests !== null) {
      queryParams['guests'] = sentGuests;
    }

    const same =
      current.get('city') === city &&
      current.get('checkIn') === checkIn &&
      current.get('checkOut') === checkOut &&
      current.get('guests') === sentGuests;
    if (same) {
      this.rerun$.next();
      return;
    }

    void this.router.navigate(['/search'], { queryParams });
  }

  /** Identifies a result row by its hotel id for `NgFor`. */
  protected trackById(_index: number, hotel: HotelSummary): number {
    return hotel.id;
  }

  /**
   * Runs one search for the given query, or returns to the idle state when the
   * query names none of the four criteria: the form is reset to its initial
   * criteria, and any results, error and pending state are cleared. `city`,
   * `checkIn`, `checkOut` and a present `guests` are passed on exactly as the
   * URL holds them, an empty string included; an absent `guests` is sent as
   * 1, the server's own default. The server alone decides their validity. The
   * form is refilled with the same text, so the guest count shows exactly what
   * the URL holds, such as `2.0` or an explicit empty `guests=`, and the guest
   * count is marked pristine, as not edited since. An unchanged re-submit
   * therefore sends that text again, an empty one included: an unedited
   * explicit empty from the URL is the one place `submit` sends an empty
   * field. The error handling and `finalize` sit inside this inner
   * observable, so a failed search never ends the outer stream and a
   * cancelled one still clears `pending`.
   */
  private run(params: ParamMap): Observable<HotelSummary[]> {
    // `has`, not truthiness: an empty value counts as present, so a blank
    // submission still reaches the server and its 400 shows inline. Any one
    // criterion, `guests` alone included, makes it a search, so the server's
    // 400 for the missing ones shows inline too.
    if (
      !params.has('city') &&
      !params.has('checkIn') &&
      !params.has('checkOut') &&
      !params.has('guests')
    ) {
      // The page is reused when the brand, nav "Search" or "Log out" leaves a
      // search for bare /search, so the form returns to its initial criteria
      // too, as a reload of bare /search shows them.
      this.form.reset();
      this.results.set(null);
      this.searched.set(null);
      this.error.set(null);
      this.pending.set(false);
      return EMPTY;
    }

    const city = params.get('city') ?? '';
    const checkIn = params.get('checkIn') ?? '';
    const checkOut = params.get('checkOut') ?? '';
    // 1 is the server's own default for an omitted count. A present value goes
    // out as written, so the server binds the text itself: `2.0` gets its 400
    // rather than being read as 2.
    const guests = params.get('guests') ?? '1';
    const q: HotelSearchQuery = { city, checkIn, checkOut, guests };

    this.form.patchValue({ city, checkIn, checkOut, guests });
    // The guest count now shows the URL's own text, unedited, so `submit` can
    // tell an explicit empty `guests` from a field the guest cleared.
    this.form.controls.guests.markAsPristine();
    this.error.set(null);
    this.pending.set(true);

    return this.hotels.search(q).pipe(
      tap((list) => {
        this.searched.set(q);
        this.results.set(list);
      }),
      catchError((err: unknown) => {
        this.results.set(null);
        this.error.set(toUiError(err));
        return EMPTY;
      }),
      finalize(() => this.pending.set(false)),
    );
  }

  /**
   * Returns focus to the element recorded at submit once the search that
   * submit started has completed with a response, after the render that
   * re-enables the Search button and shows the outcome. A submit from the
   * focused Search button handed focus to the page heading while the search
   * was pending, and a disabled button that still held focus would drop it
   * to the document body, so focus moves only when it has stayed on that
   * heading since such a submit, or is on the body, on nothing, or on that
   * element, and only to an element that is still in the document and
   * enabled; anything else the guest focused meanwhile keeps focus, and so
   * does the heading once focus has left it and the guest has brought it
   * back. The hand-off ends here either way. A cancelled search never calls
   * this, and a search that no submit started, such as one run by
   * back/forward navigation or a deep link, passes `null` and never moves
   * focus.
   *
   * @param target - The element recorded at the submit that started the
   *   search, or `null` when no submit started it or nothing was focused.
   * @param handOff - That submit's hand-off of focus to the page heading, or
   *   `null` when it handed none over.
   */
  private restoreFocusAfterSearch(target: HTMLElement | null, handOff: HeadingHandOff | null): void {
    // Read as the search completes, before the render below, which no guest
    // action can precede.
    const holder = handOff !== null && handOff.holds() ? handOff.heading : null;
    handOff?.end();
    if (target === null) {
      return;
    }

    afterNextRender(
      () => {
        const active = this.document.activeElement;
        const dropped = active === null || active === this.document.body || active === target || active === holder;
        if (dropped && target.isConnected && !target.matches(':disabled')) {
          target.focus();
        }
      },
      { injector: this.injector },
    );
  }

  /**
   * Hands focus to `heading` from the focused Search button, before the
   * pending state disables that button, and starts watching whether it stays
   * there. The focus itself scrolls nothing; the heading is then scrolled
   * only as far as it takes to show it, its focus ring included, which is not
   * at all when it shows already, so a form scrolled past its heading still
   * shows where focus is. The first `focusout` from the heading, for another
   * element or out of the document, ends the hand-off, after which focus
   * back on it is the guest's own; so does the hand-off's `end`, and either
   * removes the listener.
   *
   * @param heading - The page heading, which takes focus.
   * @returns The hand-off, or `null` when the heading did not take focus.
   */
  private handFocusToHeading(heading: HTMLElement): HeadingHandOff | null {
    heading.focus({ preventScroll: true });
    if (this.document.activeElement !== heading) {
      return null;
    }
    heading.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    let holds = true;
    const end = (): void => {
      holds = false;
      heading.removeEventListener('focusout', end);
    };
    heading.addEventListener('focusout', end);
    return { heading, holds: () => holds, end };
  }
}
