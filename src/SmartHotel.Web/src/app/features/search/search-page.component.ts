import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, DOCUMENT, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { EMPTY, Observable, Subject, catchError, finalize, map, merge, switchMap, tap } from 'rxjs';

import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { HotelSearchQuery, HotelSummary } from '../../core/api/models';
import { UiError, fieldErrorIds, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

/**
 * Reads the URL's guest-count text as the number the form's number input
 * shows: `null`, an empty input, when the text is blank or not a finite
 * number. It is for display only; the search sends the text itself, so the
 * server's binding, not this reading, decides whether it is valid.
 */
function guestsForDisplay(text: string): number | null {
  if (text.trim() === '') {
    return null;
  }
  const n = Number(text);
  return Number.isFinite(n) ? n : null;
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
 * - idle on bare `/search` (the nav and brand links and the `''` and `**`
 *   redirects): the form only, with no results, message or error;
 * - loading: the app-wide indicator, driven by the loading interceptor, while
 *   the submit button is disabled through {@link SearchPageComponent.pending};
 * - results in server order (lowest nightly price, then name), or the
 *   empty-result message naming the city that was actually searched; both
 *   read the completed search's criteria, never the live form;
 * - an inline error through `ErrorMessageComponent`, mapped by `toUiError`:
 *   the server's 400 problem with its field errors, or "The service is
 *   unreachable." for status 0.
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
   * The search criteria as entered. The date inputs yield raw `yyyy-MM-dd`
   * strings, and the number input yields `null` when it is emptied.
   */
  protected readonly form = this.fb.group({
    city: '',
    checkIn: '',
    checkOut: '',
    guests: this.fb.control<number | null>(1),
  });

  /** `true` while this page's own search request is in flight. */
  protected readonly pending = signal(false);

  /** The failed search's error, or `null` when the last search did not fail. */
  protected readonly error = signal<UiError | null>(null);

  /** Prefix of the ids the inline error gives its field messages. */
  protected readonly errorIdPrefix = 'search-error';

  /**
   * The ids of the server's messages per rejected field (`city`, `checkIn`,
   * `checkOut`, `guests`), which the inputs reference through
   * `aria-describedby` and mark with `aria-invalid`; empty while there is no
   * error, so each new search clears them.
   */
  protected readonly fieldErrors = computed(() => fieldErrorIds(this.error(), this.errorIdPrefix));

  /** The completed search's hotels in server order; `null` until one completes. */
  protected readonly results = signal<HotelSummary[] | null>(null);

  /** The criteria of the last search that completed, which the results belong to. */
  protected readonly searched = signal<HotelSearchQuery | null>(null);

  private readonly rerun$ = new Subject<void>();

  constructor() {
    merge(this.route.queryParamMap, this.rerun$.pipe(map(() => this.route.snapshot.queryParamMap)))
      .pipe(
        switchMap((params) => {
          // The record belongs to the search this emission starts: taking it
          // here means a cancelled search, or the idle state, cannot leave it
          // for a later back/forward search to act on.
          const target = this.focusedAtSubmit;
          this.focusedAtSubmit = null;
          // `complete` follows a response (`run` turns an error into a
          // completion) but never an unsubscribe, so a search that a newer
          // one cancels never moves focus.
          return this.run(params).pipe(tap({ complete: () => this.restoreFocusAfterSearch(target) }));
        }),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /**
   * Writes the form's criteria into the URL, which runs the search. An emptied
   * guest count is left out, and the server then uses 1. When the criteria
   * equal the current URL, the router would not emit, so the current query is
   * replayed instead. The element focused at submit, normally the Search
   * button that the pending state disables, is recorded for the search this
   * submit starts, so focus can return to it once that search completes with
   * a response; a search cancelled by a newer one never moves focus.
   */
  protected submit(): void {
    const focused = this.document.activeElement;
    this.focusedAtSubmit = focused instanceof HTMLElement && focused !== this.document.body ? focused : null;

    const { city, checkIn, checkOut, guests } = this.form.getRawValue();
    const queryParams: Record<string, string | number> = { city, checkIn, checkOut };
    if (guests !== null) {
      queryParams['guests'] = guests;
    }

    const current = this.route.snapshot.queryParamMap;
    const same =
      current.get('city') === city &&
      current.get('checkIn') === checkIn &&
      current.get('checkOut') === checkOut &&
      current.get('guests') === (guests === null ? null : String(guests));
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
   * query names none of the four criteria. `city`, `checkIn`, `checkOut` and a
   * present `guests` are passed on exactly as the URL holds them, an empty
   * string included; an absent `guests` is sent as 1, the server's own
   * default. The server alone decides their validity. The form is refilled
   * from the same values, its guest count with a numeric reading of the text
   * for display only. The error handling and `finalize` sit inside this inner
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

    this.form.patchValue({ city, checkIn, checkOut, guests: guestsForDisplay(guests) });
    this.error.set(null);
    this.results.set(null);
    this.pending.set(true);

    return this.hotels.search(q).pipe(
      tap((list) => {
        this.searched.set(q);
        this.results.set(list);
      }),
      catchError((err: HttpErrorResponse) => {
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
   * re-enables the Search button and shows the outcome. Disabling the focused
   * button while the search is pending can drop focus to the document body,
   * so focus moves only when it is still on the body, on nothing, or on that
   * element, and only to an element that is still in the document and
   * enabled; anything else the guest focused meanwhile keeps focus. A
   * cancelled search never calls this, and a search that no submit started,
   * such as one run by back/forward navigation or a deep link, passes `null`
   * and never moves focus.
   *
   * @param target - The element recorded at the submit that started the
   *   search, or `null` when no submit started it or nothing was focused.
   */
  private restoreFocusAfterSearch(target: HTMLElement | null): void {
    if (target === null) {
      return;
    }

    afterNextRender(
      () => {
        const active = this.document.activeElement;
        const dropped = active === null || active === this.document.body || active === target;
        if (dropped && target.isConnected && !target.matches(':disabled')) {
          target.focus();
        }
      },
      { injector: this.injector },
    );
  }
}
