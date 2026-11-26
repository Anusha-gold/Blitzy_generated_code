import { Injectable, computed, signal } from '@angular/core';

/**
 * App-wide counter of in-flight `HttpClient` requests.
 *
 * `loadingInterceptor` is its only writer: it calls {@link LoadingService.begin}
 * when a request starts and {@link LoadingService.end} when it completes, errors
 * or is cancelled. `LoadingIndicatorComponent` reads {@link LoadingService.isLoading}.
 * The state changes synchronously, with no debounce or delay, so even a short
 * request shows the indicator and the last completion hides it at once.
 */
@Injectable({ providedIn: 'root' })
export class LoadingService {
  private readonly _pending = signal(0);

  /** Number of `HttpClient` requests currently in flight; never negative. */
  readonly pending = this._pending.asReadonly();

  /** `true` while at least one `HttpClient` request is in flight. */
  readonly isLoading = computed(() => this._pending() > 0);

  /** Records that one request has started. */
  begin(): void {
    this._pending.update((n) => n + 1);
  }

  /**
   * Records that one request has finished. The counter clamps at 0, so an
   * unmatched call cannot drive it negative and hide a later request.
   */
  end(): void {
    this._pending.update((n) => Math.max(0, n - 1));
  }
}
