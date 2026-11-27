import { defineConfig, devices } from '@playwright/test';

import { resolveStackTarget } from './e2e/support/stack-target';

const target = resolveStackTarget();

// Playwright loads this file again in every worker process, and workers
// inherit the main process's environment. Writing the resolved pair back
// means specs, helpers and workers all read the target resolved here, and
// re-resolution in a worker yields the same pair because it is deterministic.
process.env['E2E_BASE_URL'] = target.baseUrl;
process.env['E2E_API_URL'] = target.apiUrl;

/**
 * Playwright Test configuration for the web end-to-end suite, run by `ng test`
 * through the in-repo `tools/playwright-builder` and by `npx playwright test`.
 *
 * Target: `e2e/global-setup.ts` builds the dedicated `smarthotel-acceptance`
 * Compose stack before the first spec, and every spec targets it at
 * `http://localhost:14200` (web, `use.baseURL`) and
 * `http://localhost:18080/api` (API, `E2E_API_URL`). With `E2E_REUSE_STACK=1`
 * the run instead targets an already running stack at `E2E_BASE_URL` and
 * `E2E_API_URL`; such runs are for local iteration only.
 *
 * Browser: a single Chromium project, the browser Playwright 1.60.0 bundles.
 *
 * Execution is serial (`fullyParallel: false`, `workers: 1`): every spec
 * shares one stateful stack, and bookings made by one test change the
 * availability counts another reads. `retries: 0` keeps a failure from being
 * masked by a later passing attempt.
 *
 * The `list` reporter prints to the console and exits with Playwright's code;
 * the HTML reporter would serve its report after a failure and leave an
 * unattended run waiting. No `webServer` is configured, because global setup,
 * not a dev server, starts the stack, and no `globalTimeout` is set, because
 * the image build in global setup can take several minutes.
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: target.baseUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
