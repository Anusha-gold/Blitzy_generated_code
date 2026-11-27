import { defineConfig, devices } from '@playwright/test';

import { resolveStackTarget } from './e2e/support/stack-target';

// Playwright's debug logger (DEBUG) and Node's (NODE_DEBUG=http) print request
// headers, including the bearer tokens the specs send. In a direct
// `npx playwright test` both are already switched on before this file loads:
// Node reads NODE_DEBUG when the process starts, and Playwright reads DEBUG
// when it loads, so clearing either here cannot turn its logger off in this
// process, and the run is refused instead. `ng test` removes both before it
// starts Playwright. The message names the variables that are set, never
// their values.
const debugVariablesSet = ['DEBUG', 'NODE_DEBUG'].filter(
  (name) => (process.env[name] ?? '').trim() !== '',
);
if (debugVariablesSet.length > 0) {
  const names = debugVariablesSet.join(' and ');
  throw new Error(
    `Debug logging is on (${names} set), and it prints request headers, ` +
      `including bearer tokens. Unset ${names}, or run the suite with \`ng test\`, ` +
      'which turns debug logging off.',
  );
}

const target = resolveStackTarget();

// Playwright loads this file again in every worker process, and workers
// inherit the main process's environment. Writing the resolved pair back
// means specs, helpers and workers all read the target resolved here, and
// re-resolution in a worker yields the same pair because it is deterministic.
process.env['E2E_BASE_URL'] = target.baseUrl;
process.env['E2E_API_URL'] = target.apiUrl;

// Stops Playwright from saving a page snapshot (DOM text, including the
// signed-in guest's email) with each failure. Workers inherit it, and
// re-loading this file in a worker sets it again.
process.env['PLAYWRIGHT_NO_COPY_PROMPT'] = '1';

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
 *
 * Failures are reported by the `list` reporter's console output only. No
 * trace, screenshot, video or page snapshot is retained, because each would
 * capture credentials, bearer tokens or the guest's email. Playwright still
 * writes `error-context.md`, which embeds an ARIA snapshot of page text, for
 * a failed locator assertion; `preserveOutput: 'never'` deletes each test's
 * output directory when the test ends, so the file the console names under
 * "Error Context" is not kept.
 */
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  preserveOutput: 'never',
  use: {
    baseURL: target.baseUrl,
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
