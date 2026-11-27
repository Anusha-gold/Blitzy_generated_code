'use strict';

/*
 * The local Angular CLI builder behind `ng test`. It runs Playwright Test, the
 * web app's only test framework, with the workspace's Playwright configuration
 * and reports Playwright's exit code to the CLI. It replaces Karma, the default
 * `ng test` runner, and playwright-ng-schematics, which supports only Angular 18
 * and later.
 *
 * Architect loads this file with `require()`, so it stays CommonJS: neither
 * this folder's package.json nor the workspace's declares `"type": "module"`.
 */

const { spawn } = require('node:child_process');
const path = require('node:path');
const { createBuilder } = require('@angular-devkit/architect');

/**
 * Runs `playwright test --config <config>` in the workspace and reports whether it passed.
 *
 * @param {{ config?: string }} options The validated builder options. `config` is the
 *   Playwright configuration path, relative to the workspace root.
 * @param {import('@angular-devkit/architect').BuilderContext} context The Architect
 *   context, which supplies `workspaceRoot` and `logger`.
 * @returns {Promise<{ success: boolean, error?: string }>} Resolves `success: true` only
 *   when Playwright exits with code 0. It never rejects.
 */
function runPlaywright(options, context) {
  return new Promise((resolve) => {
    const root = context.workspaceRoot;

    let cli;
    try {
      cli = require.resolve('@playwright/test/cli', { paths: [root] });
    } catch (err) {
      const message =
        `Cannot resolve @playwright/test/cli from ${root}. ` +
        `Run "npm ci" in the workspace first. ${err.message}`;
      context.logger.error(message);
      resolve({ success: false, error: message });
      return;
    }

    const config = path.resolve(root, options.config || 'playwright.config.ts');
    context.logger.info(`Running Playwright Test with ${config}`);

    // A child that fails after starting can emit 'error' and then 'close', so
    // only the first outcome is reported.
    let settled = false;
    const settle = (output) => {
      if (!settled) {
        settled = true;
        resolve(output);
      }
    };
    const failToStart = (err) => {
      context.logger.error(`Failed to start Playwright: ${err.message}`);
      settle({ success: false, error: err.message });
    };

    let child;
    try {
      // No shell, so every argument reaches Node verbatim on every platform and
      // paths with spaces need no quoting. The environment passes through
      // unchanged, so E2E_REUSE_STACK, E2E_BASE_URL and E2E_API_URL reach the
      // configuration and global setup as the caller set them.
      child = spawn(process.execPath, [cli, 'test', '--config', config], {
        cwd: root,
        stdio: 'inherit',
        env: process.env,
      });
    } catch (err) {
      // spawn reports most start failures through 'error', but throws for some.
      failToStart(err);
      return;
    }

    child.on('error', failToStart);
    child.on('close', (code, signal) => {
      if (signal !== null) {
        const message = `Playwright was terminated by signal ${signal}`;
        context.logger.error(message);
        settle({ success: false, error: message });
        return;
      }
      settle({ success: code === 0 });
    });
  });
}

/**
 * The Angular Architect builder registered as `test` in `builders.json`, which
 * `angular.json` binds to the `test` target, so `ng test` runs Playwright Test.
 */
module.exports = createBuilder(runPlaywright);
