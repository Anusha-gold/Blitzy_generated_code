import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NgIf } from '@angular/common';
import { LoadingService } from '../../core/http/loading.service';

/**
 * The app-wide request status, a slot between the nav bar and the page. The
 * host keeps the banner's height even while idle, so the banner never covers
 * the navigation and the page never shifts when loading starts or ends.
 *
 * The indicator (`role="progressbar"`, `data-testid="loading-indicator"`) is
 * rendered only while {@link LoadingService.isLoading} is `true` and is absent
 * from the DOM otherwise. A visually hidden polite status region stays rendered
 * throughout and reads "Loading…" or "Loading complete.", so screen readers
 * announce the start and end of loading without moving focus.
 *
 * `loadingInterceptor` drives that state for every `HttpClient` request, so
 * the component only reads it and never changes it. Reading a signal in the
 * template keeps OnPush change detection current without subscriptions.
 *
 * It does not cover the `/config.json` fetch that runs before Angular
 * bootstraps; `src/index.html` shows the static `boot-loading` element for that.
 */
@Component({
  selector: 'app-loading-indicator',
  standalone: true,
  imports: [NgIf],
  templateUrl: './loading-indicator.component.html',
  styleUrl: './loading-indicator.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoadingIndicatorComponent {
  /** The shared in-flight request state the template reads. */
  protected readonly loading = inject(LoadingService);
}
