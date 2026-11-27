/**
 * Entry point of the SmartHotel SPA.
 *
 * It loads and validates `/config.json` first and only then bootstraps
 * Angular, providing the result as `API_CONFIG`. When the configuration cannot
 * be fetched or is rejected, Angular is never bootstrapped: the static
 * `boot-loading` element from `index.html` is replaced with a static
 * `boot-error` alert instead. The same alert covers a bootstrap that fails.
 *
 * Neither failure writes to the browser console: the boot-error alert is the
 * whole report, and no raw startup error is printed, by this file or by
 * Angular's `ErrorHandler`. Configuration validation never includes the
 * rejected values, and a bootstrap error can carry provider or initialization
 * details.
 */
import { ErrorHandler } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';
import { API_CONFIG, loadRuntimeConfig, type ApiConfig } from './app/core/config/runtime-config';

const BOOT_ERROR_MESSAGE =
  'SmartHotel could not start: its configuration is unavailable. Reload to try again.';

/**
 * Shows the static boot-error alert in place of the boot-loading element.
 *
 * The alert is built with DOM APIs and `textContent` only, never parsed HTML.
 * It stays a direct child of `<app-root>` where that element exists, which is
 * where the host page's inline styles in `index.html` expect pre-bootstrap
 * messages. Calling it again changes nothing.
 */
function showBootError(): void {
  if (document.querySelector('[data-testid="boot-error"]')) {
    return;
  }

  const message = document.createElement('p');
  message.setAttribute('role', 'alert');
  message.setAttribute('data-testid', 'boot-error');
  message.textContent = BOOT_ERROR_MESSAGE;

  const bootLoading = document.querySelector('[data-testid="boot-loading"]');
  if (bootLoading) {
    bootLoading.replaceWith(message);
    return;
  }

  // A bootstrap that failed after Angular claimed the host element has already
  // cleared the boot-loading element, and may have left partial output there.
  const appRoot = document.querySelector('app-root');
  if (appRoot) {
    appRoot.replaceChildren(message);
    return;
  }

  document.body.append(message);
}

/**
 * The application's `ErrorHandler`, which keeps the browser console silent when
 * bootstrap fails.
 *
 * Angular reports a bootstrap failure to its `ErrorHandler` before it rejects,
 * and the default handler prints the raw error. While bootstrap is pending this
 * handler holds every reported error in memory instead. Once
 * {@link StartupErrorHandler.bootstrapSucceeded} is called it hands the held
 * errors, and every later one, to the default handling. Once
 * {@link StartupErrorHandler.bootstrapFailed} is called it drops them and stays
 * silent, because the boot-error alert reports that outcome.
 */
class StartupErrorHandler extends ErrorHandler {
  private state: 'pending' | 'running' | 'failed' = 'pending';
  private readonly held: unknown[] = [];

  /** Reports, holds or drops `error` according to the bootstrap outcome. */
  override handleError(error: unknown): void {
    if (this.state === 'running') {
      super.handleError(error);
    } else if (this.state === 'pending') {
      this.held.push(error);
    }
  }

  /** Reports the held errors and switches to Angular's default handling. */
  bootstrapSucceeded(): void {
    if (this.state !== 'pending') {
      return;
    }
    this.state = 'running';
    for (const error of this.held.splice(0)) {
      super.handleError(error);
    }
  }

  /** Discards the held errors and ignores every later one. */
  bootstrapFailed(): void {
    if (this.state !== 'pending') {
      return;
    }
    this.state = 'failed';
    this.held.length = 0;
  }
}

/**
 * Loads the runtime configuration, then bootstraps the application with it as
 * `API_CONFIG`.
 *
 * Either failure shows the boot-error alert instead; when the configuration
 * fails, Angular is not bootstrapped. The file header explains why neither
 * failure is printed.
 */
async function start(): Promise<void> {
  let config: ApiConfig;
  try {
    config = await loadRuntimeConfig();
  } catch {
    showBootError();
    return;
  }

  const errorHandler = new StartupErrorHandler();
  try {
    await bootstrapApplication(AppComponent, {
      ...appConfig,
      providers: [
        ...appConfig.providers,
        { provide: API_CONFIG, useValue: config },
        { provide: ErrorHandler, useValue: errorHandler },
      ],
    });
  } catch {
    errorHandler.bootstrapFailed();
    showBootError();
    return;
  }
  errorHandler.bootstrapSucceeded();
}

void start();
