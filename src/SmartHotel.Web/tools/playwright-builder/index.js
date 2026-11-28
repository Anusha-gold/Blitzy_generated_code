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

/** The configuration used when the `config` option is omitted. */
const DEFAULT_CONFIG = 'playwright.config.ts';

/** The usage error for a `config` option that cannot name a configuration file. */
const CONFIG_USAGE_ERROR =
  'Invalid "config" option: it must be a non-blank path to a Playwright configuration ' +
  'file, without control or line-separator characters. Omit --config to use ' +
  `${DEFAULT_CONFIG}.`;

/**
 * Tells whether a `config` option value can name a configuration file: a
 * string that is not blank and holds no control characters (U+0000 to U+001F
 * and U+007F to U+009F) and no line or paragraph separator (U+2028, U+2029).
 *
 * @param {unknown} value The option value as Architect passed it.
 * @returns {boolean} `true` when the value can be resolved and passed to Playwright.
 */
function isUsableConfigPath(value) {
  if (typeof value !== 'string' || value.trim() === '') {
    return false;
  }
  for (const character of value) {
    const code = character.codePointAt(0);
    if (code < 0x20 || (code >= 0x7f && code <= 0x9f) || code === 0x2028 || code === 0x2029) {
      return false;
    }
  }
  return true;
}

/**
 * Environment variables, upper-cased, that switch on a debug logger which
 * prints request headers, including the bearer tokens the specs send.
 * Playwright's `DEBUG` logger prints them on its `pw:api` channel, and
 * `DEBUG_FILE` copies that output to a file. Node's `NODE_DEBUG=http` prints
 * each outgoing request's options, headers included.
 */
const DEBUG_VARIABLES = ['DEBUG', 'DEBUG_FILE', 'NODE_DEBUG'];

/**
 * Copies an environment without the debug-logger variables in `DEBUG_VARIABLES`.
 *
 * Names are compared case-insensitively, because Windows environment names
 * are, so `debug` or `Debug` cannot reach Playwright either. Every other
 * variable passes unchanged.
 *
 * @param {NodeJS.ProcessEnv} source The environment to copy, normally `process.env`.
 * @returns {{ env: NodeJS.ProcessEnv, debugDropped: boolean }} The copy, and
 *   whether a debug-logger variable with a non-blank value was left out.
 */
function withoutDebugLogging(source) {
  const env = {};
  let debugDropped = false;
  for (const [name, value] of Object.entries(source)) {
    if (DEBUG_VARIABLES.includes(name.toUpperCase())) {
      debugDropped = debugDropped || (value !== undefined && value.trim() !== '');
      continue;
    }
    env[name] = value;
  }
  return { env, debugDropped };
}

/** The longest text, in UTF-16 code units, that `encodeForLog` renders. */
const MAX_LOGGED_LENGTH = 512;

/** The shape of a value printed bare in a message: a Node error code or a signal name. */
const SAFE_TOKEN = /^[A-Z][A-Z0-9_]{0,31}$/;

/**
 * Renders a value, such as a file path, for a log line, so that it can neither
 * forge a line nor send control characters to the terminal.
 *
 * `JSON.stringify` quotes the value and escapes `"`, `\` and U+0000 to U+001F.
 * The C1 controls U+007F to U+009F and the separators U+2028 and U+2029 are
 * escaped as well. A value longer than `MAX_LOGGED_LENGTH` is cut there and
 * marked as truncated.
 *
 * @param {string} value The value to render.
 * @returns {string} The quoted, escaped and bounded value.
 */
function encodeForLog(value) {
  const shown = value.length > MAX_LOGGED_LENGTH ? value.slice(0, MAX_LOGGED_LENGTH) : value;
  const encoded = JSON.stringify(shown).replace(
    /[\u007f-\u009f\u2028\u2029]/g,
    (character) => `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`,
  );
  return shown === value ? encoded : `${encoded} (truncated)`;
}

/**
 * Returns a value only when it has the shape of a Node error code or a signal
 * name, such as `ENOENT`, `MODULE_NOT_FOUND` or `SIGTERM`, so it can be
 * printed as it is.
 *
 * @param {unknown} value The candidate, for example an error's `code`.
 * @returns {string | undefined} The value, or `undefined` for any other value.
 */
function asSafeToken(value) {
  return typeof value === 'string' && SAFE_TOKEN.test(value) ? value : undefined;
}

/**
 * Describes a caught error by its Node error code alone. The error's message
 * is never used, because it can quote paths, module stacks or other input.
 *
 * @param {unknown} err The caught or emitted error.
 * @returns {string} ` (<code>)` when the error carries a safe code, otherwise
 *   an empty string.
 */
function describeErrorCode(err) {
  const code = asSafeToken(err !== null && typeof err === 'object' ? err.code : undefined);
  return code === undefined ? '' : ` (${code})`;
}

/**
 * Writes a line through the Architect logger. The line never decides the
 * run's outcome, so a logger that throws can neither reject the builder's
 * promise nor stop it from waiting for Playwright.
 *
 * @param {import('@angular-devkit/architect').BuilderContext} context The Architect
 *   context, which supplies `logger`.
 * @param {'info' | 'warn'} level The logger method to call.
 * @param {string} message The line, built only from fixed text and encoded values.
 */
function logSafely(context, level, message) {
  try {
    context.logger[level](message);
  } catch {
    // Deliberately ignored: the line is informational, and the run's outcome
    // is decided only by Playwright's exit, which is still awaited.
  }
}

/**
 * Signals that stop `ng test`. On POSIX each is forwarded to Playwright's
 * process group (`SIGNALS_PROCESS_GROUP`), so stopping ng stops the run
 * instead of leaving Playwright running on its own. SIGQUIT, a terminal's
 * Ctrl+\, is among them because on POSIX it reaches only ng's process group,
 * which Playwright is not part of. On Windows none of them is forwarded
 * (`STOP_SIGNALS`).
 */
const FORWARDED_SIGNALS = ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT'];

/**
 * Whether Playwright runs in a process group of its own, which a stop signals
 * as a whole: on POSIX only. There the Playwright process is spawned detached,
 * so it leads a new process group (and session) that its workers and the
 * processes they start join. A `docker compose` command global setup runs is
 * not among them: setup runs each in a process group of its own and stops it
 * itself before Playwright exits. Windows has
 * no process groups to signal, and Node's `kill` there terminates a process
 * forcefully whatever the signal, so a forwarded signal would end Playwright
 * before global setup could stop a Compose command it runs. The stop signals
 * ng can receive there are console events: Ctrl+C (SIGINT), Ctrl+Break
 * (SIGBREAK) and closing the console window (SIGHUP). Each already reaches
 * every process attached to the console, and Playwright, spawned without
 * `detached`, shares ng's console, so ng forwards none of them; only the
 * Architect teardown, which is not a console event, ends the Playwright
 * process there.
 */
const SIGNALS_PROCESS_GROUP = process.platform !== 'win32';

/**
 * The signals ng listens for while Playwright runs. On POSIX they are
 * `FORWARDED_SIGNALS`, each forwarded to Playwright's process group. On
 * Windows SIGBREAK, a console's Ctrl+Break, is added, because without a
 * listener Node ends ng on it at once, and ending ng ends the Playwright
 * process it started, before global setup has stopped a Compose command it
 * runs. There each listener only records the signal, which Playwright
 * receives from the console itself.
 */
const STOP_SIGNALS = SIGNALS_PROCESS_GROUP ? FORWARDED_SIGNALS : [...FORWARDED_SIGNALS, 'SIGBREAK'];

/** How often, in milliseconds, a stopped run's process group is checked for members. */
const GROUP_POLL_INTERVAL_MS = 100;

/** How long, in milliseconds, a stopped run's remaining processes get to exit before SIGKILL. */
const GROUP_EXIT_TIMEOUT_MS = 10_000;

/**
 * How long, in milliseconds, the group gets to empty after SIGKILL before the
 * run fails with the group reported still running.
 */
const GROUP_KILL_GRACE_MS = 1_000;

/**
 * Tells whether a spawned child process started and has not exited yet.
 *
 * @param {import('node:child_process').ChildProcess} child The child process.
 * @returns {boolean} `true` while the child is running.
 */
function isRunning(child) {
  return child.pid !== undefined && child.exitCode === null && child.signalCode === null;
}

/**
 * Sends a signal to a child process. `kill` throws for a signal the platform
 * cannot send, such as SIGHUP on Windows; SIGTERM, which every platform
 * supports, is then sent in its place. A failure Node detects later arrives
 * through the child's 'error' event. On Windows `kill` terminates the process
 * forcefully whatever the signal, so there only the Architect teardown's
 * SIGTERM is sent (`SIGNALS_PROCESS_GROUP`).
 *
 * @param {import('node:child_process').ChildProcess} child The child process.
 * @param {string} signal The signal name, one of `FORWARDED_SIGNALS`.
 * @returns {boolean} `false` when no signal could be sent.
 */
function signalChild(child, signal) {
  try {
    child.kill(signal);
  } catch {
    return signal === 'SIGTERM' ? false : signalChild(child, 'SIGTERM');
  }
  return true;
}

/**
 * Sends a signal to a run: on POSIX to every process in Playwright's process
 * group, which reaches the CLI, its workers and the processes they start, but
 * not a `docker compose` command global setup runs in a group of its own and
 * stops itself; on Windows to the Playwright process alone, which that
 * terminates, so only the Architect teardown signals a run there. When the
 * group cannot be signalled, for example because it is already empty, the
 * Playwright process is signalled instead.
 *
 * @param {import('node:child_process').ChildProcess} child The Playwright
 *   process, which on POSIX leads the group.
 * @param {string} signal The signal name, one of `FORWARDED_SIGNALS`.
 * @returns {boolean} `false` when no signal could be sent.
 */
function signalRun(child, signal) {
  if (SIGNALS_PROCESS_GROUP) {
    try {
      // A negative PID addresses the process group with that ID.
      process.kill(-child.pid, signal);
      return true;
    } catch {
      // Deliberately ignored: the Playwright process is signalled below.
    }
  }
  return signalChild(child, signal);
}

/**
 * Tells whether a process group still has members. Signal 0 tests for the
 * group without signalling it: ESRCH means no process is left, while any
 * other error, such as EPERM, means a member exists that cannot be signalled.
 *
 * @param {number} pgid The process group ID.
 * @returns {boolean} `true` while any process remains in the group.
 */
function groupHasMembers(pgid) {
  try {
    process.kill(-pgid, 0);
    return true;
  } catch (err) {
    return !(err !== null && typeof err === 'object' && err.code === 'ESRCH');
  }
}

/**
 * Waits until a process group has no members, checking every
 * `GROUP_POLL_INTERVAL_MS`.
 *
 * @param {number} pgid The process group ID.
 * @param {number} timeoutMs The longest wait, in milliseconds.
 * @returns {Promise<boolean>} Resolves `true` once the group is empty, or
 *   `false` when members remain after `timeoutMs`. It never rejects.
 */
function waitForEmptyGroup(pgid, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const check = () => {
      if (!groupHasMembers(pgid)) {
        resolve(true);
      } else if (Date.now() >= deadline) {
        resolve(false);
      } else {
        setTimeout(check, GROUP_POLL_INTERVAL_MS);
      }
    };
    check();
  });
}

/**
 * Gives the processes still in Playwright's group once the CLI has exited
 * after a stop, such as a test worker or a browser, a bounded, best-effort
 * drain. A `docker compose` command global setup started is never among
 * them: it runs in a process group of its own, which setup stops before the
 * Playwright process exits. Members left after `GROUP_EXIT_TIMEOUT_MS`
 * receive SIGKILL, and the wait then ends after at most `GROUP_KILL_GRACE_MS`,
 * whatever remains; the result says whether the group emptied, so the caller
 * reports a group that did not instead of assuming it gone. The builder holds
 * no acceptance-run lock and never touches global setup's barrier file. The
 * drain starts only once the Playwright process has exited, and global setup,
 * which runs in that process, first stops a Compose command it had in flight
 * and deletes the file once that command's end is confirmed. After a stop,
 * only a setup killed with SIGKILL, or a Compose command whose end setup
 * could not confirm, such as one with a process that survived SIGKILL,
 * leaves the file, which then keeps a fresh re-run out until the project has
 * been checked.
 *
 * @param {number} pgid The process group ID, which is the Playwright process's PID.
 * @param {import('@angular-devkit/architect').BuilderContext} context The Architect
 *   context, which supplies `logger`.
 * @returns {Promise<boolean>} Resolves `true` once the group is empty, within
 *   `GROUP_EXIT_TIMEOUT_MS` or after SIGKILL within `GROUP_KILL_GRACE_MS`, and
 *   `false` when members remain. It never rejects.
 */
async function drainProcessGroup(pgid, context) {
  if (await waitForEmptyGroup(pgid, GROUP_EXIT_TIMEOUT_MS)) {
    return true;
  }
  logSafely(
    context,
    'warn',
    `Processes Playwright started were still running ${GROUP_EXIT_TIMEOUT_MS / 1000} s ` +
      'after the stop. Sending them SIGKILL.',
  );
  try {
    process.kill(-pgid, 'SIGKILL');
  } catch {
    // Deliberately ignored: the group emptied meanwhile, or a member cannot be
    // signalled; either way the wait below is bounded.
  }
  return waitForEmptyGroup(pgid, GROUP_KILL_GRACE_MS);
}

/**
 * Runs `playwright test --config <config>` in the workspace and reports whether it passed.
 *
 * An omitted `config` means `playwright.config.ts`; a blank value, or one with
 * control or line-separator characters, fails with a usage error before
 * Playwright starts. Playwright runs without a shell, in the workspace root,
 * with inherited stdio and the caller's environment minus `DEBUG`,
 * `DEBUG_FILE` and `NODE_DEBUG`, whose debug loggers print bearer tokens.
 *
 * On POSIX, SIGINT, SIGTERM, SIGHUP and SIGQUIT received while Playwright
 * runs are forwarded to it, and the Architect teardown sends SIGTERM if the
 * run is stopped first. Playwright leads its own process group there, and a
 * stop signals the whole group: the CLI, its workers and the processes they
 * start. Global setup runs each `docker compose` command in a process group
 * of its own instead, and stops it itself before Playwright exits. Once the
 * CLI has exited, the builder gives Playwright's group a bounded,
 * best-effort drain: it checks every 100 ms for up to 10 s whether the group
 * is empty, then sends SIGKILL to what remains and waits at most 1 s more. A
 * group still not empty then fails the run with a message naming the
 * process group. On Windows, where Node's `kill`
 * terminates a process forcefully whatever the signal, ng forwards nothing:
 * Ctrl+C (SIGINT), Ctrl+Break (SIGBREAK) and closing the console window
 * (SIGHUP) reach Playwright directly, as it shares ng's console, so global
 * setup can stop a Compose command it runs before Playwright exits. ng only
 * records the signal and waits for Playwright's exit, which it reports; a
 * closed console window still ends both shortly after, as Windows ends every
 * process attached to it. Only the Architect teardown ends the Playwright
 * process there, with SIGTERM. A run that ends without a stop is reported as
 * soon as the CLI exits.
 *
 * @param {{ config?: string }} options The validated builder options. `config` is the
 *   Playwright configuration path, relative to the workspace root.
 * @param {import('@angular-devkit/architect').BuilderContext} context The Architect
 *   context, which supplies `workspaceRoot`, `logger` and `addTeardown`.
 * @returns {Promise<{ success: boolean, error?: string }>} Resolves `success: true` only
 *   when Playwright exits with code 0 and, after a stop, its process group
 *   emptied. A failure carries a fixed message in `error`, which the Angular
 *   CLI prints. It never rejects.
 */
function runPlaywright(options, context) {
  return new Promise((resolve) => {
    const root = context.workspaceRoot;

    /** @type {import('node:child_process').ChildProcess | undefined} */
    let child;
    // The first stop signal received while Playwright ran; it explains a failed exit.
    let stoppedBy;
    // True from the CLI's exit after a stop until the run settles, while the
    // builder waits for the rest of Playwright's process group. A further stop
    // signal in that time still reaches the processes that are left.
    let draining = false;
    const stop = (signal) => {
      if (child === undefined || settled || (!isRunning(child) && !draining)) {
        return;
      }
      stoppedBy = stoppedBy ?? signal;
      if (!signalRun(child, signal)) {
        logSafely(
          context,
          'warn',
          `Could not send ${signal} to Playwright. Waiting for it to exit.`,
        );
      }
    };
    /**
     * Records a console event ng received on Windows without signalling
     * Playwright, which receives the same event from the console and handles
     * it itself. Forwarding it would terminate Playwright at once, before
     * global setup could stop a Compose command it runs. The recorded signal
     * explains a failed exit.
     *
     * @param {string} signal The signal name, one of `STOP_SIGNALS`.
     */
    const record = (signal) => {
      if (child !== undefined && !settled && isRunning(child)) {
        stoppedBy = stoppedBy ?? signal;
      }
    };
    // The listener for each of `STOP_SIGNALS`: on POSIX it forwards the
    // signal, on Windows it only records it.
    const onStopSignal = SIGNALS_PROCESS_GROUP ? stop : record;

    // The single place the outcome is decided, so only the first outcome
    // counts: a child that fails after starting can emit 'error' and then
    // 'close'. Failure messages are returned in `error` and never logged
    // here, because the Angular CLI prints `error` itself.
    let settled = false;
    let markSettled;
    // Resolves once the outcome is decided; the Architect teardown awaits it.
    const settledRun = new Promise((done) => {
      markSettled = done;
    });
    const settle = (output) => {
      if (!settled) {
        settled = true;
        for (const signal of STOP_SIGNALS) {
          process.off(signal, onStopSignal);
        }
        resolve(output);
        markSettled();
      }
    };

    // Only an omitted option means the default. An explicitly empty value, such
    // as `--config=` or an unset variable expanded into it, is a usage error:
    // falling back would run the default configuration, whose global setup
    // rebuilds the shared acceptance stack.
    const requested = options.config === undefined ? DEFAULT_CONFIG : options.config;
    if (!isUsableConfigPath(requested)) {
      settle({ success: false, error: CONFIG_USAGE_ERROR });
      return;
    }

    let cli;
    try {
      cli = require.resolve('@playwright/test/cli', { paths: [root] });
    } catch (err) {
      settle({
        success: false,
        error:
          `Cannot resolve @playwright/test/cli from ${encodeForLog(root)}` +
          `${describeErrorCode(err)}. Run "npm ci" in the workspace first.`,
      });
      return;
    }

    const config = path.resolve(root, requested);
    logSafely(context, 'info', `Running Playwright Test with ${encodeForLog(config)}`);

    const { env, debugDropped } = withoutDebugLogging(process.env);
    if (debugDropped) {
      logSafely(
        context,
        'info',
        'Debug logging (DEBUG, DEBUG_FILE, NODE_DEBUG) is turned off for this run, ' +
          'because it prints request headers, including bearer tokens.',
      );
    }

    try {
      // No shell, so every argument reaches Node verbatim on every platform and
      // paths with spaces need no quoting. Apart from the debug-logger
      // variables, the environment passes through unchanged, so
      // E2E_REUSE_STACK, E2E_BASE_URL and E2E_API_URL reach the configuration
      // and global setup as the caller set them. On POSIX, `detached` makes
      // Playwright the leader of a new process group, which its workers and
      // the processes they start join, so a stop reaches all of them; global
      // setup runs each `docker compose` command in a group of its own and
      // stops it itself. On Windows it stays attached to ng's console, so a
      // console event reaches it directly. stdio stays inherited, so the
      // output still streams to the terminal, and the child is not unref'd,
      // so ng still waits for it. A SIGKILL sent to ng or to ng's process
      // group cannot be intercepted or forwarded, so it does not reach
      // Playwright's group.
      child = spawn(process.execPath, [cli, 'test', '--config', config], {
        cwd: root,
        stdio: 'inherit',
        env,
        detached: SIGNALS_PROCESS_GROUP,
      });
    } catch (err) {
      // spawn reports most start failures through 'error', but throws for some.
      settle({ success: false, error: `Failed to start Playwright${describeErrorCode(err)}.` });
      return;
    }

    // Stopping ng stops Playwright too. A listener also keeps Node from exiting
    // on the signal, so ng waits for Playwright's exit and reports it. On
    // POSIX, a terminal's Ctrl+C now reaches only ng's process group, and ng
    // forwards it once to Playwright's group. On Windows, a console event
    // reaches every process attached to the console, so Playwright receives
    // it directly and handles it; ng only records it, because forwarding it
    // would terminate Playwright at once, before global setup could stop a
    // Compose command it runs and delete its barrier file.
    for (const signal of STOP_SIGNALS) {
      process.on(signal, onStopSignal);
    }
    context.addTeardown(async () => {
      stop('SIGTERM');
      await settledRun;
    });

    child.on('error', (err) => {
      if (child.pid === undefined) {
        settle({ success: false, error: `Failed to start Playwright${describeErrorCode(err)}.` });
        return;
      }
      // The child is running, so the error came from signalling it. 'close'
      // still decides the outcome, so ng never exits ahead of Playwright.
      logSafely(
        context,
        'warn',
        `Could not signal Playwright${describeErrorCode(err)}. Waiting for it to exit.`,
      );
    });
    child.on('close', (code, signal) => {
      let output;
      if (signal !== null) {
        const name = asSafeToken(signal);
        output = {
          success: false,
          error:
            name === undefined
              ? 'Playwright was terminated by a signal'
              : `Playwright was terminated by signal ${name}`,
        };
      } else if (code !== 0 && stoppedBy !== undefined) {
        output = {
          success: false,
          error: `Playwright stopped after ng test received ${stoppedBy}`,
        };
      } else {
        output = { success: code === 0 };
      }
      if (!SIGNALS_PROCESS_GROUP || stoppedBy === undefined) {
        settle(output);
        return;
      }
      // After a stop, give surviving group members a bounded drain before
      // reporting the outcome; a group that does not empty is reported, not
      // assumed gone.
      draining = true;
      const pgid = child.pid;
      const reportSurvivors = () =>
        settle({
          success: false,
          error:
            `${output.error ?? `Playwright exited after ng test received ${stoppedBy}`}.` +
            ` Processes Playwright started (process group ${pgid}) were still running after SIGKILL;` +
            ' make sure they have ended before running ng test again.',
        });
      drainProcessGroup(pgid, context).then(
        (emptied) => (emptied ? settle(output) : reportSurvivors()),
        reportSurvivors,
      );
    });
  });
}

/**
 * The Angular Architect builder registered as `test` in `builders.json`, which
 * `angular.json` binds to the `test` target, so `ng test` runs Playwright Test.
 */
module.exports = createBuilder(runPlaywright);
