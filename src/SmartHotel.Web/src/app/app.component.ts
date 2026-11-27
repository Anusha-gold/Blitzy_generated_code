import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LoadingIndicatorComponent } from './shared/loading-indicator/loading-indicator.component';
import { NavBarComponent } from './shared/nav-bar/nav-bar.component';

/**
 * The application shell: the navigation bar, the global loading indicator and
 * the routed page inside the single `main` landmark.
 *
 * `main.ts` bootstraps it only after `/config.json` has loaded and passed
 * validation, so it replaces the static `boot-loading` element that
 * `index.html` renders inside `<app-root>`.
 *
 * The shell holds no state of its own. The session and the loading state live
 * in `AuthService` and `LoadingService`, and the child components read them
 * as signals, so OnPush change detection keeps every part current.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, NavBarComponent, LoadingIndicatorComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {}
