import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CurrencyPipe, NgFor, NgIf } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { EMPTY, Observable, Subject, catchError, finalize, map, merge, switchMap, tap } from 'rxjs';

import { HotelsApiService } from '../../core/api/hotels-api.service';
import type { HotelSearchQuery, HotelSummary } from '../../core/api/models';
import { UiError, toUiError } from '../../core/http/problem-details';
import { ErrorMessageComponent } from '../../shared/error-message/error-message.component';

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

  /** The completed search's hotels in server order; `null` until one completes. */
  protected readonly results = signal<HotelSummary[] | null>(null);

  /** The criteria of the last search that completed, which the results belong to. */
  protected readonly searched = signal<HotelSearchQuery | null>(null);

  private readonly rerun$ = new Subject<void>();

  constructor() {
    merge(this.route.queryParamMap, this.rerun$.pipe(map(() => this.route.snapshot.queryParamMap)))
      .pipe(
        switchMap((params) => this.run(params)),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  /**
   * Writes the form's criteria into the URL, which runs the search. An emptied
   * guest count is left out, and the server then uses 1. When the criteria
   * equal the current URL, the router would not emit, so the current query is
   * replayed instead.
   */
  protected submit(): void {
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
   * query names no criteria. Values are passed on exactly as the URL holds
   * them, an empty string included, so the server decides their validity. The
   * error handling and `finalize` sit inside this inner observable, so a failed
   * search never ends the outer stream and a cancelled one still clears
   * `pending`.
   */
  private run(params: ParamMap): Observable<HotelSummary[]> {
    // `has`, not truthiness: an empty value counts as present, so a blank
    // submission still reaches the server and its 400 shows inline.
    if (!params.has('city') && !params.has('checkIn') && !params.has('checkOut')) {
      this.results.set(null);
      this.searched.set(null);
      this.error.set(null);
      this.pending.set(false);
      return EMPTY;
    }

    const city = params.get('city') ?? '';
    const checkIn = params.get('checkIn') ?? '';
    const checkOut = params.get('checkOut') ?? '';
    // 1 is the server's own default for an omitted count. Any other value is
    // passed on unchecked: a non-numeric one goes out as `NaN` and gets a 400.
    const guests = params.has('guests') ? Number(params.get('guests')) : 1;
    const q: HotelSearchQuery = { city, checkIn, checkOut, guests };

    this.form.patchValue({ city, checkIn, checkOut, guests });
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
}
