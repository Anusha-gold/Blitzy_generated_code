import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NgIf } from '@angular/common';
import { LoadingService } from '../../core/http/loading.service';

/**
 * The app-wide request indicator (`role="progressbar"`,
 * `data-testid="loading-indicator"`), rendered only while
 * {@link LoadingService.isLoading} is `true` and absent from the DOM otherwise.
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
