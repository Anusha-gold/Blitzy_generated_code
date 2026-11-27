/**
 * Entry point of the SmartHotel SPA.
 *
 * It loads and validates `/config.json` first and only then bootstraps
 * Angular, providing the result as `API_CONFIG`. When the configuration cannot
 * be fetched or is rejected, Angular is never bootstrapped: the static
 * `boot-loading` element from `index.html` is replaced with a static
 * `boot-error` alert instead. The same alert covers a bootstrap that fails.
 */
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
 * where the global styles expect pre-bootstrap messages. Calling it again
 * changes nothing.
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
 * Loads the runtime configuration, then bootstraps the application with it.
 *
 * Either failure ends in the boot-error state. The configuration failure is
 * logged as a fixed message only, because the rejection can describe values
 * from `/config.json`.
 */
async function start(): Promise<void> {
  let config: ApiConfig;
  try {
    config = await loadRuntimeConfig();
  } catch {
    console.error('SmartHotel: runtime configuration is unavailable or invalid.');
    showBootError();
    return;
  }

  try {
    await bootstrapApplication(AppComponent, {
      ...appConfig,
      providers: [...appConfig.providers, { provide: API_CONFIG, useValue: config }],
    });
  } catch (error: unknown) {
    console.error('SmartHotel: application bootstrap failed.', error);
    showBootError();
  }
}

void start();
